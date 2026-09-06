# Distributed-store contract proof

This is an implementability review for future Redis/Valkey and PostgreSQL adapters. V1 includes **only the in-memory provider**. No remote backend, crash recovery, replication, database, or cross-node runtime has been certified. The following obligations must be implemented and tested before claiming adapter conformance.

## Mapping the public contract to a remote transaction

| Contract element | Remote responsibility |
| --- | --- |
| `RateLimitPartition(PolicyId, Key)` | Collision-safe structured storage identity |
| `RateLimitStoreRequest.OperationId` | Stable identity for retries of this one engine attempt |
| `RateLimitStateSnapshot` | Immutable policy configuration, state version, elapsed timestamps, exact counts/events/decimal tokens |
| `RateLimitStoreContext` | Authoritative shared-domain elapsed TimeSpan ticks plus one UTC observation |
| Pure transition callback | Deterministic decision and staged replacement computed from that snapshot/time |
| `RateLimitStoreResult.Committed` | Return only after the atomic result is known |
| `RateLimitStoreResult.Failed` | Typed non-commit or indeterminate outcome; no provider exception text |

A provider executes read → sample store time → evaluate → conditional commit atomically with respect to competing writers. The callback can execute locally under a transaction/optimistic version check; it need not execute inside the database. It may be recomputed only after a **known non-commit**. Never expose a provisional `Allowed` result before commit.

Encoding must preserve ordinal key identity, including UTF-16 code units, rather than normalizing case/Unicode or silently replacing malformed surrogate sequences during encoding. Use a length-prefixed exact tuple representation; if a hashed physical key is used, retain and verify the original identity in the value. Keep policy ID, algorithm discriminator, explicit persisted schema version, immutable settings, and state in one consistency unit. Validate all hydrated state: queue order/count/latest event, token bounds, fixed-window alignment, timestamp ordering, and recovery conditions. Do not trust a client-supplied snapshot or TTL.

The engine's policy `StateVersion` identifies V1 state. Persist that version in an adapter envelope and reject an unsupported version before constructing a V1 snapshot. The public snapshot is not a prescribed Redis/SQL/JSON format.

## Time across nodes and process restarts

The in-memory process clock cannot be copied into a distributed adapter. The adapter must define one durable, shared epoch and measure all context/state timestamps in TimeSpan ticks from it. Client process clocks and raw Stopwatch ticks from different nodes are invalid inputs.

Redis server `TIME` or PostgreSQL server time can supply the authority. Because these are wall clocks, a provider must document its clock assumptions. Clamp the logical evaluation time to at least the persisted previous time for the locked partition after a backwards jump; never subtract a negative elapsed duration. A forward server-clock jump is an authoritative time advance and may replenish quota, unlike a client UTC jump. If this is unacceptable, the deployment needs a stronger clock policy before the adapter is approved. Metadata UTC can differ from the logical elapsed domain.

Fixed windows use the same epoch across nodes/restarts. Cleanup uses the same time authority and recovery predicate as evaluation. Do not derive TTL from the public UTC `ResetAt`: it is a projection and may be null.

## Redis / Valkey proof

Redis cannot execute an arbitrary C# callback in Lua. A general adapter can use:

1. `WATCH` the complete partition key.
2. Read and validate the state, obtain server `TIME`, and construct context in the shared logical domain.
3. Invoke the pure callback locally.
4. Issue `MULTI` / `EXEC` with the replacement and any retention metadata.
5. Return the result only after confirmed success; a watch conflict is a known non-commit and permits bounded recomputation with the same operation ID.

A denied result still needs the watched transaction validated so policy conflicts or concurrent updates cannot be ignored. A no-change operation can use a conservative conditional rewrite or another validated no-op transaction pattern.

All atomic state must live in one key, or keys guaranteed to share a cluster hash slot. Supporting an optimized Lua/function path requires deliberately reimplementing and independently certifying the algorithm; it is not serialization of the C# callback. Limit retry count and return `ContentionExhausted` on repeated known conflicts.

Safest V1 adapter design is to retain state without expiration while debt is active and delete it only under the same atomic recovery check. A future TTL optimization must prove it never expires earlier than full recovery, including TTL precision rounding, clock changes, policy changes, and sliding latest-event expiry. Null/unrepresentable recovery forbids an expiry estimate. Redis eviction must not discard active quota; use a suitable dedicated/noeviction configuration and map capacity errors safely.

A disconnect after `EXEC` transmission is `IndeterminateCommit`, even if the caller cancels. Do not call it `StoreUnavailable`, fail open, or automatically repeat the mutation. A future adapter may atomically record an operation receipt with state and reconcile it internally; V1 does not require a receipt service or expose a public reconciliation API.

Cross-node correctness requires all writers and cleanup to use this transaction protocol on the same authority. Redis persistence, acknowledged-write loss during failover, and replication settings are deployment assumptions: atomicity on a primary does not itself establish durable quota across failover.

## PostgreSQL proof

Use a unique constraint on the exact partition identity and a transaction:

1. Insert the missing row with a conflict-safe pattern; resolve concurrent creation under that unique constraint.
2. Acquire `SELECT ... FOR UPDATE` on the row before reading/evaluating active state.
3. Sample authoritative database time after obtaining the lock, clamp/convert to the shared logical domain, and run the pure callback.
4. Persist the replacement/state version and commit.
5. Return the decision only after confirmed commit.

`clock_timestamp()` sampled after locking avoids using a transaction-start timestamp that became stale while waiting. Serialization failures or known rolled-back unique/lock conflicts may use a bounded retry loop with the same operation ID. A connection loss during `COMMIT` is indeterminate; an ordinary retry could double-consume. A future receipt in the same transaction can support internal reconciliation without changing `IRateLimiter`.

Cleanup must obtain the same row lock, resample database time, and verify full recovery before deleting. It cannot blindly delete by an old timestamp read outside the lock. JSON or typed columns are provider choices; neither may lose decimal token precision or queue ordering/count. Exact column collation/encoding must match ordinal key identity rather than a database's case-insensitive default.

## Failure, cancellation, and crash proof obligations

| Event | Required outcome |
| --- | --- |
| Known pre-commit connection failure | `StoreUnavailable`; fail-open allowed only by explicit policy, no recorded usage |
| Callback failure or caller cancellation before commit | No quota consumption; typed invalid state or caller cancellation as appropriate |
| Known contention rollback | Bounded recomputation, same operation ID |
| Lost commit acknowledgement / exception after transmission | `IndeterminateCommit`, always blocked, no blind retry |
| Process crash after successful commit | Permit remains consumed even if response was never received |
| Changed configuration for active policy ID/key | `PolicyConflict`, no implicit reset |
| Full capacity with active debt | Typed capacity failure; no early eviction |
| Unknown persisted algorithm/version or malformed state | `InvalidState`, no fabricated allowance |

Independent `EvaluateAsync` calls receive new IDs and are never promised deduplication. The contract gives at-most-one mutation per correctly implemented attempt; it does not give exactly-once business operations, availability during a partition, or distributed consensus.

Before an adapter ships, CI must exercise multiple nodes against the real backend, concurrent missing/existing keys, contention retry exhaustion, persistence/restart, policy conflicts, backwards/forwards authoritative time, encoding collisions, active-debt cleanup races, noeviction/capacity behavior, and disconnect/cancellation immediately before and after commit.

This design follows [Redis transactions](https://redis.io/docs/latest/develop/using-commands/transactions/), [WATCH](https://redis.io/docs/latest/commands/watch/), [clustered multi-key operations](https://redis.io/docs/latest/develop/using-commands/multi-key-operations/), [PostgreSQL row locks](https://www.postgresql.org/docs/current/explicit-locking.html), and [serialization failure semantics](https://www.postgresql.org/docs/current/transaction-iso.html). These sources establish backend primitives; they do not certify an unimplemented BKE adapter.

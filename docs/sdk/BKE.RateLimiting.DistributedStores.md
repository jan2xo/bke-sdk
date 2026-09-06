# Distributed-store proof for BKE.RateLimiting

This document is a design review and adapter proof obligation for future Redis/Valkey and PostgreSQL adapters. It is not runtime certification and V1 ships only the in-memory provider. The claims below remain planned until an adapter and fault-injection evidence exist in CI.

## Required atomic shape

The store operation receives a structured `(PolicyId, Key)` identity, authoritative store time, a state snapshot, and a pure transition callback. It returns the transition result only after the replacement state is durably committed. The callback must be deterministic and side-effect free. The adapter must expose conflict, unavailable, capacity, and indeterminate-commit outcomes as typed failures.

The contract carries an operation identity separately from key identity. It is retained across known contention retries, but independent engine calls are not deduplicated and consumers receive no exactly-once promise. A caller must not infer that a timeout means the permit was not consumed. When commit outcome is unknown, the adapter returns `IndeterminateCommit`; this always blocks, and the adapter never retries blindly.

## Redis / Valkey recipe

Redis/Valkey cannot execute the SDK's arbitrary C# pure callback. A portable adapter should `WATCH` the single state key, read the state, obtain server time with `TIME`, run the pure transition locally, then `MULTI`/`EXEC` a conditional replacement. A changed watch key is a known conflict and may be recomputed. An optional Lua/function implementation may perform the complete algorithm only when the adapter has deliberately reimplemented and certified that algorithm; Lua is not a way to serialize a C# callback. Use one key or a same-slot hash tag for the whole atomic partition; cross-slot transactions are not atomic.

Use Redis/Valkey server time (or a documented monotonic server-side time strategy) as the authoritative store domain; do not combine client timestamps from different nodes. Store policy version and algorithm state together. Set TTL only when the state is fully recovered and idle, or to a conservative upper bound that cannot restore a permit through eviction. Sliding-window events and token fractional state must be represented without lossy integer conversion. Adapter certification must state assumptions about eviction policy, replication/failover durability, and the consistency level accepted during failover.

Retries are safe only when the script reports a known conflict before mutation. A connection timeout after the server accepted the script is indeterminate. The adapter must return that state, preserve the operation identity, and offer reconciliation. Network retries must not create a second permit.

## PostgreSQL recipe

Use one transaction with a deterministic keyed row lock (`SELECT ... FOR UPDATE`) or a serializable compare/update loop. Missing rows must be created under the same uniqueness constraint and lock ordering as existing rows; concurrent insert races are retried as known conflicts. Use database time (`clock_timestamp()` or a transaction-consistent documented alternative), clamp backwards UTC projections in the adapter's store time domain, and persist policy identity/version plus JSON or typed algorithm state together. A serialization failure is a known non-commit and may be retried with the same operation identity. A lost connection during commit is indeterminate; do not repeat the mutation without reconciliation.

Partition cleanup must not delete a row with active debt or an unexpired sliding event. A cleanup job may delete only a row whose algorithm-specific recovery condition is true and whose lock is held. TTL and cleanup are storage concerns, but their safety condition is part of the adapter's conformance proof.

## Cross-node and crash semantics

All nodes must use the same authoritative time domain and atomic state owner. Process crash before commit consumes nothing; crash after commit consumes the permit even if the client did not receive the response. Policy-version mismatch is a typed conflict. Fail-open may produce an `Allowed` result with a typed storage failure and unknown usage fields; it must not claim a remaining count. Fail-closed produces `Blocked` with a safe typed error.

These rules establish implementability without claiming exactly-once delivery or availability during a partition. Adapter certification must include fault injection around timeout-after-commit and process restart.

The Redis transaction and optimistic-locking model described here follows the [Redis transactions documentation](https://redis.io/docs/latest/develop/using-commands/transactions/) and its [WATCH command reference](https://redis.io/docs/latest/commands/watch/). Same-slot requirements for clustered multi-key operations are covered by the [Redis multi-key operations documentation](https://redis.io/docs/latest/develop/using-commands/multi-key-operations/). The PostgreSQL lock and retry guidance follows [explicit row locking](https://www.postgresql.org/docs/current/explicit-locking.html) and [transaction isolation/serialization failures](https://www.postgresql.org/docs/current/transaction-iso.html).

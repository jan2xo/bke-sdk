# BKE.RateLimiting

`BKE.RateLimiting` 0.1.0 is an independent .NET 10 package providing capability `bke.rate-limiting`, contract version 1. It has no package dependencies, HTTP requirement, network calls, logging infrastructure, database, or product-specific rules.

## Ownership and public entry point

The caller owns authentication, authorization, key selection, application policy, responses, and the store lifetime. Rate limiting is an admission decision; an allowed request does not authorize a license, account, or protected resource.

```csharp
using BKE.RateLimiting;

// Keep the store alive across requests. A new store starts with fresh quota.
using var store = new InMemoryRateLimitStore(
    maxPartitions: 10_000, maxRetainedEvents: 100_000);
IRateLimiter limiter = new BkeRateLimiter(store);
var policy = RateLimitPolicy.FixedWindow("operation-v1", 10, TimeSpan.FromMinutes(1));

RateLimitResult result = await limiter.EvaluateAsync(
    new RateLimitRequest("host-selected-opaque-subject", policy), cancellationToken);
if (result.Decision == RateLimitDecision.Allowed)
{
    // The host performs its already-authorized action.
}
```

Multiple limiter instances share quota when they share the same store instance. Different stores and process restarts start independent quota. The limiter does not dispose the caller's store.

Keys are nonempty opaque strings, at most 4096 UTF-16 code units, with ordinal comparison and no normalization. Case variants, composed/decomposed Unicode, and whitespace are distinct. The partition is a structured `(PolicyId, Key)` pair, never a delimiter-joined string. Policy IDs are nonblank strings of at most 256 code units; hosts should use stable, non-sensitive names.

The active partition retains the complete immutable policy configuration, including failure mode. A different configuration under the same ID/key produces `PolicyConflict` while existing debt is active. A fully recovered partition can accept a changed configuration. A new policy ID deliberately creates independent quota; hosts must not change IDs on every request.

## Algorithms and bounds

| Policy | Accounting | Recovery |
| --- | --- | --- |
| Fixed window | O(1) count; windows aligned to the store's elapsed-time origin | Next window boundary |
| Sliding window | Exact accepted-event queue in `(now - window, now]`; boundary events expire | One permit at oldest event expiry; full allowance at latest event expiry |
| Token bucket | Starts full; one token per acceptance; decimal internal fractional refill, capped at capacity | One permit at the next whole token; full recovery at capacity |

Allowance/capacity must be 1–1,000,000. Window/replenishment period must be positive and at most 365 days. Token refill is a finite `double` from 0.000001 through 1,000,000 tokens per period, converted to decimal for accounting. Refill delays round up to TimeSpan ticks. Invalid inputs throw argument exceptions at construction; ordinary decisions and store failures use typed results.

V1 provides no queueing, weighted requests, progressive penalties, reputation, CAPTCHA, or account lockout.

## Results and optional observations

`Task<RateLimitResult> EvaluateAsync(RateLimitRequest, CancellationToken)` returns:

| Field | Meaning |
| --- | --- |
| `Decision` | `Allowed`, `Throttled`, or `Blocked` |
| `PolicyId` | The caller-selected policy name; no key is included |
| `Remaining` | Whole permits after the evaluation, or null when unknown |
| `RetryAfter` | Zero for a recorded acceptance; positive delay to one permit for a throttle, or null when unknown/unrepresentable |
| `ResetAt` | UTC projection of full recovery without further consumption, or null when unknown/unrepresentable |
| `ObservedAt` | UTC observation associated with the decision |
| `Failure` | Optional safe enum; no exception text |
| `UsageRecorded` | True only for an ordinary acceptance that recorded one consumption |

An `Allowed` result with `Failure == StoreUnavailable` and `UsageRecorded == false` is explicitly uncounted fail-open admission. It has null remaining/retry/reset fields. A normal `Allowed` result always consumes exactly one permit; its zero retry delay describes the current admission, not a guarantee about a subsequent request.

Known retry/reset values are nonnegative/ordered. Unrepresentable timestamps and durations stay null; they are not fabricated maximum-value projections. UTC may jump backwards or forwards, so timestamps are observations, not monotonic sequence numbers. Quota uses elapsed store time and is unaffected by UTC jumps. Inject `TimeProvider` into the store for deterministic tests. The limiter's optional clock supplies metadata only for unexpected adapter exceptions.

The result itself is the optional observation contract: applications can count decisions, failures, and recorded admissions without a sink, `ILogger`, or telemetry dependency. Request/partition debug strings redact the key. Hosts must still avoid logging the explicitly accessible `Key` property, secrets in `PolicyId`, or their own sensitive request objects.

## Atomicity, cancellation, and failures

The in-memory store owns one semaphore protecting time capture, state read, pure transition, capacity checks, commit, and cleanup. The immutable staged state is installed only after the final pre-commit cancellation check. Callback failure and pre-commit cancellation do not consume quota; maintenance may reclaim already fully recovered partitions. A committed result is returned even if cancellation arrives afterwards.

The adapter port is `IRateLimitStore.ExecuteAsync(RateLimitStoreRequest, Func<RateLimitStoreContext, RateLimitStoreTransition>, CancellationToken)`. The snapshot exposes immutable policy/version and algorithm state. Context time is in elapsed **TimeSpan ticks of a shared store domain**, not raw Stopwatch ticks or caller UTC. The synchronous callback is pure and must not do I/O or reenter the store.

Only a known pre-commit `StoreUnavailable` may honor `FailOpen`. The default is `FailClosed`. `PartitionCapacityExhausted`, `StateCapacityExhausted`, `PolicyConflict`, `InvalidState`, `ContentionExhausted`, and `IndeterminateCommit` always block, even under a fail-open policy. Unexpected adapter exceptions map to indeterminate commit, never an ordinary outage.

An adapter may recompute the pure callback after a known non-commit conflict while retaining `OperationId`. A lost commit acknowledgement must not cause a blind retry. Each independent engine call gets a new operation ID; there is no public deduplication or exactly-once promise. Throwing caller cancellation guarantees non-commit; cancellation after an uncertain remote write must be reported as indeterminate instead.

Custom stores are trusted adapters. Public snapshots are an adapter contract, not authenticated state from arbitrary clients. Adapters must validate persisted encoding/version and full state invariants when hydrating data, and preserve atomicity and time semantics. Built-in scalar checks are not a substitute for provider conformance.

## Bounded memory and operation

Default limits are 10,000 partitions and 100,000 retained sliding events across the store. A new partition at capacity first triggers a scan for fully recovered partitions. If capacity remains exhausted it returns a typed blocked result, without evicting active debt. Event-budget exhaustion likewise blocks a state replacement. Hosts select smaller or larger limits for their workload.

Evaluations reclaim the current fully recovered partition; capacity pressure also scans the store. Hosts may call `PruneExpiredAsync` explicitly for idle cleanup and `GetStatisticsAsync` for partition/event counts. There is no timer or background service. Cleanup and evaluation share the same lock, so cleanup cannot manufacture permits. An unrepresentable future recovery timestamp prevents automatic reclamation until a later evaluation establishes a representable safe recovery point.

The security tradeoff is explicit: high-cardinality keys can fill the configured bound and deny admission to new subjects, but they cannot force silent active-state eviction. Memory depends on configured bounds and key sizes. A full cleanup scan is O(partition count); the global lock serializes unrelated keys. The [certification report](BKE.RateLimiting.Certification.md) records measured limits and remaining performance coverage.

## API freeze conclusions

The 0.1.0 review retained a store-owned atomic transition, immutable snapshots with algorithm/version data, distinct committed/failure outcomes, operation identity for known-conflict retries, nullable unknown timing metadata, and failure-aware consumption metadata. These allow future adapters without changing `IRateLimiter`; adapter serialization, durability, and fault handling still require their own certification.

The policy hierarchy is intentionally closed to consumer-defined algorithms. Future library algorithms can be added, but hosts must handle unknown enum values conservatively and adapters must reject unsupported persisted algorithm/state versions. The core makes no wire-format or automatic state-migration guarantee. Multi-policy calls remain independent and have no rollback.

See the [distributed-store proof](BKE.RateLimiting.DistributedStores.md), [ASP.NET design](BKE.RateLimiting.AspNetCore.md), [five integration recipes](BKE.RateLimiting.Recipes.md), and [certification evidence](BKE.RateLimiting.Certification.md).

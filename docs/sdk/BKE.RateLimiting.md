# BKE.RateLimiting

`BKE.RateLimiting` is the product neutral, HTTP free rate limiting capability for BKE applications. Version `0.1.0` targets .NET 10 and exposes capability `bke.rate-limiting`, contract version 1.

## Boundary and contract

The caller owns key selection, authentication, proxy trust, HTTP status mapping, logging, and persistence policy. A key is opaque and compared with ordinal semantics; the library must not normalize, concatenate, or interpret it. `PolicyId` is caller selected and is part of the state identity. A policy change for an active key is a typed conflict, never an implicit reset.

The public consumer shape is:

```csharp
Task<RateLimitResult> EvaluateAsync(
    RateLimitRequest request,
    CancellationToken cancellationToken = default);
```

`RateLimitRequest` contains the opaque key and immutable policy. Policies are `FixedWindow`, `SlidingWindow`, or `TokenBucket`; construction rejects invalid values. Each `Allowed` result consumes exactly one permit. Normal decisions are returned as typed results (`Allowed`, `Throttled`, or `Blocked`). Storage and policy failures are represented by safe typed errors; raw keys, exception messages, and sensitive caller data are never echoed.

`RetryAfter` means the delay until one permit can be granted without another consumption. `ResetAt` is the full recovery projection. Unknown values remain null. UTC is used for output; elapsed calculations are monotonic within a store operation. Cancellation propagates before commit and never changes a fail-open decision.

## In-memory store

The in-memory implementation is the required V1 provider. A shared store instance shares quota across limiter instances. Its atomic operation must cover state read, time capture, pure transition, commit, and pruning. A cancellation or callback failure before commit leaves committed state unchanged.

Partition limits are a security boundary. Fully recovered idle partitions may be reclaimed; active debt, throttled state, and incompletely refilled token buckets must not be silently evicted because eviction would create permits. Capacity exhaustion is therefore an explicit typed failure and follows the policy failure mode.

The store contract deliberately does not promise exactly-once behavior for a remote adapter. An adapter may recompute a pure transition after a known contention conflict while retaining the same `OperationId`, but must never blindly retry after `IndeterminateCommit`. Independent `EvaluateAsync` calls are never deduplicated. `RateLimitStoreResult` distinguishes committed results from failures. Only a known pre-commit `StoreUnavailable` can produce a fail-open, uncounted `Allowed`; indeterminate commit always blocks.

## Safe usage

```csharp
var request = new RateLimitRequest(
    key: "login:principal-and-ip", // application-owned opaque identifier
    policy: RateLimitPolicy.FixedWindow("login-v1", 10, TimeSpan.FromMinutes(1)));

RateLimitResult result = await limiter.EvaluateAsync(request, cancellationToken);
switch (result.Decision)
{
    case RateLimitDecision.Allowed:
        // perform the operation
        break;
    case RateLimitDecision.Throttled:
        // caller chooses its response; use result.RetryAfter when present
        break;
    case RateLimitDecision.Blocked:
        // fail closed or handle a typed conflict/capacity failure
        break;
}
```

Do not use wall-clock sleeps in tests. Inject `TimeProvider` and advance a fake provider. Do not use a raw email address, IP address, or API key as telemetry metadata unless the host has separately made that decision.

See [distributed-store design](BKE.RateLimiting.DistributedStores.md), [ASP.NET design](BKE.RateLimiting.AspNetCore.md), and [integration recipes](BKE.RateLimiting.Recipes.md).

## API freeze review

Before freezing 0.1.0, verify that the store callback's time value is explicitly store-domain time, not a process-created `DateTimeOffset`, and that the callback result separates a replacement state from a decision/result. The atomic method must make the commit boundary observable enough for adapters to distinguish a known conflict from an indeterminate commit. Include a separate operation identity only if the public contract intends future idempotent reconciliation; key identity alone cannot provide it.

The contract makes policy identity part of the partition key and persists the immutable policy snapshot/version with state. A remote adapter cannot safely implement policy conflict, TTL, or retry behavior if those fields are private to `BkeRateLimiter`. These are required design properties for future Redis/Valkey/PostgreSQL support, although no remote adapter is included in V1. The in-memory store also exposes bounded pruning and statistics for lifecycle certification.

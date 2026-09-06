# BKE.RateLimiting 0.1.0 engineering certification

## Scope and reconciled baseline

Canonical repository: [jan2xo/bke-sdk](https://github.com/jan2xo/bke-sdk). Verified default branch `main` started at `570972bf22ceb83342913487fa0d6a4fb48a9687`. The implementation branch is `feat/rate-limiting-v1`; merge and public NuGet publication require separate authorization.

Baseline inventory was four independent .NET 10 packages: Desktop.Client 2.0.0, Desktop.Licensing 2.0.0, Updater 0.4.0, Notifications 0.4.0. There was no shared Core/Abstractions project to reuse. Global SDK selection was 10.0.100 with latestPatch. Baseline [full certification 34008249214](https://github.com/jan2xo/bke-sdk/actions/runs/34008249214) and [Notifications 34008249227](https://github.com/jan2xo/bke-sdk/actions/runs/34008249227) were green, with 83 existing tests.

The new package follows existing project/package/test conventions and adds one independent `net10.0` package. Existing SDK implementations and versions are unchanged. `BKE.SDK.sln`, package inventory/contract documentation, and full certification include RateLimiting; the focused Desktop solution remains unchanged.

All restore/build/test/pack/consumer/benchmark execution occurred on GitHub Actions. Local files were patch staging only. Three requested GPT-5.6 Luna agents at low reasoning covered implementation, tests, and review/benchmark/documentation with separate file ownership; one integration owner reconciled corrections.

Before adding a custom engine, the review inspected the .NET runtime's [token-bucket limiter](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Threading.RateLimiting/src/System/Threading/RateLimiting/TokenBucketRateLimiter.cs) and [sliding-window limiter](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Threading.RateLimiting/src/System/Threading/RateLimiting/SlidingWindowRateLimiter.cs). Their internal state/time ownership does not supply the requested interchangeable atomic store boundary and exact trailing-window contract. This package keeps the small explicit engine/store separation. New workflow actions use verified official action commit pins.

## Verification inventory

The current suite has **50 cases** (48 in the first green implementation plus two final date-overflow cases). It includes 12,000 deterministic generated operations; those operations are assertions within tests, not 12,000 separately discovered test cases.

| Area | Evidence |
| --- | --- |
| Fixed-window model | Seeds 0, 1, 7, 31; 1,000 operations each, independent window/count model |
| Sliding-window model | Seeds 11, 29, 47, 83; 1,000 operations each, independent trailing-event model |
| Token-bucket model | Seeds 101, 202, 303, 404; 1,000 operations each, integer reference units for fractional refill |
| Concurrency | 100 simultaneous tasks per algorithm, two limiters sharing one store, exactly 17 acceptances |
| Key/policy boundaries | Empty/giant keys, case/Unicode distinctions, structured-tuple separator collision, independent partitions, active policy conflicts |
| Time/numeric boundaries | Limit one, exact rollover, UTC jumps, monotonic rollback, fractional refill, burst saturation, absurd bounds, retry/reset overflow |
| Cancellation/commit | Pre-cancel, waiting at the real store gate, cancellation after transition/before commit, callback throw, post-commit exception without retry |
| Lifecycle | Partition cap, global sliding-event cap, no active-debt eviction, exact full-recovery pruning, safe token/sliding cleanup |
| Failure/privacy | Explicit fail-open/closed outage, always-blocked non-outage failures, uncounted admission, redacted keys and exception messages |
| Package | Identity/version/TFM/license/README/XML doc inspection; no dependency group packages; SHA-256 |
| Blank consumer | Temporary directory, only generated nupkg feed, fresh NuGet cache, no project references, public API compile and execution |
| Existing family | Client 27 + Licensing 7 + Updater 29 + Notifications 20 = 83 tests |

Every generated operation checks decision/remaining against its independent model and nonnegative/ordered known timing. The finite deterministic matrix is regression and invariant evidence, not an exhaustive proof over all schedules or values.

### Recorded first qualification

Source `1f2528ffb53d1eedfd166d9c7e83c36429c3e9be` passed:

- [Dedicated RateLimiting run 34040433847](https://github.com/jan2xo/bke-sdk/actions/runs/34040433847), job 101506069994: 48/48 tests, restore, Release build, pack, isolated consumer, benchmark.
- [Full SDK run 34040433731](https://github.com/jan2xo/bke-sdk/actions/runs/34040433731), job 101506069484: 131/131 family tests and five packages.
- Zero build warnings and errors.
- Blank-consumer marker: `BLANK_CONSUMER_PASS`; first allowed, second throttled with 60-second retry, fake-time rollover allowed, explicit uncounted outage admission, public namespaces only.
- [Artifact 9991500945](https://github.com/jan2xo/bke-sdk/actions/runs/34040433847/artifacts/9991500945), name `BKE.RateLimiting-0.1.0-certification`, including the nupkg, TRX, consumer/package proof, and benchmark files.
- First-qualification nupkg SHA-256: `0b4d09148c6c46c802e41a8493006c4f1fe5e1860829e9d05daea40b1d15228c` (26,768 bytes).
- Artifact archive SHA-256: `a826cadde491d99e3b99d156dd2e2c6dfe11993ebadc354a53b08445ad238d1c`.

The final API review after that run changed unrepresentable time metadata to null and added two test cases. Therefore the hashes and measurements above identify that recorded qualification, **not an assertion that later package bytes are identical**. The implementation PR carries final candidate SHA, fresh run IDs, final 50/133 test counts when confirmed, and the final artifact/hash. The dedicated workflow prints `NUGET_SHA256`, runner identity, benchmark JSON, and the checked-out SHA so each artifact can be traced to its run.

## Benchmark evidence

Recorded on the first qualification above: Ubuntu 24.04.4 LTS, .NET 10.0.11, four reported processors, concurrency four. Each of 12 scenarios ran three times with 50,000 operations per repeat: **36 rows, 1,800,000 measured evaluations**. All measured operations were admitted. Many-key scenarios used 1,000 partitions. Policies had ample quota and the store clock was frozen; these are admitted-path throughput measurements, not expiry or throttled-path load tests.

Keys/requests were prepared before measurement. Each repeat warmed up 128 evaluations in a separate store. Stopwatch latency surrounds each evaluation; throughput includes the harness/parallel driver. Allocation uses process-wide allocated bytes over the measured interval, including task/locking/driver overhead. Report construction/sorting occurs afterwards.

The following are medians across three repeats. Latency columns are the median of each repeat's percentile, not pooled percentiles.

| Algorithm | Keys | Mode | Ops/sec | p50 µs | p95 µs | p99 µs | Allocated bytes/op |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| fixed | single | sequential | 393,472 | 2.13 | 3.05 | 6.85 | 632.0 |
| fixed | single | concurrent (4) | 218,991 | 16.93 | 19.88 | 30.74 | 1256.0 |
| fixed | many | sequential | 590,510 | 1.56 | 2.15 | 5.78 | 634.1 |
| fixed | many | concurrent (4) | 401,651 | 8.64 | 12.08 | 21.89 | 1256.6 |
| sliding | single | sequential | 597,171 | 1.36 | 1.94 | 2.55 | 704.0 |
| sliding | single | concurrent (4) | 509,613 | 6.04 | 8.51 | 12.12 | 1326.5 |
| sliding | many | sequential | 639,023 | 1.25 | 1.81 | 2.34 | 706.1 |
| sliding | many | concurrent (4) | 514,516 | 6.13 | 8.83 | 18.27 | 1326.3 |
| token | single | sequential | 594,120 | 1.56 | 2.13 | 2.34 | 632.0 |
| token | single | concurrent (4) | 557,542 | 6.37 | 8.97 | 17.93 | 1254.6 |
| token | many | sequential | 633,489 | 1.40 | 1.98 | 3.10 | 634.1 |
| token | many | concurrent (4) | 543,910 | 6.62 | 8.78 | 14.34 | 1257.5 |

### Global-lock decision

The single semaphore is retained for V1 because it gives a small, auditable atomic boundary. On these hosted-runner samples, concurrent throughput was approximately 219k–558k ops/sec with median-repeat p99 of 12–31 µs. Concurrency does not scale linearly: fixed/single throughput was approximately 44% below its sequential sample, and other scenarios also showed overhead. Different keys still contend on this lock.

No throughput SLO was supplied. This evidence does not establish a production capacity guarantee, a CPU architecture comparison, or an unacceptable pathological stall for the measured workload. It supports keeping the simple lock while explicitly recording the boundary. A later high-throughput consumer should benchmark its own key distribution, concurrency, cancellation, refill/expiry, and latency target before proposing sharding.

The benchmark is a small characterization harness, not a statistically isolated BenchmarkDotNet study. Tiered JIT, scheduling, scenario order, and process-wide allocations influence results. Saturated capacity scans and very high concurrency are not measured here; repeated attacker-generated keys at capacity can incur O(partition-count) scans. No speculative lock redesign was made.

### Memory and cleanup evidence

The lifecycle probe attempted 1,001 fixed-window partitions with a configured maximum of 1,000. Exactly one request was blocked; statistics showed 1,000 partitions. Approximate retained process-memory increase was **230,408 bytes**. After advancing deterministic time past recovery, explicit prune reduced partitions/events to zero and a new request was allowed.

This is a measured 1,000-key sample, not a formula for maximum memory. Key sizes and configured limits matter. Per-scenario process-memory deltas can be negative because GC/JIT liveness changes which harness objects survive each snapshot; they must not be interpreted as negative store memory or memory savings. The dedicated lifecycle sample and bounded-state tests provide the useful lifecycle evidence.

Sliding memory is independently bounded by the configurable global retained-event budget; tests prove event-budget exhaustion preserves existing quota and capacity is reclaimed after recovery. Active throttled state and incomplete token refill cannot be evicted early. Cleanup shares the evaluation lock, so prune/evaluate interleavings cannot bypass the commit boundary.

## Adversarial findings and API corrections

The integrated implementation includes these review-driven corrections:

- **Ambiguous identity:** structured policy/key partitions replace delimiter collision risk; exact ordinal semantics and key bounds are tested.
- **Time-domain confusion:** the store owns elapsed TimeSpan ticks, shared across limiters; caller UTC is observation metadata only.
- **Failure-mode confusion:** only known pre-commit unavailability may fail open. Unknown/post-commit exceptions block as indeterminate and are never retried by the engine.
- **False accounting:** uncounted fail-open admission exposes a typed failure and null quota facts; `UsageRecorded` distinguishes real consumption.
- **Unsafe cleanup:** only full algorithm recovery authorizes pruning. Sliding latest-event expiry and token debt determine recovery, with partition and event bounds.
- **Numeric/algorithm traps:** fixed-window O(1) accounting, persistent sliding queue instead of per-call full copying, decimal token refill, saturation before multiplication, and ceil-rounded delays.
- **Overflow projection:** independent freeze review found that clamping a date/duration to its maximum could claim a known recovery time. Final factory/evaluator changes keep unrepresentable `ResetAt`/`RetryAfter` null and retain active debt. Two near-date-maximum fixed/sliding cases supplement the slow-refill overflow test.
- **Distributed API feasibility:** snapshots carry immutable policy/version, store-domain time and state; typed commit outcomes and per-attempt operation IDs allow optimistic/transactional adapters without promising exactly-once operation delivery.
- **Consumer policy hazards:** recipes use host-owned identity, safe composite keys, short-circuiting independent limits, explicit earlier-permit consumption, and avoid an unauthenticated account-only lockout.

These findings were corrected before freezing the candidate, without adding an HTTP adapter or production database provider.

## Scope limits and next action

Engineering evidence covers portable .NET 10 code on the Actions Linux runner. It does not certify Windows/macOS GUI behavior, actual Air Stack/Render Dock/Agent/Worker integration, an ASP.NET middleware package, a Python bridge, production traffic, or distributed storage. Those are explicit future consumer/provider tasks.

The in-memory provider intentionally loses quota on process restart and is unsuitable for quotas that must survive restart or span processes. A host can fill configured capacity; the store blocks new admissions instead of creating permits through eviction. Custom stores and supplied clocks are trusted infrastructure and must satisfy their documented contract.

Redis/Valkey and PostgreSQL are [design/conformance proofs](BKE.RateLimiting.DistributedStores.md) only; ASP.NET is [adapter design](BKE.RateLimiting.AspNetCore.md) only. There is no public deduplication/reconciliation API, unknown-state migration, weighted permit support, cross-policy atomic transaction, account lockout, telemetry service, or progressive penalty engine.

Next action is owner review of the final PR and its exact candidate evidence. No merge, deployment, release, public NuGet publish, or production operation is part of this implementation.

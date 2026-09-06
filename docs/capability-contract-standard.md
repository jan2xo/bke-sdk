# BKE Capability Contract Standard

Every reusable capability documents:

- **CAPABILITY**
- **IDENTITY / VERSION**
- **WHAT I NEED**
- **INPUT**
- **PROCESS** (private implementation)
- **OUTPUT**
- **WHAT I GIVE**
- **ERRORS**
- **SECURITY BOUNDARY**
- **SUPPORTED ADAPTERS**

Contracts are typed, product-neutral, async-friendly, and transport-independent. Consumers depend on interfaces and data contracts, never authority-bearing implementation details. Transport, persistence, UI, privileged execution, and remote providers are adapters outside the portable contract.

## Permanent BKE rule

```text
WHAT I NEED
    ↓
INPUT
    ↓
PROCESS / PRIVATE IMPLEMENTATION
    ↓
OUTPUT
    ↓
WHAT I GIVE
```

A capability contract describes what a module needs and gives. It does not expose how the trusted provider performs the work.

## Current execution model

```text
bke-sdk
├── BKE.Updater       (code, tests, NuGet, CI)
├── BKE.Notifications (code, tests, NuGet, CI)
├── BKE.RateLimiting  (engine, in-memory store, tests, NuGet, CI)
└── Full SDK Certification (deliberate only)
```

Air Stack and Render Dock are proof consumers; they are not capability owners.

## Contract invariants

Public contracts should make contradictory states difficult or impossible to construct.

Examples:

- an `UpToDate` updater result cannot also claim an available version
- an `UpdateAvailable` result requires an available version
- a failed updater result requires a typed error
- publish status is separate from notification lifecycle state
- provider-generated notification identity is not supplied by the publishing consumer
- logical notification actions expose identifiers and labels only, never executable authority

Contract tests should lock these invariants before provider implementation begins.

## Updater contract

```text
CAPABILITY: bke.updates.check
CONTRACT VERSION: 1

WHAT I NEED
- product identity
- current version
- optional specifically requested version

WHAT I GIVE
- UpToDate
- UpdateAvailable
- Deferred
- Failed
- typed failure classification
```

`CheckAsync` is an update-check capability only. Downloading, installation, privilege elevation, signature authority, and install-target selection are separate capabilities/providers and must not be smuggled into a check intent flag.

The consumer-facing updater contract does not accept signing keys, trust stores, executable or installer paths, installation roots, privileged helper selection, trusted URLs, policy authority, signing authority, entitlement authority, or update authorization authority. A trusted provider adapter owns those decisions.

Updater failures must remain distinguishable enough for deterministic troubleshooting. The portable contract currently distinguishes invalid request, provider unavailable, transport failure, protocol failure, malformed response, verification failure, policy denial, and unknown failure.

## Notifications contract

```text
CAPABILITY: bke.notifications
CONTRACT VERSION: 1

WHAT I NEED
- source display metadata
- title/body
- category/severity
- optional logical actions
- optional expiry intent

WHAT I GIVE
- publish result
- notification feed
- unread/read/dismissed lifecycle
- mark-read result
- dismiss result
- unread count
```

The notification feed is capability behavior: it is part of WHAT I GIVE. The persistence mechanism behind the feed is not part of the contract. Files, SQLite, PostgreSQL, Redis, remote APIs, or other storage mechanisms remain provider adapters.

`Source` is descriptive message metadata only. It is never proof of caller identity. Producer authentication/authorization must be established by the provider/adapter boundary rather than trusting a caller-supplied display string.

Logical notification actions contain only an action identifier and display label. They must not carry arbitrary URLs, shell commands, executable paths, installer paths, privilege-bearing targets, or other command authority.

Operating-system toasts, push services, message brokers, persistence providers, remote transport, and product UI are adapters/presentation layers outside the portable notification contract.

## Rate-limiting contract

`BKE.RateLimiting` provides the `bke.rate-limiting` contract, version 1. Its consumer supplies an opaque key and policy; it receives an allowed, throttled, or blocked decision with known allowance/timing facts and typed storage failures. Authentication, key interpretation, business policy, and host response stay with the consumer.

The engine owns pure algorithm transitions. A selected store commits each transition atomically in its own authoritative time domain. V1 includes a bounded in-memory store. Future distributed stores must distinguish known non-commit from indeterminate commit and must never silently evict active quota debt. Fail-open acceptance must remain distinguishable from counted allowance.

See [the dedicated guide](sdk/BKE.RateLimiting.md) for exact invariants, resource bounds, cancellation, failure policy, and certification limits.

## .NET 10 baseline

**2026-08-29 — .NET 10 baseline decision**

Active BKE SDK development targets stable .NET 10. .NET 8 SDK development is deprecated. Existing .NET 8 package bytes remain immutable historical artifacts; new package versions target .NET 10 only. Products still targeting .NET 8 must migrate before consuming these package versions.

## Future architecture (document only)

`bke-runtime` may eventually host capability registry, provider resolution, plugin loading, dependency graphs, jobs, events, state, HTTPS adapters, and UI composition. The portable artifact is the contract; `bke-sdk` is not an executable host.

Container distinction: `bke-sdk` is reusable contracts/libraries/packages. `bke-runtime` is the future executable capability host. Use Docker/Podman/OCI to run `bke-runtime`, not `docker run bke-sdk`.

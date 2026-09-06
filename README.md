# BKE SDK Family

`bke-sdk` is the umbrella repository for reusable, product-facing .NET 10 capability packages used by BKE software.

## Active capability family

The current repository contains four independently versioned packages:

```text
BKE SDK
├── BKE.Desktop.Client        2.0.0
├── BKE.Desktop.Licensing     2.0.0
├── BKE.Updater               0.4.0
└── BKE.Notifications         0.4.0
```

A product composes only the capabilities it needs. One repository does not imply one package, one dependency chain, or one CI blast radius.

## Umbrella solution

`BKE.SDK.sln` contains all active SDK and test projects for deliberate full-family development/certification.

`BKE.Desktop.SDK.sln` is retained as the focused Client/Licensing solution used by the legacy capability CI lane so ordinary changes do not rebuild unrelated capabilities.

## Licensing capability

`BKE.Desktop.Licensing` is the hardened product-to-Licensing-Agent authorization and activation capability.

It owns integration mechanics only:

```text
Product
  -> BKE.Desktop.Licensing
  -> BKE Licensing Agent
  -> BKE licensing environment
```

The package never becomes the licensing authority. It does not contain lease authority, entitlement policy, private signing keys, trusted-key selection, privileged update execution, installer authority, or product-specific business logic.

### Quick start

```csharp
using BKE.Desktop.Licensing;

using var licensing = BkeLicensingClient.Create();

var result = await licensing.EnsureAuthorizedAsync(
    productId: "bke-your-product",
    version: "1.0.0",
    installationId: GetStableInstallationId(),
    options: new LicensingFlowOptions
    {
        ActivationInteraction = ActivationInteraction.NativeDesktop
    });

if (!result.Authorized)
{
    // Fail closed.
    return;
}

// Start product functionality.
```

`EnsureAuthorizedAsync` owns the standard startup choreography:

```text
Authorize
  -> Authorized: return
  -> ActivationRequired:
       apply interaction policy
       -> Agent-owned activation presentation
       -> wait for completion
       -> re-authorize
       -> return final decision
```

Products should not duplicate this orchestration in `Program.cs` and should not locate or launch the License Center executable themselves.

## Activation interaction policy

The public policy is explicit rather than a generic `GUI=true` flag:

```csharp
public enum ActivationInteraction
{
    NativeDesktop,
    SystemBrowser,
    CommandLine,
    None
}
```

For `BKE.Desktop.Licensing` 2.0.0:

- `NativeDesktop` is supported and is the default. The SDK asks the Licensing Agent to open its native License Center.
- `None` performs authorization only and leaves `ActivationRequired` to the caller without presenting activation UI.
- `SystemBrowser` is reserved but deliberately returns `Unsupported` until a browser activation path is separately hardened and certified.
- `CommandLine` is reserved but deliberately returns `Unsupported` until an Agent-owned CLI activation path is separately hardened and certified.

An unsupported interaction must never silently fall back to another presentation mode.

## Low-level licensing surface

Advanced callers can use:

- `AuthorizeAsync(productId, version, installationId)`
- `OpenLicenseCenterAsync(productId, version, installationId)`

The current Agent contract uses the fixed loopback boundary:

- `POST http://127.0.0.1:43873/v1/authorize`
- `POST http://127.0.0.1:43873/v1/license-center/open`

Automatic redirects and proxy use are disabled by the default client factory.

## Updater capability

`BKE.Updater` is the canonical product-facing secured update-check capability.

Current contract identity:

```text
CAPABILITY: bke.updates.check
CONTRACT VERSION: 1
```

The consumer provides only product/version identity through `UpdateCheckRequest`. The contract returns invariant-safe states (`UpToDate`, `UpdateAvailable`, `Deferred`, `Failed`) and typed failure classification including provider availability, transport, protocol, malformed-response, verification, and policy failures.

Starting with `BKE.Updater` 0.4.0, the package also provides the default product-to-Licensing-Agent client:

```csharp
using BKE.Updater;

using var updates = BkeUpdaterClient.Create();
var result = await updates.CheckAsync(
    new UpdateCheckRequest("bke-your-product", "1.0.0"));
```

The fixed loopback protocol is private to the SDK implementation. Products do not call Licensing Agent updater routes or define transport DTOs themselves.

Checking for an update remains intentionally separate from downloading or installing one. The SDK does not accept executable paths, installer paths, install roots, trusted URLs, signing keys, trust stores, privileged helper selection, entitlement authority, or update authorization authority.

The Licensing Agent remains the trusted local provider and owns signed-lease resolution, trusted authority communication, signed-policy verification, trusted-key handling, download grants, content verification, and privileged update execution.

## Notifications capability

`BKE.Notifications` is a product-neutral software-side notification capability contract package.

Current hardened contract identity:

```text
CAPABILITY: bke.notifications
CONTRACT VERSION: 1
```

Starting with `BKE.Notifications` 0.4.0, consumers can depend on the narrowest notification capability they actually need:

```text
INotificationPublisher
INotificationFeedReader
INotificationLifecycle
INotificationUnreadCounter
```

`INotificationClient` remains the full composite contract and inherits all four interfaces. This lets producer modules depend only on publishing while feed UIs and full providers opt into their own operation families. Provider-specific transports such as Licensing Agent, Gmail, Telegram, Viber, local persistence, or future services remain adapters outside the portable contract.

The portable contract separates:

- publish acceptance/rejection/failure
- feed retrieval
- notification lifecycle state (`Unread`, `Read`, `Dismissed`)
- mark-read and dismiss operations
- unread-count queries
- logical actions (`Id` + `Label` only)

A publish request does not choose the provider-generated notification identifier or timestamp. `Source` is descriptive message metadata only; it is not authenticated producer identity. Authentication and authorization of the caller belong to the provider/adapter boundary.

Notification feed behavior is part of WHAT I GIVE. Storage is not: databases, files, Redis, remote APIs, or other persistence mechanisms remain provider adapters. Product UI, operating-system toast presentation, push infrastructure, and message brokers also remain outside the portable contract.

Logical actions must not carry executable paths, shell commands, arbitrary URLs, or privilege-bearing targets. The consuming application/provider maps a logical action ID such as `open-update` or `show-license` to approved behavior.

## Package references

Active .NET 10 licensing integrations should use:

```xml
<PackageReference Include="BKE.Desktop.Licensing" Version="2.0.0" />
```

Current reusable capability packages are:

```xml
<PackageReference Include="BKE.Updater" Version="0.4.0" />
<PackageReference Include="BKE.Notifications" Version="0.4.0" />
```

## Per-SDK contract documentation

Each active SDK has one dedicated contract guide under `docs/sdk/` describing:

```text
WHAT I NEED
WHAT I DO
WHAT I GIVE
CAPABILITIES
FAILURES
SECURITY / PROVIDER BOUNDARY
```

## Historical compatibility packages

`BKE.Desktop.Client` 1.0.0 and `BKE.Desktop.Licensing` 1.0.0 remain immutable historical .NET 8 package artifacts. They are not republished or rewritten.

`BKE.Updater` 0.2.0 remains the immutable scaffold artifact. `BKE.Updater` 0.3.0 remains the immutable hardened-contract release. `BKE.Updater` 0.4.0 adds the default Licensing Agent client without changing contract v1 semantics.

`BKE.Notifications` 0.2.0 remains the immutable scaffold artifact. `BKE.Notifications` 0.3.0 remains the hardened notification contract. `BKE.Notifications` 0.4.0 adds least-privilege publish/feed/lifecycle/unread capability interfaces while preserving contract v1 semantics and `INotificationClient` compatibility.

The active .NET 10 desktop successors are:

```text
BKE.Desktop.Client      2.0.0
BKE.Desktop.Licensing   2.0.0
```

Consumer migration is performed repository-by-repository with CI evidence.

## Security boundary

The Licensing Agent remains the local trusted provider and owns activation presentation and updater authority-facing responsibilities. BKE Digital Solutions remains the commercial and remote policy authority.

The SDK cannot select trusted keys, write leases, choose privileged helpers, choose install roots, or authorize itself. Unavailable, malformed, unsupported, denied, or failed outcomes remain fail-closed.

The local product-to-Agent transport is currently ordinary loopback HTTP. It does not cryptographically authenticate the process that owns `127.0.0.1:43873`; process authenticity is a shared protocol-boundary hardening item and must not be "solved" by embedding authority or private keys in product code.

## Licensing of SDK source

New BKE capability packages are distributed under [LICENSE-BKE-PROPRIETARY.txt](LICENSE-BKE-PROPRIETARY.txt) unless a package explicitly retains earlier release terms.

The already-distributed historical package versions retain the terms under which those exact versions were released; successor-package licensing does not retroactively rewrite previously distributed copies.

## .NET 10 baseline

**2026-08-29 — .NET 10 baseline decision**

Active BKE SDK development targets stable .NET 10. Previous .NET 8 package releases remain immutable historical compatibility artifacts and are not republished. New active package releases are .NET 10 only. Products still targeting .NET 8 must migrate before adopting these package versions.

The future `bke-runtime` executable host remains a separate project; this repository contains reusable SDK libraries and secured capability clients/contracts.

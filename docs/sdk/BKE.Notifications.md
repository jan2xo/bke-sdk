# BKE.Notifications

## Purpose

`BKE.Notifications` is the product-neutral software notification capability contract for BKE applications.

It defines typed notification publishing, feed retrieval, lifecycle operations, unread counts, logical actions, and typed failures while leaving transport, persistence, authentication, and presentation to provider/application implementations.

Current package source targets **.NET 10**. Current package version: **0.4.0**.

## Capability identity

```text
Capability ID:      bke.notifications
Contract version:   1
```

Exposed by:

```csharp
NotificationCapability.Id
NotificationCapability.ContractVersion
```

Package 0.4.0 does not change contract version 1 semantics. It adds least-privilege interface boundaries around the existing operations.

## Contract model

```text
PRODUCER
  -> INotificationPublisher

PRODUCT FEED UI
  -> INotificationFeedReader
  -> INotificationLifecycle
  -> INotificationUnreadCounter

FULL PROVIDER
  -> INotificationClient
     = all four capabilities
```

The contract describes notification behavior without requiring a specific database, broker, push service, desktop toast system, or UI framework.

## Capability segregation

Consumers should depend on the narrowest contract they need:

```csharp
INotificationPublisher
INotificationFeedReader
INotificationLifecycle
INotificationUnreadCounter
```

`INotificationClient` remains the full composite contract and inherits all four interfaces for providers that implement the entire notification experience.

This means a producer that only emits notifications does not gain feed-reading, lifecycle-mutation, or unread-count dependencies. A future Gmail, Telegram, Licensing Agent, or other adapter can implement only the capability contracts that make sense for that provider instead of forcing unrelated semantics into every integration.

## WHAT I NEED

### To publish a notification

Depend on:

```csharp
INotificationPublisher
```

Use `NotificationPublishRequest` with:

- `Source`
- `Title`
- `Body`
- optional `Category`
- optional `Severity`
- optional logical `Actions`
- optional `ExpiresAt`

Required text values must be non-empty.

### To read the feed

Depend on:

```csharp
INotificationFeedReader
```

Use `NotificationFeedQuery` with:

- `Limit` — 1 to 200, default 50
- `IncludeDismissed` — default `false`

### To change notification state

Depend on:

```csharp
INotificationLifecycle
```

Supply the notification ID to:

- `MarkReadAsync(...)`
- `DismissAsync(...)`

### To read unread count

Depend on:

```csharp
INotificationUnreadCounter
```

### Full provider requirement

A provider that supports all notification operations may implement:

```csharp
INotificationClient
```

The SDK does not choose persistence or transport.

## WHAT I DO

The contracts define these operations:

```csharp
Task<NotificationPublishResult> PublishAsync(...);
Task<NotificationFeedResult> GetFeedAsync(...);
Task<NotificationOperationResult> MarkReadAsync(...);
Task<NotificationOperationResult> DismissAsync(...);
Task<NotificationUnreadCountResult> GetUnreadCountAsync(...);
```

A conforming provider maps its implementation into these stable typed results.

## WHAT I GIVE

### Publish result

`NotificationPublishResult` contains:

- `Status`
- optional `NotificationId`
- optional `Error`

`NotificationPublishStatus`:

- `Accepted`
- `Rejected`
- `Failed`

### Feed result

`NotificationFeedResult` contains:

- `Items`
- optional `Error`
- `Succeeded`

Each `NotificationItem` contains:

- `Id`
- `Source`
- `Title`
- `Body`
- `Category`
- `Severity`
- `CreatedAt`
- optional `ExpiresAt`
- `State`
- logical `Actions`

### Lifecycle operation result

`NotificationOperationResult` reports:

- `Succeeded`
- `NotFound`
- `Rejected`
- `Failed`

plus an optional typed error.

### Unread count result

`NotificationUnreadCountResult` contains:

- `Count`
- optional `Error`
- `Succeeded`

Unread count can never be negative.

## Notification states

```csharp
NotificationState.Unread
NotificationState.Read
NotificationState.Dismissed
```

## Categories

```csharp
NotificationCategory.General
NotificationCategory.Product
NotificationCategory.Licensing
NotificationCategory.Update
NotificationCategory.System
```

## Severities

```csharp
NotificationSeverity.Information
NotificationSeverity.Success
NotificationSeverity.Warning
NotificationSeverity.Error
```

Severity communicates meaning to the consumer/provider. The SDK does not prescribe colors, icons, sounds, or platform presentation.

## Logical actions

A notification may contain `NotificationAction` values with:

- `Id`
- `Label`

Actions are logical identifiers only.

The provider/product decides what an action ID means and how it is presented. The SDK does not embed executable commands or arbitrary URLs as privileged behavior.

## Typed failures

`NotificationError` contains:

- `Code`
- `Message`
- `Retryable`

`NotificationErrorCode`:

- `InvalidRequest`
- `ProviderUnavailable`
- `Rejected`
- `Conflict`
- `ProtocolFailure`
- `Unknown`

This allows products to distinguish invalid input, temporary provider outages, explicit rejection, state conflicts, protocol problems, and unknown failures.

## Capabilities

Current contract supports:

```text
PUBLISH
  interface -> INotificationPublisher
  input     -> NotificationPublishRequest
  output    -> NotificationPublishResult

READ FEED
  interface -> INotificationFeedReader
  input     -> NotificationFeedQuery
  output    -> NotificationFeedResult

MARK READ / DISMISS
  interface -> INotificationLifecycle
  input     -> notification ID
  output    -> NotificationOperationResult

UNREAD COUNT
  interface -> INotificationUnreadCounter
  input     -> none
  output    -> NotificationUnreadCountResult
```

## What this SDK does NOT do

`BKE.Notifications` intentionally does not own:

- Windows toast / Action Center integration
- Android/iOS push services
- Gmail/SMTP delivery
- Telegram/Viber delivery
- WebSockets or SSE infrastructure
- persistence/database implementation
- Redis/message brokers
- notification server hosting
- product notification UI
- producer authentication implementation

Those are provider/application concerns behind or above the contract.

## Minimal publish usage

```csharp
using BKE.Notifications;

public sealed class ProductNotifier
{
    private readonly INotificationPublisher notifications;

    public ProductNotifier(INotificationPublisher notifications)
    {
        this.notifications = notifications;
    }

    public Task<NotificationPublishResult> PublishUpdateAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        var request = new NotificationPublishRequest(
            source: "bke-render-dock",
            title: "Update available",
            body: "A new Render Dock version is available.",
            category: NotificationCategory.Update,
            severity: NotificationSeverity.Information);

        return notifications.PublishAsync(request, cancellationToken);
    }
}
```

## Provider responsibility

A provider implementation may own:

- durable storage
- ordering and retention
- producer authentication/authorization
- transport
- synchronization
- delivery infrastructure
- mapping logical actions into safe application behavior

Those choices must remain behind the portable SDK contract.

## Consumer responsibility

The consuming product owns:

- when to publish notifications
- how to render its notification center
- how to interpret logical action IDs
- how to react to typed provider errors
- product-specific filtering and presentation behavior

The product should depend on the narrowest notification capability interface it needs, not on a specific database, broker, notification service implementation, or the full `INotificationClient` when only one operation family is required.

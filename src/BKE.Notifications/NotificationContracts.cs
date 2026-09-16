using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BKE.Notifications;

public static class NotificationCapability
{
    public const string Id = "bke.notifications";
    public const int ContractVersion = 1;
}

public enum NotificationSeverity
{
    Information,
    Success,
    Warning,
    Error
}

public enum NotificationCategory
{
    General,
    Product,
    Licensing,
    Update,
    System
}

public sealed record NotificationAction
{
    public string Id { get; }
    public string Label { get; }

    public NotificationAction(string id, string label)
    {
        Id = ContractValue.Require(id, nameof(id));
        Label = ContractValue.Require(label, nameof(label));
    }
}

public sealed record NotificationPublishRequest
{
    public string Source { get; }
    public string Title { get; }
    public string Body { get; }
    public NotificationCategory Category { get; }
    public NotificationSeverity Severity { get; }
    public IReadOnlyList<NotificationAction> Actions { get; }
    public DateTimeOffset? ExpiresAt { get; }

    public NotificationPublishRequest(
        string source,
        string title,
        string body,
        NotificationCategory category = NotificationCategory.General,
        NotificationSeverity severity = NotificationSeverity.Information,
        IReadOnlyList<NotificationAction>? actions = null,
        DateTimeOffset? expiresAt = null)
    {
        Source = ContractValue.Require(source, nameof(source));
        Title = ContractValue.Require(title, nameof(title));
        Body = ContractValue.Require(body, nameof(body));
        Category = category;
        Severity = severity;
        Actions = actions?.ToArray() ?? Array.Empty<NotificationAction>();
        ExpiresAt = expiresAt;
    }
}

public enum NotificationPublishStatus
{
    Accepted,
    Rejected,
    Failed
}

public enum NotificationState
{
    Unread,
    Read,
    Dismissed
}

public enum NotificationErrorCode
{
    InvalidRequest,
    ProviderUnavailable,
    Rejected,
    Conflict,
    ProtocolFailure,
    Unknown
}

public sealed record NotificationError
{
    public NotificationErrorCode Code { get; }
    public string Message { get; }
    public bool Retryable { get; }

    public NotificationError(NotificationErrorCode code, string message, bool retryable = false)
    {
        Code = code;
        Message = ContractValue.Require(message, nameof(message));
        Retryable = retryable;
    }
}

public sealed record NotificationPublishResult
{
    public NotificationPublishStatus Status { get; }
    public string? NotificationId { get; }
    public NotificationError? Error { get; }

    private NotificationPublishResult(
        NotificationPublishStatus status,
        string? notificationId,
        NotificationError? error)
    {
        Status = status;
        NotificationId = notificationId;
        Error = error;
    }

    public static NotificationPublishResult Accepted(string notificationId) =>
        new(NotificationPublishStatus.Accepted, ContractValue.Require(notificationId, nameof(notificationId)), null);

    public static NotificationPublishResult Rejected(NotificationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new NotificationPublishResult(NotificationPublishStatus.Rejected, null, error);
    }

    public static NotificationPublishResult Failed(NotificationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new NotificationPublishResult(NotificationPublishStatus.Failed, null, error);
    }
}

public sealed record NotificationItem
{
    public string Id { get; }
    public string Source { get; }
    public string Title { get; }
    public string Body { get; }
    public NotificationCategory Category { get; }
    public NotificationSeverity Severity { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public NotificationState State { get; }
    public IReadOnlyList<NotificationAction> Actions { get; }

    public NotificationItem(
        string id,
        string source,
        string title,
        string body,
        DateTimeOffset createdAt,
        NotificationState state = NotificationState.Unread,
        NotificationCategory category = NotificationCategory.General,
        NotificationSeverity severity = NotificationSeverity.Information,
        IReadOnlyList<NotificationAction>? actions = null,
        DateTimeOffset? expiresAt = null)
    {
        Id = ContractValue.Require(id, nameof(id));
        Source = ContractValue.Require(source, nameof(source));
        Title = ContractValue.Require(title, nameof(title));
        Body = ContractValue.Require(body, nameof(body));
        CreatedAt = createdAt;
        State = state;
        Category = category;
        Severity = severity;
        Actions = actions?.ToArray() ?? Array.Empty<NotificationAction>();
        ExpiresAt = expiresAt;
    }
}

public sealed record NotificationFeedQuery
{
    public int Limit { get; }
    public bool IncludeDismissed { get; }

    public NotificationFeedQuery(int limit = 50, bool includeDismissed = false)
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 200.");
        }

        Limit = limit;
        IncludeDismissed = includeDismissed;
    }
}

public sealed record NotificationFeedResult
{
    public IReadOnlyList<NotificationItem> Items { get; }
    public NotificationError? Error { get; }
    public bool Succeeded => Error is null;

    private NotificationFeedResult(IReadOnlyList<NotificationItem> items, NotificationError? error)
    {
        Items = items;
        Error = error;
    }

    public static NotificationFeedResult Success(IReadOnlyList<NotificationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new NotificationFeedResult(items.ToArray(), null);
    }

    public static NotificationFeedResult Failed(NotificationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new NotificationFeedResult(Array.Empty<NotificationItem>(), error);
    }
}

public enum NotificationOperationStatus
{
    Succeeded,
    NotFound,
    Rejected,
    Failed
}

public sealed record NotificationOperationResult
{
    public NotificationOperationStatus Status { get; }
    public NotificationError? Error { get; }

    private NotificationOperationResult(NotificationOperationStatus status, NotificationError? error)
    {
        Status = status;
        Error = error;
    }

    public static NotificationOperationResult Succeeded() =>
        new(NotificationOperationStatus.Succeeded, null);

    public static NotificationOperationResult NotFound() =>
        new(NotificationOperationStatus.NotFound, null);

    public static NotificationOperationResult Rejected(NotificationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new NotificationOperationResult(NotificationOperationStatus.Rejected, error);
    }

    public static NotificationOperationResult Failed(NotificationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new NotificationOperationResult(NotificationOperationStatus.Failed, error);
    }
}

public sealed record NotificationUnreadCountResult
{
    public int Count { get; }
    public NotificationError? Error { get; }
    public bool Succeeded => Error is null;

    private NotificationUnreadCountResult(int count, NotificationError? error)
    {
        Count = count;
        Error = error;
    }

    public static NotificationUnreadCountResult Success(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Unread count cannot be negative.");
        }

        return new NotificationUnreadCountResult(count, null);
    }

    public static NotificationUnreadCountResult Failed(NotificationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new NotificationUnreadCountResult(0, error);
    }
}

public interface INotificationPublisher
{
    Task<NotificationPublishResult> PublishAsync(
        NotificationPublishRequest request,
        CancellationToken cancellationToken = default);
}

public interface INotificationFeedReader
{
    Task<NotificationFeedResult> GetFeedAsync(
        NotificationFeedQuery query,
        CancellationToken cancellationToken = default);
}

public interface INotificationLifecycle
{
    Task<NotificationOperationResult> MarkReadAsync(
        string notificationId,
        CancellationToken cancellationToken = default);

    Task<NotificationOperationResult> DismissAsync(
        string notificationId,
        CancellationToken cancellationToken = default);
}

public interface INotificationUnreadCounter
{
    Task<NotificationUnreadCountResult> GetUnreadCountAsync(
        CancellationToken cancellationToken = default);
}

public interface INotificationClient :
    INotificationPublisher,
    INotificationFeedReader,
    INotificationLifecycle,
    INotificationUnreadCounter
{
}

internal static class ContractValue
{
    public static string Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value;
    }
}

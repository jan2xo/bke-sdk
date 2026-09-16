using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BKE.Notifications;

/// <summary>
/// Least-privilege notification inbox backed by the machine-local BKE Licensing Agent.
/// This client can read and mutate inbox lifecycle state but cannot publish notifications.
/// </summary>
public sealed class BkeNotificationInboxClient :
    INotificationFeedReader,
    INotificationLifecycle,
    INotificationUnreadCounter,
    IDisposable
{
    public static readonly Uri DefaultAgentBaseAddress = new("http://127.0.0.1:43873/");

    private readonly string productId;
    private readonly string version;
    private readonly string installationId;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;

    public static BkeNotificationInboxClient Create(
        string productId,
        string version,
        string installationId)
    {
        ValidateContext(productId, version, installationId);
        var handler = CreateDefaultHandler();
        return new BkeNotificationInboxClient(
            productId,
            version,
            installationId,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            owns: true);
    }

    internal static BkeNotificationInboxClient Create(
        string productId,
        string version,
        string installationId,
        HttpClient httpClient)
    {
        ValidateContext(productId, version, installationId);
        return new BkeNotificationInboxClient(
            productId,
            version,
            installationId,
            httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
            owns: false);
    }

    internal static HttpClientHandler CreateDefaultHandler() =>
        new()
        {
            AllowAutoRedirect = false,
            UseProxy = false
        };

    private BkeNotificationInboxClient(
        string productId,
        string version,
        string installationId,
        HttpClient httpClient,
        bool owns)
    {
        this.productId = productId;
        this.version = version;
        this.installationId = installationId;
        this.httpClient = httpClient;
        ownsHttpClient = owns;
    }

    public async Task<NotificationFeedResult> GetFeedAsync(
        NotificationFeedQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            using var timeout = LinkedTimeout(cancellationToken);
            using var response = await httpClient.PostAsJsonAsync(
                new Uri(DefaultAgentBaseAddress, "v1/notifications/feed"),
                new AgentFeedRequest(productId, version, installationId, query.Limit, query.IncludeDismissed),
                timeout.Token).ConfigureAwait(false);

            var document = await ReadAsync<AgentFeedResponse>(response, timeout.Token).ConfigureAwait(false);
            if (document is null)
                return FeedFailure(NotificationErrorCode.ProtocolFailure, "The local notification provider returned malformed data.");
            if (!Compatible(document.CapabilityId, document.ContractVersion))
                return FeedFailure(NotificationErrorCode.ProtocolFailure, "The local notification provider returned an incompatible capability contract.");
            if (document.Status == "Failed")
                return NotificationFeedResult.Failed(MapError(document.Error));
            if (document.Status != "Succeeded" || document.Error is not null || document.Items is null)
                return FeedFailure(NotificationErrorCode.ProtocolFailure, "The local notification provider returned an invalid feed result.");
            if (!response.IsSuccessStatusCode)
                return FeedFailure(NotificationErrorCode.ProtocolFailure, "The local notification provider returned an inconsistent HTTP result.");

            var items = new List<NotificationItem>(document.Items.Count);
            foreach (var value in document.Items)
            {
                var item = MapItem(value);
                if (item is null)
                    return FeedFailure(NotificationErrorCode.ProtocolFailure, "The local notification provider returned a malformed notification item.");
                items.Add(item);
            }
            return NotificationFeedResult.Success(items);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FeedFailure(NotificationErrorCode.ProviderUnavailable, "The local notification provider did not respond in time.", true);
        }
        catch (HttpRequestException)
        {
            return FeedFailure(NotificationErrorCode.ProviderUnavailable, "The local notification provider is unavailable.", true);
        }
        catch (JsonException)
        {
            return FeedFailure(NotificationErrorCode.ProtocolFailure, "The local notification provider returned malformed JSON.");
        }
    }

    public Task<NotificationOperationResult> MarkReadAsync(
        string notificationId,
        CancellationToken cancellationToken = default) =>
        LifecycleAsync("v1/notifications/mark-read", notificationId, cancellationToken);

    public Task<NotificationOperationResult> DismissAsync(
        string notificationId,
        CancellationToken cancellationToken = default) =>
        LifecycleAsync("v1/notifications/dismiss", notificationId, cancellationToken);

    public async Task<NotificationUnreadCountResult> GetUnreadCountAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = LinkedTimeout(cancellationToken);
            using var response = await httpClient.PostAsJsonAsync(
                new Uri(DefaultAgentBaseAddress, "v1/notifications/unread-count"),
                new AgentContextRequest(productId, version, installationId),
                timeout.Token).ConfigureAwait(false);

            var document = await ReadAsync<AgentUnreadResponse>(response, timeout.Token).ConfigureAwait(false);
            if (document is null || !Compatible(document.CapabilityId, document.ContractVersion))
                return CountFailure(NotificationErrorCode.ProtocolFailure, "The local notification provider returned an incompatible response.");
            if (document.Status == "Failed")
                return NotificationUnreadCountResult.Failed(MapError(document.Error));
            if (document.Status != "Succeeded" || document.Error is not null || document.Count is null || document.Count < 0)
                return CountFailure(NotificationErrorCode.ProtocolFailure, "The local notification provider returned an invalid unread count.");
            if (!response.IsSuccessStatusCode)
                return CountFailure(NotificationErrorCode.ProtocolFailure, "The local notification provider returned an inconsistent HTTP result.");
            return NotificationUnreadCountResult.Success(document.Count.Value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CountFailure(NotificationErrorCode.ProviderUnavailable, "The local notification provider did not respond in time.", true);
        }
        catch (HttpRequestException)
        {
            return CountFailure(NotificationErrorCode.ProviderUnavailable, "The local notification provider is unavailable.", true);
        }
        catch (JsonException)
        {
            return CountFailure(NotificationErrorCode.ProtocolFailure, "The local notification provider returned malformed JSON.");
        }
    }

    private async Task<NotificationOperationResult> LifecycleAsync(
        string relativePath,
        string notificationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(notificationId))
            return NotificationOperationResult.Failed(new NotificationError(
                NotificationErrorCode.InvalidRequest,
                "A notification identifier is required."));

        try
        {
            using var timeout = LinkedTimeout(cancellationToken);
            using var response = await httpClient.PostAsJsonAsync(
                new Uri(DefaultAgentBaseAddress, relativePath),
                new AgentLifecycleRequest(productId, version, installationId, notificationId),
                timeout.Token).ConfigureAwait(false);

            var document = await ReadAsync<AgentOperationResponse>(response, timeout.Token).ConfigureAwait(false);
            if (document is null || !Compatible(document.CapabilityId, document.ContractVersion))
                return OperationFailure(NotificationErrorCode.ProtocolFailure, "The local notification provider returned an incompatible response.");
            if (document.Status == "Failed")
                return NotificationOperationResult.Failed(MapError(document.Error));
            if (!response.IsSuccessStatusCode)
                return OperationFailure(NotificationErrorCode.ProtocolFailure, "The local notification provider returned an inconsistent HTTP result.");

            return document.Status switch
            {
                "Succeeded" when document.Error is null => NotificationOperationResult.Succeeded(),
                "NotFound" when document.Error is null => NotificationOperationResult.NotFound(),
                "Rejected" when document.Error is not null => NotificationOperationResult.Rejected(MapError(document.Error)),
                _ => OperationFailure(NotificationErrorCode.ProtocolFailure, "The local notification provider returned an invalid lifecycle result.")
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OperationFailure(NotificationErrorCode.ProviderUnavailable, "The local notification provider did not respond in time.", true);
        }
        catch (HttpRequestException)
        {
            return OperationFailure(NotificationErrorCode.ProviderUnavailable, "The local notification provider is unavailable.", true);
        }
        catch (JsonException)
        {
            return OperationFailure(NotificationErrorCode.ProtocolFailure, "The local notification provider returned malformed JSON.");
        }
    }

    private static NotificationItem? MapItem(AgentNotificationItem value)
    {
        if (string.IsNullOrWhiteSpace(value.Id) ||
            string.IsNullOrWhiteSpace(value.Source) ||
            string.IsNullOrWhiteSpace(value.Title) ||
            string.IsNullOrWhiteSpace(value.Body) ||
            !Enum.TryParse<NotificationState>(value.State, false, out var state) ||
            !Enum.TryParse<NotificationCategory>(value.Category, false, out var category) ||
            !Enum.TryParse<NotificationSeverity>(value.Severity, false, out var severity))
        {
            return null;
        }

        var actions = new List<NotificationAction>();
        foreach (var action in value.Actions ?? Array.Empty<AgentNotificationAction>())
        {
            if (string.IsNullOrWhiteSpace(action.Id) || string.IsNullOrWhiteSpace(action.Label))
                return null;
            actions.Add(new NotificationAction(action.Id, action.Label));
        }

        return new NotificationItem(
            value.Id,
            value.Source,
            value.Title,
            value.Body,
            value.CreatedAt,
            state,
            category,
            severity,
            actions,
            value.ExpiresAt);
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static CancellationTokenSource LinkedTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        return timeout;
    }

    private static bool Compatible(string? capabilityId, int contractVersion) =>
        string.Equals(capabilityId, NotificationCapability.Id, StringComparison.Ordinal) &&
        contractVersion == NotificationCapability.ContractVersion;

    private static NotificationError MapError(AgentError? error)
    {
        if (error is null || string.IsNullOrWhiteSpace(error.Message))
            return new NotificationError(NotificationErrorCode.ProtocolFailure, "The local notification provider returned an invalid error.");
        if (!Enum.TryParse<NotificationErrorCode>(error.Code, false, out var code))
            code = NotificationErrorCode.ProtocolFailure;
        return new NotificationError(code, error.Message, error.Retryable);
    }

    private static NotificationFeedResult FeedFailure(NotificationErrorCode code, string message, bool retryable = false) =>
        NotificationFeedResult.Failed(new NotificationError(code, message, retryable));

    private static NotificationOperationResult OperationFailure(NotificationErrorCode code, string message, bool retryable = false) =>
        NotificationOperationResult.Failed(new NotificationError(code, message, retryable));

    private static NotificationUnreadCountResult CountFailure(NotificationErrorCode code, string message, bool retryable = false) =>
        NotificationUnreadCountResult.Failed(new NotificationError(code, message, retryable));

    private static void ValidateContext(string productId, string version, string installationId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            throw new ArgumentException("A product identifier is required.", nameof(productId));
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("A product version is required.", nameof(version));
        if (string.IsNullOrWhiteSpace(installationId))
            throw new ArgumentException("An installation identifier is required.", nameof(installationId));
    }

    public void Dispose()
    {
        if (ownsHttpClient)
            httpClient.Dispose();
    }

    private sealed record AgentContextRequest(
        [property: JsonPropertyName("product_id")] string ProductId,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("installation_id")] string InstallationId);

    private sealed record AgentFeedRequest(
        [property: JsonPropertyName("product_id")] string ProductId,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("installation_id")] string InstallationId,
        [property: JsonPropertyName("limit")] int Limit,
        [property: JsonPropertyName("include_dismissed")] bool IncludeDismissed);

    private sealed record AgentLifecycleRequest(
        [property: JsonPropertyName("product_id")] string ProductId,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("installation_id")] string InstallationId,
        [property: JsonPropertyName("notification_id")] string NotificationId);

    private sealed record AgentFeedResponse(
        [property: JsonPropertyName("capability_id")] string? CapabilityId,
        [property: JsonPropertyName("contract_version")] int ContractVersion,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("items")] IReadOnlyList<AgentNotificationItem>? Items,
        [property: JsonPropertyName("error")] AgentError? Error);

    private sealed record AgentOperationResponse(
        [property: JsonPropertyName("capability_id")] string? CapabilityId,
        [property: JsonPropertyName("contract_version")] int ContractVersion,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("error")] AgentError? Error);

    private sealed record AgentUnreadResponse(
        [property: JsonPropertyName("capability_id")] string? CapabilityId,
        [property: JsonPropertyName("contract_version")] int ContractVersion,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("count")] int? Count,
        [property: JsonPropertyName("error")] AgentError? Error);

    private sealed record AgentNotificationItem(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("severity")] string? Severity,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("actions")] IReadOnlyList<AgentNotificationAction>? Actions);

    private sealed record AgentNotificationAction(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("label")] string? Label);

    private sealed record AgentError(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("retryable")] bool Retryable);
}

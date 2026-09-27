using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BKE.Desktop.Licensing;

/// <summary>
/// Typed product-facing client for the machine-local BKE Licensing Agent.
/// The Agent remains the authority and owns activation presentation.
/// </summary>
public sealed class BkeLicensingClient : IDisposable
{
    public static readonly Uri DefaultAgentBaseAddress = new("http://127.0.0.1:43873/");

    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;

    public static BkeLicensingClient Create()
    {
        var handler = CreateDefaultHandler();
        return new BkeLicensingClient(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            owns: true);
    }

    internal static BkeLicensingClient Create(HttpClient httpClient) =>
        new(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), owns: false);

    internal static HttpClientHandler CreateDefaultHandler() =>
        new()
        {
            AllowAutoRedirect = false,
            UseProxy = false
        };

    private BkeLicensingClient(HttpClient client, bool owns)
    {
        httpClient = client;
        ownsHttpClient = owns;
    }

    /// <summary>
    /// Ask the local Agent for the current authorization decision only.
    /// This method never launches activation UI.
    /// </summary>
    public async Task<AuthorizationResult> AuthorizeAsync(
        string productId,
        string version,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        if (!Valid(productId, version, installationId))
            return new(AuthorizationStatus.InvalidRequest, "Product identity is missing or invalid.");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            using var response = await httpClient.PostAsJsonAsync(
                new Uri(DefaultAgentBaseAddress, "v1/authorize"),
                new AuthorizationRequest(productId, version, installationId),
                timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new(MapAuthorizationHttpFailure(response.StatusCode),
                    $"The Licensing Agent rejected the authorization request with HTTP {(int)response.StatusCode}.");

            var decision = await response.Content.ReadFromJsonAsync<AuthorizationResponse>(
                cancellationToken: timeout.Token).ConfigureAwait(false);

            if (decision?.Authorized is null || string.IsNullOrWhiteSpace(decision.Reason))
                return new(AuthorizationStatus.InvalidResponse,
                    "The Licensing Agent returned an invalid authorization response.");

            if (decision.Authorized.Value)
                return new(AuthorizationStatus.Authorized, decision.Reason);

            var status = MapAuthorizationReason(decision.Reason);
            return new(status, decision.Reason);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(AuthorizationStatus.Timeout, "The Licensing Agent did not respond in time.");
        }
        catch (HttpRequestException)
        {
            return new(AuthorizationStatus.AgentUnavailable, "The Licensing Agent is unavailable.");
        }
        catch (JsonException)
        {
            return new(AuthorizationStatus.InvalidResponse, "The Licensing Agent returned malformed data.");
        }
    }

    /// <summary>
    /// Ask the Agent to open its native License Center for this product context.
    /// Products do not locate or launch the License Center executable themselves.
    /// </summary>
    public async Task<LicenseCenterResult> OpenLicenseCenterAsync(
        string productId,
        string version,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        if (!Valid(productId, version, installationId))
            return new(LicenseCenterStatus.InvalidRequest, "Product identity is missing or invalid.");

        var correlationId = Guid.NewGuid().ToString("N");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));

            using var response = await httpClient.PostAsJsonAsync(
                new Uri(DefaultAgentBaseAddress, "v1/license-center/open"),
                new LicenseCenterRequest(productId, version, installationId, correlationId),
                timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new(MapLicenseCenterHttpFailure(response.StatusCode),
                    $"The Licensing Agent rejected the License Center request with HTTP {(int)response.StatusCode}.");

            var result = await response.Content.ReadFromJsonAsync<LicenseCenterResponse>(
                cancellationToken: timeout.Token).ConfigureAwait(false);

            if (result?.CorrelationId != correlationId || string.IsNullOrWhiteSpace(result.Outcome))
                return new(LicenseCenterStatus.InvalidResponse,
                    "The Licensing Agent returned malformed License Center data.");

            return result.Outcome switch
            {
                "authorization_refreshed" => new(LicenseCenterStatus.AuthorizationRefreshed, result.Reason ?? string.Empty),
                "completed" => new(LicenseCenterStatus.Completed, result.Reason ?? string.Empty),
                "cancelled" => new(LicenseCenterStatus.Cancelled, result.Reason ?? string.Empty),
                "agent_unavailable" => new(LicenseCenterStatus.AgentUnavailable, result.Reason ?? string.Empty),
                "invalid_product_context" => new(LicenseCenterStatus.InvalidProductContext, result.Reason ?? string.Empty),
                "incompatible_product_version" => new(LicenseCenterStatus.IncompatibleProductVersion, result.Reason ?? string.Empty),
                "activation_failed" => new(LicenseCenterStatus.ActivationFailed, result.Reason ?? string.Empty),
                "failed" => new(LicenseCenterStatus.Failed, result.Reason ?? string.Empty),
                _ => new(LicenseCenterStatus.Unsupported, result.Reason ?? $"Unsupported outcome: {result.Outcome}")
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(LicenseCenterStatus.Timeout, "License Center did not complete in time.");
        }
        catch (HttpRequestException)
        {
            return new(LicenseCenterStatus.AgentUnavailable, "The Licensing Agent is unavailable.");
        }
        catch (JsonException)
        {
            return new(LicenseCenterStatus.InvalidResponse, "The Licensing Agent returned malformed data.");
        }
    }

    /// <summary>
    /// Complete the standard licensing startup flow. If activation is required,
    /// the selected interaction policy determines whether the Agent may present
    /// activation. NativeDesktop is the only presentation certified in v1.0.0.
    /// </summary>
    public async Task<AuthorizationResult> EnsureAuthorizedAsync(
        string productId,
        string version,
        string installationId,
        LicensingFlowOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= LicensingFlowOptions.Default;

        if (options.AuthorizationRefreshTimeout <= TimeSpan.Zero ||
            options.AuthorizationRefreshInterval <= TimeSpan.Zero)
        {
            return new(AuthorizationStatus.InvalidRequest,
                "Authorization refresh timing must be greater than zero.");
        }

        var authorization = await AuthorizeAsync(
            productId, version, installationId, cancellationToken).ConfigureAwait(false);

        if (!RequiresLicenseCenterRecovery(authorization.Status))
            return authorization;

        switch (options.ActivationInteraction)
        {
            case ActivationInteraction.None:
                return authorization;

            case ActivationInteraction.SystemBrowser:
                return new(AuthorizationStatus.Unsupported,
                    "activation_interaction_not_supported:system_browser");

            case ActivationInteraction.CommandLine:
                return new(AuthorizationStatus.Unsupported,
                    "activation_interaction_not_supported:command_line");

            case ActivationInteraction.NativeDesktop:
                break;

            default:
                return new(AuthorizationStatus.InvalidRequest,
                    "Unknown activation interaction policy.");
        }

        var center = await OpenLicenseCenterAsync(
            productId, version, installationId, cancellationToken).ConfigureAwait(false);

        switch (center.Status)
        {
            case LicenseCenterStatus.AuthorizationRefreshed:
            case LicenseCenterStatus.Completed:
                return await WaitForAuthorizationRefreshAsync(
                    productId, version, installationId, options, cancellationToken).ConfigureAwait(false);

            case LicenseCenterStatus.Cancelled:
                return new(AuthorizationStatus.ActivationCancelled,
                    string.IsNullOrWhiteSpace(center.Reason) ? "activation_cancelled" : center.Reason);

            case LicenseCenterStatus.AgentUnavailable:
                return new(AuthorizationStatus.AgentUnavailable, center.Reason);

            case LicenseCenterStatus.Timeout:
                return new(AuthorizationStatus.Timeout, center.Reason);

            case LicenseCenterStatus.ProtocolRejected:
                return new(AuthorizationStatus.ProtocolRejected, center.Reason);

            case LicenseCenterStatus.InvalidRequest:
                return new(AuthorizationStatus.InvalidRequest, center.Reason);

            case LicenseCenterStatus.InvalidResponse:
                return new(AuthorizationStatus.InvalidResponse, center.Reason);

            case LicenseCenterStatus.InvalidProductContext:
            case LicenseCenterStatus.IncompatibleProductVersion:
            case LicenseCenterStatus.Unsupported:
                return new(AuthorizationStatus.Unsupported, center.Reason);

            case LicenseCenterStatus.ActivationFailed:
            case LicenseCenterStatus.Failed:
            default:
                return new(AuthorizationStatus.Denied,
                    string.IsNullOrWhiteSpace(center.Reason) ? "activation_failed" : center.Reason);
        }
    }

    private async Task<AuthorizationResult> WaitForAuthorizationRefreshAsync(
        string productId,
        string version,
        string installationId,
        LicensingFlowOptions options,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + options.AuthorizationRefreshTimeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await AuthorizeAsync(
                productId, version, installationId, cancellationToken).ConfigureAwait(false);

            if (!RequiresLicenseCenterRecovery(result.Status))
                return result;

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            var delay = remaining < options.AuthorizationRefreshInterval
                ? remaining
                : options.AuthorizationRefreshInterval;

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        return new(AuthorizationStatus.Timeout,
            "Activation completed but authorization did not refresh in time.");
    }

    private static AuthorizationStatus MapAuthorizationReason(string reason)
    {
        if (reason.Equals("activation_required", StringComparison.OrdinalIgnoreCase))
            return AuthorizationStatus.ActivationRequired;

        if (reason.Equals("lease_expired", StringComparison.OrdinalIgnoreCase) ||
            reason.Equals("lease_version_rejected", StringComparison.OrdinalIgnoreCase) ||
            reason.Equals("lease_revoked", StringComparison.OrdinalIgnoreCase) ||
            reason.Equals("lease_superseded", StringComparison.OrdinalIgnoreCase) ||
            reason.Equals("lease_authority_mismatch", StringComparison.OrdinalIgnoreCase) ||
            reason.Equals("unverifiable_signed_lease", StringComparison.OrdinalIgnoreCase))
        {
            return AuthorizationStatus.RecoveryRequired;
        }

        if (reason.Equals("unsupported", StringComparison.OrdinalIgnoreCase) ||
            reason.Equals("unsupported_product", StringComparison.OrdinalIgnoreCase) ||
            reason.Equals("unsupported_version", StringComparison.OrdinalIgnoreCase))
        {
            return AuthorizationStatus.Unsupported;
        }

        return AuthorizationStatus.Denied;
    }

    private static bool RequiresLicenseCenterRecovery(AuthorizationStatus status) =>
        status is AuthorizationStatus.ActivationRequired or AuthorizationStatus.RecoveryRequired;

    private static AuthorizationStatus MapAuthorizationHttpFailure(HttpStatusCode statusCode) =>
        (int)statusCode >= 500
            ? AuthorizationStatus.AgentUnavailable
            : AuthorizationStatus.ProtocolRejected;

    private static LicenseCenterStatus MapLicenseCenterHttpFailure(HttpStatusCode statusCode) =>
        (int)statusCode >= 500
            ? LicenseCenterStatus.AgentUnavailable
            : LicenseCenterStatus.ProtocolRejected;

    private static bool Valid(params string[] values) =>
        values.All(value => !string.IsNullOrWhiteSpace(value));

    public void Dispose()
    {
        if (ownsHttpClient)
            httpClient.Dispose();
    }

    private sealed record AuthorizationRequest(
        [property: JsonPropertyName("product_id")] string ProductId,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("installation_id")] string InstallationId);

    private sealed record AuthorizationResponse(
        [property: JsonPropertyName("authorized")] bool? Authorized,
        [property: JsonPropertyName("reason")] string? Reason);

    private sealed record LicenseCenterRequest(
        [property: JsonPropertyName("product_id")] string ProductId,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("installation_id")] string InstallationId,
        [property: JsonPropertyName("correlation_id")] string CorrelationId);

    private sealed record LicenseCenterResponse(
        [property: JsonPropertyName("outcome")] string? Outcome,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("correlation_id")] string? CorrelationId);
}

public enum ActivationInteraction
{
    NativeDesktop,
    SystemBrowser,
    CommandLine,
    None
}

public sealed record LicensingFlowOptions
{
    public static LicensingFlowOptions Default { get; } = new();

    public ActivationInteraction ActivationInteraction { get; init; } = ActivationInteraction.NativeDesktop;
    public TimeSpan AuthorizationRefreshTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan AuthorizationRefreshInterval { get; init; } = TimeSpan.FromSeconds(1);
}

public enum AuthorizationStatus
{
    Authorized,
    Denied,
    ActivationRequired,
    RecoveryRequired,
    ActivationCancelled,
    AgentUnavailable,
    Timeout,
    ProtocolRejected,
    Unsupported,
    InvalidRequest,
    InvalidResponse
}

public sealed record AuthorizationResult(AuthorizationStatus Status, string Reason)
{
    public bool Authorized => Status == AuthorizationStatus.Authorized;
}

public enum LicenseCenterStatus
{
    Completed,
    AuthorizationRefreshed,
    Cancelled,
    AgentUnavailable,
    Timeout,
    ProtocolRejected,
    InvalidProductContext,
    IncompatibleProductVersion,
    ActivationFailed,
    Unsupported,
    InvalidRequest,
    InvalidResponse,
    Failed
}

public sealed record LicenseCenterResult(LicenseCenterStatus Status, string Reason);

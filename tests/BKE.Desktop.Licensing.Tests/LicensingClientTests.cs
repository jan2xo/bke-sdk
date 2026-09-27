using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BKE.Desktop.Licensing;
using Xunit;

namespace BKE.Desktop.Licensing.Tests;

public sealed class LicensingClientTests
{
    [Fact]
    public void Default_handler_disables_redirects_and_proxy()
    {
        using var handler = BkeLicensingClient.CreateDefaultHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public async Task EnsureAuthorized_native_desktop_opens_agent_center_and_reauthorizes()
    {
        var calls = 0;
        using var http = new HttpClient(new CallbackHandler(async request =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.EndsWith("/v1/authorize", request.RequestUri!.AbsoluteUri);
                return Json(HttpStatusCode.OK, "{\"authorized\":false,\"reason\":\"activation_required\"}");
            }

            if (calls == 2)
            {
                Assert.EndsWith("/v1/license-center/open", request.RequestUri!.AbsoluteUri);
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var correlation = body.RootElement.GetProperty("correlation_id").GetString();
                return Json(HttpStatusCode.OK,
                    $"{{\"outcome\":\"authorization_refreshed\",\"reason\":\"\",\"correlation_id\":\"{correlation}\"}}");
            }

            Assert.EndsWith("/v1/authorize", request.RequestUri!.AbsoluteUri);
            return Json(HttpStatusCode.OK, "{\"authorized\":true,\"reason\":\"authorized\"}");
        }));

        using var client = BkeLicensingClient.Create(http);
        var result = await client.EnsureAuthorizedAsync(
            "bke-product", "1.0.0", "installation-1",
            new LicensingFlowOptions
            {
                ActivationInteraction = ActivationInteraction.NativeDesktop,
                AuthorizationRefreshTimeout = TimeSpan.FromSeconds(2),
                AuthorizationRefreshInterval = TimeSpan.FromMilliseconds(10)
            });

        Assert.Equal(AuthorizationStatus.Authorized, result.Status);
        Assert.True(result.Authorized);
        Assert.Equal("authorized", result.Reason);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("lease_expired")]
    [InlineData("lease_version_rejected")]
    [InlineData("lease_revoked")]
    [InlineData("lease_superseded")]
    [InlineData("lease_authority_mismatch")]
    [InlineData("unverifiable_signed_lease")]
    public async Task Authorize_maps_recoverable_authority_failures(string reason)
    {
        using var http = new HttpClient(new CallbackHandler(_ => Task.FromResult(
            Json(HttpStatusCode.OK, $"{{\"authorized\":false,\"reason\":\"{reason}\"}}"))));

        using var client = BkeLicensingClient.Create(http);
        var result = await client.AuthorizeAsync(
            "bke-product", "1.0.0", "installation-1");

        Assert.Equal(AuthorizationStatus.RecoveryRequired, result.Status);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public async Task EnsureAuthorized_recovery_required_opens_agent_center_and_reauthorizes()
    {
        var calls = 0;
        using var http = new HttpClient(new CallbackHandler(async request =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.EndsWith("/v1/authorize", request.RequestUri!.AbsoluteUri);
                return Json(HttpStatusCode.OK, "{\"authorized\":false,\"reason\":\"lease_version_rejected\"}");
            }

            if (calls == 2)
            {
                Assert.EndsWith("/v1/license-center/open", request.RequestUri!.AbsoluteUri);
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var correlation = body.RootElement.GetProperty("correlation_id").GetString();
                return Json(HttpStatusCode.OK,
                    $"{{\"outcome\":\"authorization_refreshed\",\"reason\":\"\",\"correlation_id\":\"{correlation}\"}}");
            }

            Assert.EndsWith("/v1/authorize", request.RequestUri!.AbsoluteUri);
            return Json(HttpStatusCode.OK, "{\"authorized\":true,\"reason\":\"authorized\"}");
        }));

        using var client = BkeLicensingClient.Create(http);
        var result = await client.EnsureAuthorizedAsync(
            "bke-product", "1.0.0", "installation-1",
            new LicensingFlowOptions
            {
                ActivationInteraction = ActivationInteraction.NativeDesktop,
                AuthorizationRefreshTimeout = TimeSpan.FromSeconds(2),
                AuthorizationRefreshInterval = TimeSpan.FromMilliseconds(10)
            });

        Assert.Equal(AuthorizationStatus.Authorized, result.Status);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task EnsureAuthorized_none_returns_recovery_required_without_presenting_ui()
    {
        var calls = 0;
        using var http = new HttpClient(new CallbackHandler(_ =>
        {
            calls++;
            return Task.FromResult(Json(HttpStatusCode.OK,
                "{\"authorized\":false,\"reason\":\"lease_expired\"}"));
        }));

        using var client = BkeLicensingClient.Create(http);
        var result = await client.EnsureAuthorizedAsync(
            "bke-product", "1.0.0", "installation-1",
            new LicensingFlowOptions { ActivationInteraction = ActivationInteraction.None });

        Assert.Equal(AuthorizationStatus.RecoveryRequired, result.Status);
        Assert.Equal("lease_expired", result.Reason);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task EnsureAuthorized_none_returns_activation_required_without_presenting_ui()
    {
        var calls = 0;
        using var http = new HttpClient(new CallbackHandler(request =>
        {
            calls++;
            Assert.EndsWith("/v1/authorize", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(HttpStatusCode.OK,
                "{\"authorized\":false,\"reason\":\"activation_required\"}"));
        }));

        using var client = BkeLicensingClient.Create(http);
        var result = await client.EnsureAuthorizedAsync(
            "bke-product", "1.0.0", "installation-1",
            new LicensingFlowOptions { ActivationInteraction = ActivationInteraction.None });

        Assert.Equal(AuthorizationStatus.ActivationRequired, result.Status);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(ActivationInteraction.SystemBrowser, "activation_interaction_not_supported:system_browser")]
    [InlineData(ActivationInteraction.CommandLine, "activation_interaction_not_supported:command_line")]
    public async Task EnsureAuthorized_uncertified_presentations_fail_explicitly(
        ActivationInteraction interaction,
        string expectedReason)
    {
        var calls = 0;
        using var http = new HttpClient(new CallbackHandler(_ =>
        {
            calls++;
            return Task.FromResult(Json(HttpStatusCode.OK,
                "{\"authorized\":false,\"reason\":\"activation_required\"}"));
        }));

        using var client = BkeLicensingClient.Create(http);
        var result = await client.EnsureAuthorizedAsync(
            "bke-product", "1.0.0", "installation-1",
            new LicensingFlowOptions { ActivationInteraction = interaction });

        Assert.Equal(AuthorizationStatus.Unsupported, result.Status);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task EnsureAuthorized_maps_native_cancellation_without_reauthorizing()
    {
        var calls = 0;
        using var http = new HttpClient(new CallbackHandler(async request =>
        {
            calls++;
            if (calls == 1)
            {
                return Json(HttpStatusCode.OK,
                    "{\"authorized\":false,\"reason\":\"activation_required\"}");
            }

            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var correlation = body.RootElement.GetProperty("correlation_id").GetString();
            return Json(HttpStatusCode.OK,
                $"{{\"outcome\":\"cancelled\",\"reason\":\"user_cancelled\",\"correlation_id\":\"{correlation}\"}}");
        }));

        using var client = BkeLicensingClient.Create(http);
        var result = await client.EnsureAuthorizedAsync(
            "bke-product", "1.0.0", "installation-1");

        Assert.Equal(AuthorizationStatus.ActivationCancelled, result.Status);
        Assert.Equal("user_cancelled", result.Reason);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OpenLicenseCenter_rejects_wrong_correlation_id()
    {
        using var http = new HttpClient(new CallbackHandler(_ => Task.FromResult(
            Json(HttpStatusCode.OK,
                "{\"outcome\":\"authorization_refreshed\",\"reason\":\"\",\"correlation_id\":\"wrong\"}"))));

        using var client = BkeLicensingClient.Create(http);
        var result = await client.OpenLicenseCenterAsync(
            "bke-product", "1.0.0", "installation-1");

        Assert.Equal(LicenseCenterStatus.InvalidResponse, result.Status);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class CallbackHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> callback;

        internal CallbackHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback)
        {
            this.callback = callback;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request);
    }
}

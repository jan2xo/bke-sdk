using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BKE.Notifications;
using Xunit;

namespace BKE.Notifications.Tests;

public sealed class NotificationAgentInboxClientTests
{
    private const string InstallationId = "install-render-dock-1";

    [Fact]
    public void Concrete_agent_inbox_is_not_a_notification_publisher()
    {
        Assert.True(typeof(INotificationFeedReader).IsAssignableFrom(typeof(BkeNotificationInboxClient)));
        Assert.True(typeof(INotificationLifecycle).IsAssignableFrom(typeof(BkeNotificationInboxClient)));
        Assert.True(typeof(INotificationUnreadCounter).IsAssignableFrom(typeof(BkeNotificationInboxClient)));
        Assert.False(typeof(INotificationPublisher).IsAssignableFrom(typeof(BkeNotificationInboxClient)));
        Assert.False(typeof(INotificationClient).IsAssignableFrom(typeof(BkeNotificationInboxClient)));
    }

    [Fact]
    public async Task Feed_posts_only_installation_bound_context_and_query_to_fixed_agent_route()
    {
        string? path = null;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            path = request.RequestUri?.ToString();
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, FeedJson());
        });

        using var http = new HttpClient(handler);
        using var client = BkeNotificationInboxClient.Create("bke-render-dock", "1.0.2", InstallationId, http);

        var result = await client.GetFeedAsync(new NotificationFeedQuery(25));

        Assert.True(result.Succeeded);
        Assert.Equal("http://127.0.0.1:43873/v1/notifications/feed", path);
        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal("bke-render-dock", root.GetProperty("product_id").GetString());
        Assert.Equal("1.0.2", root.GetProperty("version").GetString());
        Assert.Equal(InstallationId, root.GetProperty("installation_id").GetString());
        Assert.Equal(25, root.GetProperty("limit").GetInt32());
        Assert.False(root.GetProperty("include_dismissed").GetBoolean());
        Assert.Equal(5, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task Agent_owned_notification_maps_to_public_contract()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, FeedJson())));
        using var http = new HttpClient(handler);
        using var client = BkeNotificationInboxClient.Create("bke-render-dock", "1.0.2", InstallationId, http);

        var feed = await client.GetFeedAsync(new NotificationFeedQuery());

        var item = Assert.Single(feed.Items);
        Assert.Equal("notice-1", item.Id);
        Assert.Equal("bke-licensing-agent", item.Source);
        Assert.Equal("License required", item.Title);
        Assert.Contains("commercial license", item.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(NotificationCategory.Licensing, item.Category);
        Assert.Equal(NotificationSeverity.Warning, item.Severity);
        Assert.Equal(NotificationState.Unread, item.State);
        Assert.Empty(item.Actions);
    }

    [Theory]
    [InlineData("mark-read", NotificationOperationStatus.Succeeded)]
    [InlineData("dismiss", NotificationOperationStatus.Succeeded)]
    public async Task Lifecycle_posts_installation_bound_notification_id_to_expected_agent_route(
        string operation,
        NotificationOperationStatus expected)
    {
        string? path = null;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            path = request.RequestUri?.ToString();
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, OperationJson("Succeeded"));
        });
        using var http = new HttpClient(handler);
        using var client = BkeNotificationInboxClient.Create("bke-render-dock", "1.0.2", InstallationId, http);

        var result = operation == "mark-read"
            ? await client.MarkReadAsync("notice-1")
            : await client.DismissAsync("notice-1");

        Assert.Equal(expected, result.Status);
        Assert.Equal($"http://127.0.0.1:43873/v1/notifications/{operation}", path);
        using var document = JsonDocument.Parse(body!);
        Assert.Equal("notice-1", document.RootElement.GetProperty("notification_id").GetString());
        Assert.Equal(InstallationId, document.RootElement.GetProperty("installation_id").GetString());
        Assert.Equal(4, document.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task Unread_count_maps_to_public_contract()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, UnreadJson(3))));
        using var http = new HttpClient(handler);
        using var client = BkeNotificationInboxClient.Create("bke-render-dock", "1.0.2", InstallationId, http);

        var result = await client.GetUnreadCountAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task Incompatible_capability_fails_closed()
    {
        var json = FeedJson().Replace("\"bke.notifications\"", "\"wrong.capability\"");
        var handler = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, json)));
        using var http = new HttpClient(handler);
        using var client = BkeNotificationInboxClient.Create("bke-render-dock", "1.0.2", InstallationId, http);

        var result = await client.GetFeedAsync(new NotificationFeedQuery());

        Assert.False(result.Succeeded);
        Assert.Equal(NotificationErrorCode.ProtocolFailure, result.Error!.Code);
    }

    [Fact]
    public async Task Agent_failure_maps_to_typed_error()
    {
        var json = """
            {
              "capability_id":"bke.notifications",
              "contract_version":1,
              "status":"Failed",
              "error":{"code":"ProviderUnavailable","message":"Agent unavailable.","retryable":true}
            }
            """;
        var handler = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, json)));
        using var http = new HttpClient(handler);
        using var client = BkeNotificationInboxClient.Create("bke-render-dock", "1.0.2", InstallationId, http);

        var result = await client.GetFeedAsync(new NotificationFeedQuery());

        Assert.False(result.Succeeded);
        Assert.Equal(NotificationErrorCode.ProviderUnavailable, result.Error!.Code);
        Assert.True(result.Error.Retryable);
    }

    private static string FeedJson() => """
        {
          "capability_id":"bke.notifications",
          "contract_version":1,
          "status":"Succeeded",
          "items":[{
            "id":"notice-1",
            "source":"bke-licensing-agent",
            "title":"License required",
            "body":"A commercial license is required to continue.",
            "category":"Licensing",
            "severity":"Warning",
            "created_at":"2026-09-16T15:00:00+00:00",
            "expires_at":null,
            "state":"Unread",
            "actions":[]
          }],
          "error":null
        }
        """;

    private static string OperationJson(string status) => $$"""
        {
          "capability_id":"bke.notifications",
          "contract_version":1,
          "status":"{{status}}",
          "error":null
        }
        """;

    private static string UnreadJson(int count) => $$"""
        {
          "capability_id":"bke.notifications",
          "contract_version":1,
          "status":"Succeeded",
          "count":{{count}},
          "error":null
        }
        """;

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> responder;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        {
            this.responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request);
    }
}

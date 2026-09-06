using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BKE.Notifications;
using Xunit;

namespace BKE.Notifications.Tests;

public class NotificationCapabilitySegregationTests
{
    [Fact]
    public async Task Producer_depends_only_on_publish_capability()
    {
        var publisher = new RecordingPublisher();
        var producer = new ProductNotificationProducer(publisher);

        var result = await producer.PublishAsync();

        Assert.Equal(NotificationPublishStatus.Accepted, result.Status);
        Assert.Equal(typeof(INotificationPublisher), producer.DependencyType);
        Assert.NotNull(publisher.LastRequest);
        Assert.Equal("bke-render-dock", publisher.LastRequest!.Source);
    }

    [Fact]
    public void Publish_capability_does_not_expose_feed_or_lifecycle_operations()
    {
        var methods = typeof(INotificationPublisher)
            .GetMethods()
            .Select(method => method.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(new[] { "PublishAsync" }, methods);
    }

    [Fact]
    public void Feed_reader_does_not_expose_publish_or_mutation_operations()
    {
        var methods = typeof(INotificationFeedReader)
            .GetMethods()
            .Select(method => method.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(new[] { "GetFeedAsync" }, methods);
    }

    [Fact]
    public void Lifecycle_capability_contains_only_state_mutation_operations()
    {
        var methods = typeof(INotificationLifecycle)
            .GetMethods()
            .Select(method => method.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(new[] { "DismissAsync", "MarkReadAsync" }, methods);
    }

    [Fact]
    public void Full_client_composes_all_notification_capabilities()
    {
        var contracts = typeof(INotificationClient)
            .GetInterfaces()
            .OrderBy(type => type.Name)
            .ToArray();

        Assert.Equal(
            new[]
            {
                typeof(INotificationFeedReader),
                typeof(INotificationLifecycle),
                typeof(INotificationPublisher),
                typeof(INotificationUnreadCounter)
            }.OrderBy(type => type.Name),
            contracts);
    }

    private sealed class ProductNotificationProducer
    {
        private readonly INotificationPublisher publisher;

        public ProductNotificationProducer(INotificationPublisher publisher)
        {
            this.publisher = publisher;
        }

        public Type DependencyType => typeof(INotificationPublisher);

        public Task<NotificationPublishResult> PublishAsync(
            CancellationToken cancellationToken = default) =>
            publisher.PublishAsync(
                new NotificationPublishRequest(
                    source: "bke-render-dock",
                    title: "Render complete",
                    body: "The render completed successfully.",
                    category: NotificationCategory.Product,
                    severity: NotificationSeverity.Success),
                cancellationToken);
    }

    private sealed class RecordingPublisher : INotificationPublisher
    {
        public NotificationPublishRequest? LastRequest { get; private set; }

        public Task<NotificationPublishResult> PublishAsync(
            NotificationPublishRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(NotificationPublishResult.Accepted("notification-1"));
        }
    }
}

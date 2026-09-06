using System.Collections.Immutable;
using BKE.RateLimiting;
using Xunit;

namespace BKE.RateLimiting.Tests;

public sealed class RateLimitBoundaryTests
{
    public static IEnumerable<object[]> Algorithms()
    {
        yield return new object[] { RateLimitPolicy.FixedWindow("p", 1, TimeSpan.FromSeconds(10)) };
        yield return new object[] { RateLimitPolicy.SlidingWindow("p", 1, TimeSpan.FromSeconds(10)) };
        yield return new object[] { RateLimitPolicy.TokenBucket("p", 1, 1, TimeSpan.FromSeconds(10)) };
    }

    [Theory, MemberData(nameof(Algorithms))]
    public async Task One_permit_and_exact_rollover(RateLimitPolicy policy)
    {
        var clock = new TestTimeProvider();
        using var store = new InMemoryRateLimitStore(clock);
        var limiter = new BkeRateLimiter(store);
        var request = new RateLimitRequest("key", policy);
        Assert.True((await limiter.EvaluateAsync(request)).UsageRecorded);
        var denied = await limiter.EvaluateAsync(request);
        Assert.Equal(RateLimitDecision.Throttled, denied.Decision);
        Assert.Equal(TimeSpan.FromSeconds(10), denied.RetryAfter);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True((await limiter.EvaluateAsync(request)).UsageRecorded);
        Assert.Equal(RateLimitDecision.Throttled, (await limiter.EvaluateAsync(request)).Decision);
    }

    [Theory, MemberData(nameof(Algorithms))]
    public async Task Utc_rollback_and_forward_jump_cannot_refill_quota(RateLimitPolicy policy)
    {
        var clock = new TestTimeProvider();
        using var store = new InMemoryRateLimitStore(clock);
        var limiter = new BkeRateLimiter(store);
        var request = new RateLimitRequest("key", policy);
        await limiter.EvaluateAsync(request);
        foreach (var utc in new[] { DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue.AddDays(-1) })
        {
            clock.SetUtcNow(utc);
            var result = await limiter.EvaluateAsync(request);
            Assert.Equal(RateLimitDecision.Throttled, result.Decision);
            Assert.Equal(TimeSpan.FromSeconds(10), result.RetryAfter);
            Assert.True(result.ResetAt >= result.ObservedAt);
            Assert.Equal(0, await store.PruneExpiredAsync());
        }
    }

    [Fact]
    public async Task Fixed_reset_does_not_extend_on_acceptance()
    {
        var clock = new TestTimeProvider();
        var firstTime = clock.GetUtcNow();
        using var store = new InMemoryRateLimitStore(clock);
        var limiter = new BkeRateLimiter(store);
        var request = new RateLimitRequest("key", RateLimitPolicy.FixedWindow("p", 2, TimeSpan.FromSeconds(10)));
        await limiter.EvaluateAsync(request);
        clock.Advance(TimeSpan.FromSeconds(8));
        var allowed = await limiter.EvaluateAsync(request);
        Assert.Equal(firstTime.AddSeconds(10), allowed.ResetAt);
        var denied = await limiter.EvaluateAsync(request);
        Assert.Equal(TimeSpan.FromSeconds(2), denied.RetryAfter);
        Assert.Equal(allowed.ResetAt, denied.ResetAt);
    }

    [Fact]
    public async Task Sliding_retry_and_full_recovery_are_distinct_and_eviction_is_inclusive()
    {
        var clock = new TestTimeProvider();
        var origin = clock.GetUtcNow();
        using var store = new InMemoryRateLimitStore(clock);
        var limiter = new BkeRateLimiter(store);
        var request = new RateLimitRequest("key", RateLimitPolicy.SlidingWindow("p", 2, TimeSpan.FromSeconds(10)));
        await limiter.EvaluateAsync(request);
        clock.Advance(TimeSpan.FromSeconds(1));
        await limiter.EvaluateAsync(request);
        var denied = await limiter.EvaluateAsync(request);
        Assert.Equal(TimeSpan.FromSeconds(9), denied.RetryAfter);
        Assert.Equal(origin.AddSeconds(11), denied.ResetAt);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(RateLimitDecision.Allowed, (await limiter.EvaluateAsync(request)).Decision);
        denied = await limiter.EvaluateAsync(request);
        Assert.Equal(TimeSpan.FromSeconds(1), denied.RetryAfter);
        Assert.Equal(origin.AddSeconds(20), denied.ResetAt);
    }

    [Fact]
    public async Task Fractional_bucket_retry_ceil_and_full_recovery()
    {
        var clock = new TestTimeProvider();
        var origin = clock.GetUtcNow();
        using var store = new InMemoryRateLimitStore(clock);
        var limiter = new BkeRateLimiter(store);
        var request = new RateLimitRequest("key", RateLimitPolicy.TokenBucket("p", 2, 0.25, TimeSpan.FromSeconds(1)));
        await limiter.EvaluateAsync(request);
        await limiter.EvaluateAsync(request);
        var denied = await limiter.EvaluateAsync(request);
        Assert.Equal(TimeSpan.FromSeconds(4), denied.RetryAfter);
        Assert.Equal(origin.AddSeconds(8), denied.ResetAt);
        clock.Advance(TimeSpan.FromMilliseconds(3999));
        Assert.Equal(TimeSpan.FromMilliseconds(1), (await limiter.EvaluateAsync(request)).RetryAfter);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True((await limiter.EvaluateAsync(request)).UsageRecorded);
        Assert.Equal(RateLimitDecision.Throttled, (await limiter.EvaluateAsync(request)).Decision);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, (await limiter.EvaluateAsync(request)).Remaining);
    }

    [Fact]
    public async Task Event_capacity_failure_preserves_existing_sliding_quota()
    {
        var clock = new TestTimeProvider();
        using var store = new InMemoryRateLimitStore(clock, maxPartitions: 10, maxRetainedEvents: 2);
        var limiter = new BkeRateLimiter(store);
        var policy = RateLimitPolicy.SlidingWindow("p", 2, TimeSpan.FromSeconds(10), RateLimitFailureMode.FailOpen);
        await limiter.EvaluateAsync(new("a", policy));
        await limiter.EvaluateAsync(new("a", policy));
        var blocked = await limiter.EvaluateAsync(new("b", policy));
        Assert.Equal(RateLimitDecision.Blocked, blocked.Decision);
        Assert.Equal(RateLimitFailure.StateCapacityExhausted, blocked.Failure);
        Assert.Equal(new InMemoryRateLimitStoreStatistics(1, 2), await store.GetStatisticsAsync());
        Assert.Equal(RateLimitDecision.Throttled, (await limiter.EvaluateAsync(new("a", policy))).Decision);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True((await limiter.EvaluateAsync(new("b", policy))).UsageRecorded);
        Assert.Equal(new InMemoryRateLimitStoreStatistics(1, 1), await store.GetStatisticsAsync());
    }

    [Fact]
    public async Task Cancellation_after_transition_before_commit_preserves_quota()
    {
        var clock = new TestTimeProvider();
        using var store = new InMemoryRateLimitStore(clock);
        using var cancellation = new CancellationTokenSource();
        var policy = RateLimitPolicy.FixedWindow("p", 1, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ExecuteAsync(
            new(new("p", "key"), Guid.NewGuid()), context =>
            {
                var state = new RateLimitStateSnapshot(policy, context.ElapsedTicks, TimeSpan.TicksPerSecond,
                    0, 1, ImmutableQueue<long>.Empty, 0);
                var result = RateLimitResult.Recorded(RateLimitDecision.Allowed, "p", 0, TimeSpan.Zero,
                    context.ObservedAt.AddSeconds(1), context.ObservedAt);
                cancellation.Cancel();
                return new(state, result);
            }, cancellation.Token));
        Assert.Equal(0, (await store.GetStatisticsAsync()).PartitionCount);
        Assert.True((await new BkeRateLimiter(store).EvaluateAsync(new("key", policy))).UsageRecorded);
    }

    [Fact]
    public async Task Huge_delays_saturate_metadata_without_reclaiming_debt()
    {
        var clock = new TestTimeProvider();
        clock.SetUtcNow(DateTimeOffset.MaxValue.AddTicks(-1));
        using var store = new InMemoryRateLimitStore(clock);
        var limiter = new BkeRateLimiter(store);
        var request = new RateLimitRequest("key", RateLimitPolicy.TokenBucket("p", 1, 0.000001, TimeSpan.FromDays(365)));
        var allowed = await limiter.EvaluateAsync(request);
        Assert.Equal(DateTimeOffset.MaxValue, allowed.ResetAt);
        var denied = await limiter.EvaluateAsync(request);
        Assert.Equal(RateLimitDecision.Throttled, denied.Decision);
        Assert.Equal(TimeSpan.MaxValue, denied.RetryAfter);
        Assert.Equal(DateTimeOffset.MaxValue, denied.ResetAt);
        Assert.Equal(0, await store.PruneExpiredAsync());
    }

    [Fact]
    public async Task Structured_identity_and_debug_output_do_not_leak_key()
    {
        using var store = new InMemoryRateLimitStore(new TestTimeProvider());
        var limiter = new BkeRateLimiter(store);
        var a = new RateLimitRequest("c", RateLimitPolicy.FixedWindow("a:b", 1, TimeSpan.FromSeconds(1)));
        var b = new RateLimitRequest("b:c", RateLimitPolicy.FixedWindow("a", 1, TimeSpan.FromSeconds(1)));
        Assert.True((await limiter.EvaluateAsync(a)).UsageRecorded);
        Assert.True((await limiter.EvaluateAsync(b)).UsageRecorded);
        const string secret = "private@example.com";
        var privateRequest = new RateLimitRequest(secret, a.Policy);
        Assert.DoesNotContain(secret, privateRequest.ToString());
        Assert.DoesNotContain(secret, new RateLimitStoreRequest(new("p", secret), Guid.NewGuid()).ToString());
        Assert.DoesNotContain(secret, (await limiter.EvaluateAsync(privateRequest)).ToString());
    }

    [Fact]
    public void Configuration_bounds_reject_absurd_values()
    {
        Assert.Throws<ArgumentException>(() => new RateLimitRequest("", RateLimitPolicy.FixedWindow("p", 1, TimeSpan.FromTicks(1))));
        Assert.Throws<ArgumentException>(() => RateLimitPolicy.FixedWindow(new string('x', 257), 1, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RateLimitPolicy.FixedWindow("p", int.MaxValue, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RateLimitPolicy.SlidingWindow("p", 1, TimeSpan.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => RateLimitPolicy.TokenBucket("p", int.MaxValue, 1, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RateLimitPolicy.TokenBucket("p", 1, double.Epsilon, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RateLimitPolicy.TokenBucket("p", 1, double.MaxValue, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RateLimitPolicy.FixedWindow("p", 1, TimeSpan.FromSeconds(1), (RateLimitFailureMode)99));
    }
}

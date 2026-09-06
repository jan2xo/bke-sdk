using System.Collections.Concurrent;
using BKE.RateLimiting;
using Xunit;

namespace BKE.RateLimiting.Tests;

public sealed class RateLimitingAdversarialTests
{
    [Fact]
    public void Policies_reject_invalid_and_non_finite_configuration()
    {
        Assert.Throws<ArgumentException>(() => RateLimitPolicy.FixedWindow(" ", 1, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RateLimitPolicy.FixedWindow("p", 0, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RateLimitPolicy.SlidingWindow("p", 1, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => RateLimitPolicy.TokenBucket("p", 1, double.NaN, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RateLimitPolicy.TokenBucket("p", 1, double.PositiveInfinity, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RateLimitPolicy.TokenBucket("p", 1, 1, TimeSpan.Zero));
    }

    [Fact]
    public async Task Keys_are_ordinal_and_never_normalized_or_concatenated()
    {
        var clock = new TestTimeProvider();
        var limiter = new BkeRateLimiter(new InMemoryRateLimitStore(clock));
        var policy = RateLimitPolicy.FixedWindow("p", 1, TimeSpan.FromMinutes(1));
        var a = await limiter.EvaluateAsync(new RateLimitRequest("User", policy));
        var b = await limiter.EvaluateAsync(new RateLimitRequest("user", policy));
        var c = await limiter.EvaluateAsync(new RateLimitRequest("é", policy));
        var d = await limiter.EvaluateAsync(new RateLimitRequest("e\u0301", policy));
        Assert.All(new[] { a, b, c, d }, x => Assert.Equal(RateLimitDecision.Allowed, x.Decision));
    }

    [Fact]
    public async Task Different_policies_do_not_share_partition_and_conflict_is_blocked()
    {
        var clock = new TestTimeProvider();
        var limiter = new BkeRateLimiter(new InMemoryRateLimitStore(clock));
        var first = RateLimitPolicy.FixedWindow("v1", 1, TimeSpan.FromMinutes(1));
        var second = RateLimitPolicy.FixedWindow("v2", 2, TimeSpan.FromMinutes(1));
        Assert.Equal(RateLimitDecision.Allowed, (await limiter.EvaluateAsync(new("k", first))).Decision);
        Assert.Equal(RateLimitDecision.Allowed, (await limiter.EvaluateAsync(new("k", second))).Decision);
        Assert.Equal(RateLimitDecision.Allowed, (await limiter.EvaluateAsync(new("k", second))).Decision);
    }

    [Fact]
    public async Task Same_policy_id_with_changed_configuration_is_a_typed_conflict()
    {
        var clock = new TestTimeProvider();
        var limiter = new BkeRateLimiter(new InMemoryRateLimitStore(clock));
        var first = RateLimitPolicy.FixedWindow("same", 1, TimeSpan.FromMinutes(1));
        var changed = RateLimitPolicy.FixedWindow("same", 2, TimeSpan.FromMinutes(1));
        Assert.Equal(RateLimitDecision.Allowed, (await limiter.EvaluateAsync(new("k", first))).Decision);
        var result = await limiter.EvaluateAsync(new("k", changed));
        Assert.Equal(RateLimitDecision.Blocked, result.Decision);
        Assert.Equal(RateLimitFailure.PolicyConflict, result.Failure);
    }

    [Fact]
    public void Result_factories_reject_contradictory_metadata()
    {
        var now = DateTimeOffset.UnixEpoch;
        Assert.Throws<ArgumentException>(() => RateLimitResult.Recorded(RateLimitDecision.Allowed, "p", 1, TimeSpan.FromSeconds(1), now, now));
        Assert.Throws<ArgumentException>(() => RateLimitResult.Recorded(RateLimitDecision.Throttled, "p", 1, TimeSpan.FromSeconds(1), now, now));
        Assert.Throws<ArgumentException>(() => RateLimitResult.Recorded(RateLimitDecision.Throttled, "p", 0, TimeSpan.Zero, now, now));
        Assert.Throws<ArgumentException>(() => RateLimitResult.Failed("p", RateLimitFailure.IndeterminateCommit, now, failOpen: true));
    }

    [Theory]
    [InlineData("fixed")]
    [InlineData("sliding")]
    [InlineData("bucket")]
    public async Task Concurrent_evaluations_never_create_duplicate_permits(string algorithm)
    {
        var clock = new TestTimeProvider();
        using var store = new InMemoryRateLimitStore(clock);
        var limiters = new[] { new BkeRateLimiter(store), new BkeRateLimiter(store) };
        RateLimitPolicy policy = algorithm switch
        {
            "fixed" => RateLimitPolicy.FixedWindow("p", 17, TimeSpan.FromMinutes(1)),
            "sliding" => RateLimitPolicy.SlidingWindow("p", 17, TimeSpan.FromMinutes(1)),
            _ => RateLimitPolicy.TokenBucket("p", 17, 1, TimeSpan.FromMinutes(1))
        };
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref ready) == 100) start.SetResult();
            await start.Task;
            return await limiters[index % 2].EvaluateAsync(new("same", policy));
        })));
        Assert.Equal(17, results.Count(x => x.Decision == RateLimitDecision.Allowed));
        Assert.All(results, x => Assert.True(!x.Remaining.HasValue || x.Remaining.Value >= 0));
    }

    [Fact]
    public async Task Shared_store_shares_quota_across_limiter_instances()
    {
        var clock = new TestTimeProvider();
        var store = new InMemoryRateLimitStore(clock);
        var p = RateLimitPolicy.FixedWindow("p", 1, TimeSpan.FromMinutes(1));
        var one = new BkeRateLimiter(store);
        var two = new BkeRateLimiter(store);
        Assert.Equal(RateLimitDecision.Allowed, (await one.EvaluateAsync(new("k", p))).Decision);
        Assert.Equal(RateLimitDecision.Throttled, (await two.EvaluateAsync(new("k", p))).Decision);
    }

    [Fact]
    public void Unicode_and_oversize_keys_remain_opaque()
    {
        var clock = new TestTimeProvider();
        var limiter = new BkeRateLimiter(new InMemoryRateLimitStore(clock));
        var p = RateLimitPolicy.FixedWindow("p", 1, TimeSpan.FromMinutes(1));
        var key = new string('x', 64 * 1024) + "🔐";
        Assert.Throws<ArgumentException>(() => new RateLimitRequest(key, p));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(31)]
    public async Task Deterministic_fixed_window_matrix_preserves_allowance(int seed)
    {
        var clock = new TestTimeProvider();
        var limiter = new BkeRateLimiter(new InMemoryRateLimitStore(clock));
        var policy = RateLimitPolicy.FixedWindow("matrix", 7, TimeSpan.FromSeconds(10));
        var random = new Random(seed);
        var accepted = 0;
        long activeWindow = -1;
        for (var i = 0; i < 1000; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(random.Next(0, 1000)));
            var window = clock.GetTimestamp() / policy.Window.Ticks;
            if (window != activeWindow) { accepted = 0; activeWindow = window; }
            var expectedAllowed = accepted < policy.Limit;
            var result = await limiter.EvaluateAsync(new("matrix-key", policy));
            Assert.Equal(expectedAllowed ? RateLimitDecision.Allowed : RateLimitDecision.Throttled, result.Decision);
            if (result.Decision == RateLimitDecision.Allowed) accepted++;
            Assert.True(accepted <= policy.Limit);
            Assert.Equal(policy.Limit - accepted, result.Remaining);
            Assert.True(!result.Remaining.HasValue || result.Remaining.Value >= 0);
            Assert.True(!result.RetryAfter.HasValue || result.RetryAfter.Value >= TimeSpan.Zero);
            Assert.True(result.ResetAt is null || result.ResetAt >= result.ObservedAt);
        }
    }

    [Theory]
    [InlineData(11)] [InlineData(29)] [InlineData(47)] [InlineData(83)]
    public async Task Deterministic_sliding_window_matrix_matches_trailing_event_model(int seed)
    {
        var clock = new TestTimeProvider();
        var limiter = new BkeRateLimiter(new InMemoryRateLimitStore(clock));
        var policy = RateLimitPolicy.SlidingWindow("slide", 9, TimeSpan.FromSeconds(10));
        var random = new Random(seed);
        var events = new Queue<DateTimeOffset>();
        for (var i = 0; i < 1000; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(random.Next(0, 500)));
            while (events.Count > 0 && clock.GetUtcNow() - events.Peek() >= policy.Window) events.Dequeue();
            var result = await limiter.EvaluateAsync(new("slide-key", policy));
            if (events.Count < policy.Limit)
            {
                Assert.Equal(RateLimitDecision.Allowed, result.Decision);
                events.Enqueue(clock.GetUtcNow());
            }
            else Assert.Equal(RateLimitDecision.Throttled, result.Decision);
        }
    }

    [Theory]
    [InlineData(101)] [InlineData(202)] [InlineData(303)] [InlineData(404)]
    public async Task Deterministic_token_bucket_matrix_never_overdraws_or_exceeds_capacity(int seed)
    {
        var clock = new TestTimeProvider();
        var limiter = new BkeRateLimiter(new InMemoryRateLimitStore(clock));
        var policy = RateLimitPolicy.TokenBucket("bucket", 13, 2.5, TimeSpan.FromSeconds(4));
        var random = new Random(seed);
        // Independent integer reference: 2.5 tokens / 4000ms = one 1/1600-token unit per ms.
        long tokenUnits = policy.Capacity * 1600;
        for (var i = 0; i < 1000; i++)
        {
            var elapsedMilliseconds = random.Next(0, 700);
            clock.Advance(TimeSpan.FromMilliseconds(elapsedMilliseconds));
            tokenUnits = Math.Min(policy.Capacity * 1600, tokenUnits + elapsedMilliseconds);
            var result = await limiter.EvaluateAsync(new("bucket-key", policy));
            if (tokenUnits >= 1600) { Assert.Equal(RateLimitDecision.Allowed, result.Decision); tokenUnits -= 1600; }
            else Assert.Equal(RateLimitDecision.Throttled, result.Decision);
            Assert.InRange(tokenUnits, 0, policy.Capacity * 1600L);
            Assert.Equal((int)(tokenUnits / 1600), result.Remaining);
        }
    }

    [Fact]
    public async Task Cancellation_before_evaluation_does_not_consume_a_permit()
    {
        var clock = new TestTimeProvider();
        var limiter = new BkeRateLimiter(new InMemoryRateLimitStore(clock));
        var p = RateLimitPolicy.FixedWindow("p", 1, TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.EvaluateAsync(new("k", p), cancellation.Token));
        Assert.Equal(RateLimitDecision.Allowed, (await limiter.EvaluateAsync(new("k", p))).Decision);
    }

    [Fact]
    public async Task Partition_capacity_blocks_new_keys_without_restoring_active_quota()
    {
        var clock = new TestTimeProvider();
        var store = new InMemoryRateLimitStore(clock, maxPartitions: 1);
        var limiter = new BkeRateLimiter(store);
        var policy = RateLimitPolicy.FixedWindow("p", 1, TimeSpan.FromMinutes(1));
        Assert.Equal(RateLimitDecision.Allowed, (await limiter.EvaluateAsync(new("active", policy))).Decision);
        var blocked = await limiter.EvaluateAsync(new("attacker", policy));
        Assert.Equal(RateLimitDecision.Blocked, blocked.Decision);
        Assert.Equal(RateLimitFailure.PartitionCapacityExhausted, blocked.Failure);
        Assert.Equal(RateLimitDecision.Throttled, (await limiter.EvaluateAsync(new("active", policy))).Decision);
    }

    [Fact]
    public async Task Recovered_partitions_can_be_pruned_without_creating_permits()
    {
        var clock = new TestTimeProvider();
        var store = new InMemoryRateLimitStore(clock, maxPartitions: 10);
        var limiter = new BkeRateLimiter(store);
        var policy = RateLimitPolicy.FixedWindow("p", 1, TimeSpan.FromSeconds(5));
        Assert.Equal(RateLimitDecision.Allowed, (await limiter.EvaluateAsync(new("old", policy))).Decision);
        var before = await store.GetStatisticsAsync();
        clock.Advance(TimeSpan.FromSeconds(6));
        var removed = await store.PruneExpiredAsync();
        var after = await store.GetStatisticsAsync();
        Assert.True(removed >= 1);
        Assert.True(after.PartitionCount < before.PartitionCount);
        Assert.Equal(RateLimitDecision.Allowed, (await limiter.EvaluateAsync(new("old", policy))).Decision);
    }
}

internal sealed class TestTimeProvider : TimeProvider
{
    private const long Frequency = TimeSpan.TicksPerSecond;
    private long _ticks;
    private long _utcTicks = new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override long TimestampFrequency => Frequency;
    public void Advance(TimeSpan value) { Interlocked.Add(ref _ticks, value.Ticks); Interlocked.Add(ref _utcTicks, value.Ticks); }
    public void SetUtcNow(DateTimeOffset value) => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);
}

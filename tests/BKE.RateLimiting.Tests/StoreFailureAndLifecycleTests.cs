using BKE.RateLimiting;
using Xunit;

namespace BKE.RateLimiting.Tests;

public sealed class StoreFailureAndLifecycleTests
{
    [Fact]
    public async Task Known_precommit_outage_obeys_fail_open_or_fail_closed()
    {
        var clock = new TestTimeProvider();
        var open = new BkeRateLimiter(new FailingStore(clock, RateLimitFailure.StoreUnavailable));
        var closed = new BkeRateLimiter(new FailingStore(clock, RateLimitFailure.StoreUnavailable));
        var openResult = await open.EvaluateAsync(new("k", RateLimitPolicy.FixedWindow("open", 1, TimeSpan.FromMinutes(1), RateLimitFailureMode.FailOpen)));
        var closedResult = await closed.EvaluateAsync(new("k", RateLimitPolicy.FixedWindow("closed", 1, TimeSpan.FromMinutes(1))));
        Assert.Equal(RateLimitDecision.Allowed, openResult.Decision);
        Assert.Equal(RateLimitFailure.StoreUnavailable, openResult.Failure);
        Assert.Equal(RateLimitDecision.Blocked, closedResult.Decision);
        Assert.Equal(RateLimitFailure.StoreUnavailable, closedResult.Failure);
        Assert.Null(openResult.Remaining);
    }

    [Theory]
    [InlineData(RateLimitFailure.IndeterminateCommit)]
    [InlineData(RateLimitFailure.InvalidState)]
    [InlineData(RateLimitFailure.PartitionCapacityExhausted)]
    public async Task Fail_open_is_restricted_to_known_unavailable(RateLimitFailure failure)
    {
        var clock = new TestTimeProvider();
        var limiter = new BkeRateLimiter(new FailingStore(clock, failure));
        var result = await limiter.EvaluateAsync(new("k", RateLimitPolicy.FixedWindow("p", 1, TimeSpan.FromMinutes(1), RateLimitFailureMode.FailOpen)));
        Assert.Equal(RateLimitDecision.Blocked, result.Decision);
        Assert.Equal(failure, result.Failure);
    }

    [Fact]
    public async Task Indeterminate_commit_is_blocked_and_not_retried()
    {
        var clock = new TestTimeProvider();
        using var inner = new InMemoryRateLimitStore(clock);
        var store = new ThrowAfterCommitStore(inner);
        var limiter = new BkeRateLimiter(store);
        var p = RateLimitPolicy.FixedWindow("p", 2, TimeSpan.FromMinutes(1));
        var first = await limiter.EvaluateAsync(new("k", p));
        Assert.Equal(RateLimitDecision.Blocked, first.Decision);
        Assert.Equal(RateLimitFailure.IndeterminateCommit, first.Failure);
        Assert.Equal(1, store.Attempts);
        var direct = new BkeRateLimiter(inner);
        Assert.Equal(0, (await direct.EvaluateAsync(new("k", p))).Remaining);
        Assert.Equal(RateLimitDecision.Throttled, (await direct.EvaluateAsync(new("k", p))).Decision);
    }

    [Fact]
    public async Task Callback_throw_does_not_mutate_committed_quota()
    {
        var clock = new TestTimeProvider();
        using var store = new InMemoryRateLimitStore(clock);
        var partition = new RateLimitPartition("p", "k");
        var operation = new RateLimitStoreRequest(partition, Guid.NewGuid());
        var failed = await store.ExecuteAsync(operation, _ => throw new InvalidOperationException("secret@example.com"));
        Assert.Equal(RateLimitFailure.InvalidState, failed.Failure);
        Assert.DoesNotContain("secret@example.com", failed.ToString());
        var limiter = new BkeRateLimiter(store);
        var result = await limiter.EvaluateAsync(new("k", RateLimitPolicy.FixedWindow("p", 1, TimeSpan.FromMinutes(1))));
        Assert.Equal(RateLimitDecision.Allowed, result.Decision);
    }

    [Fact]
    public async Task Cancellation_while_waiting_does_not_consume_or_commit()
    {
        var clock = new TestTimeProvider();
        using var store = new InMemoryRateLimitStore(clock);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var gateTask = Task.Run(() => store.ExecuteAsync(new(new RateLimitPartition("p", "gate"), Guid.NewGuid()), _ =>
        {
            entered.Set();
            release.Wait();
            return new RateLimitStoreTransition(null, RateLimitResult.Failed("p", RateLimitFailure.StoreUnavailable, clock.GetUtcNow()));
        }));
        entered.Wait();
        using var cts = new CancellationTokenSource();
        var waiting = store.ExecuteAsync(new(new RateLimitPartition("p", "wait"), Guid.NewGuid()), _ => throw new InvalidOperationException(), cts.Token);
        try
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally { release.Set(); }
        await gateTask;
        Assert.Equal(RateLimitDecision.Allowed, (await new BkeRateLimiter(store).EvaluateAsync(
            new("wait", RateLimitPolicy.FixedWindow("p", 1, TimeSpan.FromSeconds(1))))).Decision);
    }

    [Fact]
    public async Task Clock_rollback_does_not_create_extra_fixed_window_permits()
    {
        var clock = new TestTimeProvider();
        var limiter = new BkeRateLimiter(new InMemoryRateLimitStore(clock));
        var p = RateLimitPolicy.FixedWindow("p", 1, TimeSpan.FromSeconds(10));
        Assert.Equal(RateLimitDecision.Allowed, (await limiter.EvaluateAsync(new("k", p))).Decision);
        clock.Advance(TimeSpan.FromSeconds(-5));
        var result = await limiter.EvaluateAsync(new("k", p));
        Assert.NotEqual(RateLimitDecision.Allowed, result.Decision);
    }

    [Fact]
    public async Task Sliding_events_and_bucket_debt_are_not_pruned_before_full_recovery()
    {
        var clock = new TestTimeProvider();
        using var store = new InMemoryRateLimitStore(clock);
        var limiter = new BkeRateLimiter(store);
        var sliding = RateLimitPolicy.SlidingWindow("s", 2, TimeSpan.FromSeconds(10));
        await limiter.EvaluateAsync(new("s", sliding)); clock.Advance(TimeSpan.FromSeconds(1)); await limiter.EvaluateAsync(new("s", sliding));
        Assert.Equal(0, await store.PruneExpiredAsync());
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0, await store.PruneExpiredAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await store.PruneExpiredAsync() >= 1);

        var bucket = RateLimitPolicy.TokenBucket("b", 5, 1, TimeSpan.FromSeconds(1));
        await limiter.EvaluateAsync(new("b", bucket));
        Assert.Equal(0, await store.PruneExpiredAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await store.PruneExpiredAsync() >= 1);
        await limiter.EvaluateAsync(new("b2", bucket)); await limiter.EvaluateAsync(new("b2", bucket)); await limiter.EvaluateAsync(new("b2", bucket)); await limiter.EvaluateAsync(new("b2", bucket)); await limiter.EvaluateAsync(new("b2", bucket));
        Assert.Equal(0, await store.PruneExpiredAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, await store.PruneExpiredAsync());
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.True(await store.PruneExpiredAsync() >= 1);
    }

    private sealed class FailingStore(TimeProvider clock, RateLimitFailure failure) : IRateLimitStore
    {
        public Task<RateLimitStoreResult> ExecuteAsync(RateLimitStoreRequest request, Func<RateLimitStoreContext, RateLimitStoreTransition> transition, CancellationToken cancellationToken = default) => Task.FromResult(RateLimitStoreResult.Failed(failure, clock.GetUtcNow()));
    }

    private sealed class ThrowAfterCommitStore(IRateLimitStore inner) : IRateLimitStore
    {
        public int Attempts { get; private set; }
        public async Task<RateLimitStoreResult> ExecuteAsync(RateLimitStoreRequest request, Func<RateLimitStoreContext, RateLimitStoreTransition> transition, CancellationToken cancellationToken = default)
        { Attempts++; await inner.ExecuteAsync(request, transition, cancellationToken); throw new InvalidOperationException("indeterminate"); }
    }

}

using System.Collections.Immutable;

namespace BKE.RateLimiting;

internal static class RateLimitEvaluator
{
    internal static RateLimitStoreTransition Evaluate(RateLimitPolicy policy, RateLimitStoreContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        var state = context.State;
        if (state is not null && !Equals(state.Policy, policy))
            return Fail(policy, context, RateLimitFailure.PolicyConflict);
        if (context.ElapsedTicks < 0 || (state is not null && !ValidState(policy, state, context.ElapsedTicks)))
            return Fail(policy, context, RateLimitFailure.InvalidState);
        return policy switch
        {
            FixedWindowPolicy p => Fixed(p, context),
            SlidingWindowPolicy p => Sliding(p, context),
            TokenBucketPolicy p => Bucket(p, context),
            _ => Fail(policy, context, RateLimitFailure.InvalidState)
        };
    }

    private static bool ValidState(RateLimitPolicy policy, RateLimitStateSnapshot state, long now)
    {
        if (state.LastTimestamp < 0 || state.LastTimestamp > now || state.WindowStart < 0 ||
            state.Events is null || state.Used < 0 || state.Tokens < 0 ||
            (state.RecoveryTimestamp is { } recovery && recovery < state.LastTimestamp)) return false;
        return policy switch
        {
            FixedWindowPolicy p => state.Used <= p.Limit && state.Events.IsEmpty && state.Tokens == 0 &&
                state.WindowStart == state.LastTimestamp / p.Window.Ticks * p.Window.Ticks,
            SlidingWindowPolicy p => state.Used <= p.Limit && state.Tokens == 0 &&
                (state.Used == 0 ? state.Events.IsEmpty : !state.Events.IsEmpty && state.Events.Peek() >= 0 &&
                    state.Events.Peek() <= state.LatestEventTimestamp && state.LatestEventTimestamp <= state.LastTimestamp &&
                    (state.Used != 1 || state.Events.Peek() == state.LatestEventTimestamp)),
            TokenBucketPolicy p => state.Used == 0 && state.Events.IsEmpty && state.Tokens <= p.Capacity,
            _ => false
        };
    }

    private static RateLimitStoreTransition Fixed(FixedWindowPolicy policy, RateLimitStoreContext context)
    {
        var now = context.ElapsedTicks;
        var period = policy.Window.Ticks;
        var start = now / period * period;
        var used = context.State is { } previous && previous.WindowStart == start ? previous.Used : 0;
        var delay = period - (now - start);
        var allowed = used < policy.Limit;
        if (allowed) used++;
        var next = new RateLimitStateSnapshot(policy, now, Recovery(now, delay), start, used,
            ImmutableQueue<long>.Empty, 0);
        return Recorded(next, context, allowed, policy.Limit - used, allowed ? 0 : delay, delay);
    }

    private static RateLimitStoreTransition Sliding(SlidingWindowPolicy policy, RateLimitStoreContext context)
    {
        var now = context.ElapsedTicks;
        var queue = context.State?.Events ?? ImmutableQueue<long>.Empty;
        var used = context.State?.Used ?? 0;
        var latest = context.State?.LatestEventTimestamp ?? now;
        var cutoff = now - policy.Window.Ticks;
        while (!queue.IsEmpty && queue.Peek() <= cutoff)
        {
            queue = queue.Dequeue();
            used--;
        }
        if (used < 0 || (used == 0) != queue.IsEmpty)
            return Fail(policy, context, RateLimitFailure.InvalidState);
        var allowed = used < policy.Limit;
        if (allowed)
        {
            queue = queue.Enqueue(now);
            latest = now;
            used++;
        }
        var retry = allowed ? 0 : policy.Window.Ticks - (now - queue.Peek());
        var fullRecovery = policy.Window.Ticks - (now - latest);
        var next = new RateLimitStateSnapshot(policy, now, Recovery(now, fullRecovery), 0, used, queue, 0)
        { LatestEventTimestamp = latest };
        return Recorded(next, context, allowed, policy.Limit - used, retry, fullRecovery);
    }

    private static RateLimitStoreTransition Bucket(TokenBucketPolicy policy, RateLimitStoreContext context)
    {
        var now = context.ElapsedTicks;
        var tokens = context.State?.Tokens ?? policy.Capacity;
        var rate = (decimal)policy.TokensPerPeriod;
        var period = (decimal)policy.ReplenishmentPeriod.Ticks;
        if (context.State is { } previous)
        {
            var elapsed = (decimal)(now - previous.LastTimestamp);
            var ticksToFull = (policy.Capacity - tokens) * period / rate;
            tokens = elapsed >= ticksToFull ? policy.Capacity :
                Math.Min(policy.Capacity, tokens + elapsed * rate / period);
        }
        var allowed = tokens >= 1;
        if (allowed) tokens -= 1;
        var retry = allowed ? 0 : decimal.Ceiling((1 - tokens) * period / rate);
        var fullRecovery = decimal.Ceiling((policy.Capacity - tokens) * period / rate);
        var next = new RateLimitStateSnapshot(policy, now, Recovery(now, fullRecovery), 0, 0,
            ImmutableQueue<long>.Empty, tokens);
        return Recorded(next, context, allowed, (int)decimal.Floor(tokens), retry, fullRecovery);
    }

    private static RateLimitStoreTransition Recorded(RateLimitStateSnapshot state, RateLimitStoreContext context,
        bool allowed, int remaining, decimal retryTicks, decimal recoveryTicks)
    {
        TimeSpan? retry = retryTicks > long.MaxValue ? null : TimeSpan.FromTicks((long)Math.Max(0, retryTicks));
        var utc = context.ObservedAt.ToUniversalTime();
        var availableDateTicks = DateTimeOffset.MaxValue.UtcTicks - utc.UtcTicks;
        DateTimeOffset? reset = recoveryTicks > availableDateTicks ? null : utc.AddTicks((long)Math.Max(0, recoveryTicks));
        return new(state, RateLimitResult.Recorded(allowed ? RateLimitDecision.Allowed : RateLimitDecision.Throttled,
            state.Policy.PolicyId, remaining, retry, reset, utc));
    }

    private static long? Recovery(long now, decimal delay) =>
        delay > long.MaxValue - now ? null : now + (long)delay;

    private static RateLimitStoreTransition Fail(RateLimitPolicy policy, RateLimitStoreContext context,
        RateLimitFailure failure) => new(context.State, RateLimitResult.Failed(policy.PolicyId, failure, context.ObservedAt));
}

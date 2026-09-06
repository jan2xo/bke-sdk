using System.Collections.Immutable;

namespace BKE.RateLimiting;

public static class RateLimitCapability
{
    public const string Id = "bke.rate-limiting";
    public const int ContractVersion = 1;
    public const int MaximumKeyLength = 4096;
    public const int MaximumPolicyIdLength = 256;
    public const int MaximumAllowance = 1_000_000;
    public static readonly TimeSpan MaximumPeriod = TimeSpan.FromDays(365);
}

public enum RateLimitDecision { Allowed, Throttled, Blocked }
public enum RateLimitFailureMode { FailClosed, FailOpen }
public enum RateLimitFailure
{
    StoreUnavailable,
    PartitionCapacityExhausted,
    StateCapacityExhausted,
    PolicyConflict,
    InvalidState,
    IndeterminateCommit,
    ContentionExhausted
}

public sealed record RateLimitRequest
{
    public RateLimitRequest(string key, RateLimitPolicy policy)
    {
        Key = RateLimitValidation.Key(key);
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }
    public string Key { get; }
    public RateLimitPolicy Policy { get; }
    public override string ToString() => $"RateLimitRequest {{ PolicyId = {Policy.PolicyId}, Key = [redacted] }}";
}

public sealed record RateLimitResult
{
    private RateLimitResult(RateLimitDecision decision, string policyId, int? remaining,
        TimeSpan? retryAfter, DateTimeOffset? resetAt, DateTimeOffset observedAt, RateLimitFailure? failure)
    {
        Decision = decision;
        PolicyId = policyId;
        Remaining = remaining;
        RetryAfter = retryAfter;
        ResetAt = resetAt;
        ObservedAt = observedAt.ToUniversalTime();
        Failure = failure;
    }

    public RateLimitDecision Decision { get; }
    public string PolicyId { get; }
    public int? Remaining { get; }
    public TimeSpan? RetryAfter { get; }
    public DateTimeOffset? ResetAt { get; }
    public DateTimeOffset ObservedAt { get; }
    public RateLimitFailure? Failure { get; }
    public bool UsageRecorded => Decision == RateLimitDecision.Allowed && Failure is null;

    public static RateLimitResult Recorded(RateLimitDecision decision, string policyId,
        int remaining, TimeSpan retryAfter, DateTimeOffset resetAt, DateTimeOffset observedAt)
    {
        RateLimitValidation.PolicyId(policyId);
        if (decision is not (RateLimitDecision.Allowed or RateLimitDecision.Throttled))
            throw new ArgumentOutOfRangeException(nameof(decision));
        if (remaining < 0 || remaining > RateLimitCapability.MaximumAllowance)
            throw new ArgumentOutOfRangeException(nameof(remaining));
        if (retryAfter < TimeSpan.Zero || resetAt < observedAt)
            throw new ArgumentException("Timing values must be nonnegative and ordered.");
        if (decision == RateLimitDecision.Allowed && retryAfter != TimeSpan.Zero)
            throw new ArgumentException("An allowed result has zero retry delay.");
        if (decision == RateLimitDecision.Throttled && (remaining != 0 || retryAfter <= TimeSpan.Zero))
            throw new ArgumentException("A throttled result requires zero allowance and a positive retry delay.");
        return new(decision, policyId, remaining, retryAfter, resetAt.ToUniversalTime(), observedAt, null);
    }

    public static RateLimitResult Failed(string policyId, RateLimitFailure failure,
        DateTimeOffset observedAt, bool failOpen = false)
    {
        RateLimitValidation.PolicyId(policyId);
        if (!Enum.IsDefined(failure)) throw new ArgumentOutOfRangeException(nameof(failure));
        if (failOpen && failure != RateLimitFailure.StoreUnavailable)
            throw new ArgumentException("Only a known pre-commit store outage may fail open.", nameof(failOpen));
        return new(failOpen ? RateLimitDecision.Allowed : RateLimitDecision.Blocked,
            policyId, null, null, null, observedAt, failure);
    }
}

public interface IRateLimiter
{
    Task<RateLimitResult> EvaluateAsync(RateLimitRequest request, CancellationToken cancellationToken = default);
}

public sealed record RateLimitPartition
{
    public RateLimitPartition(string policyId, string key)
    {
        PolicyId = RateLimitValidation.PolicyId(policyId);
        Key = RateLimitValidation.Key(key);
    }
    public string PolicyId { get; }
    public string Key { get; }
    public override string ToString() => $"RateLimitPartition {{ PolicyId = {PolicyId}, Key = [redacted] }}";
}

/// <summary>One engine attempt. Known contention retries retain OperationId; another EvaluateAsync call gets a new ID.</summary>
public sealed record RateLimitStoreRequest(RateLimitPartition Partition, Guid OperationId);

/// <summary>
/// Adapter snapshot in elapsed TimeSpan ticks of its store's shared time domain, never raw Stopwatch ticks.
/// Fixed uses Used/WindowStart; sliding uses Used/Events; bucket uses Tokens.
/// RecoveryTimestamp is null if a future recovery timestamp cannot be represented safely.
/// ImmutableQueue preserves staged-update isolation without copying every retained sliding event.
/// </summary>
public sealed record RateLimitStateSnapshot(
    RateLimitPolicy Policy,
    long LastTimestamp,
    long? RecoveryTimestamp,
    long WindowStart,
    int Used,
    ImmutableQueue<long> Events,
    decimal Tokens)
{
    public long LatestEventTimestamp { get; init; }
}

public sealed record RateLimitStoreContext(
    RateLimitStateSnapshot? State,
    DateTimeOffset ObservedAt,
    long ElapsedTicks);

public sealed record RateLimitStoreTransition(RateLimitStateSnapshot? State, RateLimitResult Result);

public sealed record RateLimitStoreResult
{
    private RateLimitStoreResult(RateLimitResult? result, RateLimitFailure? failure, DateTimeOffset observedAt)
    { Result = result; Failure = failure; ObservedAt = observedAt.ToUniversalTime(); }
    public RateLimitResult? Result { get; }
    public RateLimitFailure? Failure { get; }
    public DateTimeOffset ObservedAt { get; }
    public static RateLimitStoreResult Committed(RateLimitResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(result, null, result.ObservedAt);
    }
    public static RateLimitStoreResult Failed(RateLimitFailure failure, DateTimeOffset observedAt)
    {
        if (!Enum.IsDefined(failure)) throw new ArgumentOutOfRangeException(nameof(failure));
        return new(null, failure, observedAt);
    }
}

/// <summary>
/// Execute a pure transition against an atomically protected snapshot/time and commit at most once.
/// A callback may be recomputed for known non-commits only. An unavailable result guarantees no commit.
/// IndeterminateCommit means a commit may have occurred; never retry blindly or report an ordinary outage.
/// Caller cancellation thrown by an adapter guarantees this operation did not commit.
/// The in-memory implementation does not deduplicate independent invocations of this adapter port.
/// </summary>
public interface IRateLimitStore
{
    Task<RateLimitStoreResult> ExecuteAsync(RateLimitStoreRequest request,
        Func<RateLimitStoreContext, RateLimitStoreTransition> transition,
        CancellationToken cancellationToken = default);
}

internal static class RateLimitValidation
{
    internal static string Key(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > RateLimitCapability.MaximumKeyLength)
            throw new ArgumentException("A nonempty key within the documented length bound is required.", nameof(key));
        return key;
    }
    internal static string PolicyId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > RateLimitCapability.MaximumPolicyIdLength)
            throw new ArgumentException("A policy identifier within the documented length bound is required.", nameof(id));
        return id;
    }
}

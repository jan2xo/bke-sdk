namespace BKE.RateLimiting;

public abstract record RateLimitPolicy
{
    private protected RateLimitPolicy(string policyId, RateLimitFailureMode failureMode)
    {
        PolicyId = RateLimitValidation.PolicyId(policyId);
        if (!Enum.IsDefined(failureMode)) throw new ArgumentOutOfRangeException(nameof(failureMode));
        FailureMode = failureMode;
    }
    public string PolicyId { get; }
    public RateLimitFailureMode FailureMode { get; }
    public int StateVersion => 1;
    internal static void Allowance(int value, string name)
    {
        if (value <= 0 || value > RateLimitCapability.MaximumAllowance) throw new ArgumentOutOfRangeException(name);
    }
    internal static void Period(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value > RateLimitCapability.MaximumPeriod) throw new ArgumentOutOfRangeException(name);
    }
    public static FixedWindowPolicy FixedWindow(string policyId, int limit, TimeSpan window,
        RateLimitFailureMode failureMode = RateLimitFailureMode.FailClosed) => new(policyId, limit, window, failureMode);
    public static SlidingWindowPolicy SlidingWindow(string policyId, int limit, TimeSpan window,
        RateLimitFailureMode failureMode = RateLimitFailureMode.FailClosed) => new(policyId, limit, window, failureMode);
    public static TokenBucketPolicy TokenBucket(string policyId, int capacity, double tokensPerPeriod,
        TimeSpan replenishmentPeriod, RateLimitFailureMode failureMode = RateLimitFailureMode.FailClosed)
        => new(policyId, capacity, tokensPerPeriod, replenishmentPeriod, failureMode);
}

public sealed record FixedWindowPolicy : RateLimitPolicy
{
    public FixedWindowPolicy(string policyId, int limit, TimeSpan window,
        RateLimitFailureMode failureMode = RateLimitFailureMode.FailClosed) : base(policyId, failureMode)
    { Allowance(limit, nameof(limit)); Period(window, nameof(window)); Limit = limit; Window = window; }
    public int Limit { get; }
    public TimeSpan Window { get; }
}

public sealed record SlidingWindowPolicy : RateLimitPolicy
{
    public SlidingWindowPolicy(string policyId, int limit, TimeSpan window,
        RateLimitFailureMode failureMode = RateLimitFailureMode.FailClosed) : base(policyId, failureMode)
    { Allowance(limit, nameof(limit)); Period(window, nameof(window)); Limit = limit; Window = window; }
    public int Limit { get; }
    public TimeSpan Window { get; }
}

public sealed record TokenBucketPolicy : RateLimitPolicy
{
    public TokenBucketPolicy(string policyId, int capacity, double tokensPerPeriod, TimeSpan replenishmentPeriod,
        RateLimitFailureMode failureMode = RateLimitFailureMode.FailClosed) : base(policyId, failureMode)
    {
        Allowance(capacity, nameof(capacity)); Period(replenishmentPeriod, nameof(replenishmentPeriod));
        if (!double.IsFinite(tokensPerPeriod) || tokensPerPeriod < 0.000001 || tokensPerPeriod > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(tokensPerPeriod));
        Capacity = capacity; TokensPerPeriod = tokensPerPeriod; ReplenishmentPeriod = replenishmentPeriod;
    }
    public int Capacity { get; }
    public double TokensPerPeriod { get; }
    public TimeSpan ReplenishmentPeriod { get; }
}

namespace BKE.RateLimiting;

public sealed class BkeRateLimiter : IRateLimiter
{
    private readonly IRateLimitStore _store;
    private readonly TimeProvider _failureTime;

    /// <param name="store">Shared state owner. Callers retain ownership of its lifetime.</param>
    /// <param name="timeProvider">Clock for unexpected adapter-failure metadata only; the store owns quota time.</param>
    public BkeRateLimiter(IRateLimitStore store, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _failureTime = timeProvider ?? TimeProvider.System;
    }

    public async Task<RateLimitResult> EvaluateAsync(RateLimitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var operation = new RateLimitStoreRequest(new(request.Policy.PolicyId, request.Key), Guid.NewGuid());
            var outcome = await _store.ExecuteAsync(operation,
                context => RateLimitEvaluator.Evaluate(request.Policy, context), cancellationToken).ConfigureAwait(false);
            if (outcome is null) return Unexpected(request.Policy.PolicyId);
            if (outcome.Result is { } result)
            {
                if (result.PolicyId != request.Policy.PolicyId)
                    return RateLimitResult.Failed(request.Policy.PolicyId, RateLimitFailure.InvalidState, outcome.ObservedAt);
                return result;
            }
            var failure = outcome.Failure ?? RateLimitFailure.InvalidState;
            return RateLimitResult.Failed(request.Policy.PolicyId, failure, outcome.ObservedAt,
                failure == RateLimitFailure.StoreUnavailable && request.Policy.FailureMode == RateLimitFailureMode.FailOpen);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Conforming adapters only throw caller cancellation when no mutation committed.
            throw;
        }
        catch (Exception)
        {
            // An exception can arrive after a remote write. It is not evidence of a known non-commit.
            return Unexpected(request.Policy.PolicyId);
        }
    }

    private RateLimitResult Unexpected(string policyId) =>
        RateLimitResult.Failed(policyId, RateLimitFailure.IndeterminateCommit, _failureTime.GetUtcNow());
}

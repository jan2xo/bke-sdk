namespace BKE.RateLimiting;

public sealed record InMemoryRateLimitStoreStatistics(int PartitionCount, int RetainedEventCount);

/// <summary>A bounded process-local store. Share an instance to share allowance; restart discards all state.</summary>
public sealed class InMemoryRateLimitStore : IRateLimitStore, IDisposable
{
    private readonly TimeProvider _time;
    private readonly long _originTimestamp;
    private readonly int _maxPartitions;
    private readonly int _maxRetainedEvents;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<RateLimitPartition, RateLimitStateSnapshot> _states = new();
    private long _lastElapsed;
    private DateTimeOffset _lastObserved;
    private int _retainedEvents;
    private bool _disposed;

    public InMemoryRateLimitStore(TimeProvider? timeProvider = null, int maxPartitions = 10_000,
        int maxRetainedEvents = 100_000)
    {
        if (maxPartitions <= 0) throw new ArgumentOutOfRangeException(nameof(maxPartitions));
        if (maxRetainedEvents <= 0) throw new ArgumentOutOfRangeException(nameof(maxRetainedEvents));
        _time = timeProvider ?? TimeProvider.System;
        _originTimestamp = _time.GetTimestamp();
        _lastObserved = _time.GetUtcNow().ToUniversalTime();
        _maxPartitions = maxPartitions;
        _maxRetainedEvents = maxRetainedEvents;
    }

    public async Task<RateLimitStoreResult> ExecuteAsync(RateLimitStoreRequest request,
        Func<RateLimitStoreContext, RateLimitStoreTransition> transition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Partition);
        ArgumentNullException.ThrowIfNull(transition);
        if (request.OperationId == Guid.Empty) throw new ArgumentException("An operation identifier is required.", nameof(request));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) return Failure(RateLimitFailure.StoreUnavailable);
            if (!TryReadTime(out var elapsed, out var observed)) return Failure(RateLimitFailure.InvalidState);
            var partition = request.Partition;
            _states.TryGetValue(partition, out var state);
            if (state?.RecoveryTimestamp is { } recovery && recovery <= elapsed)
            {
                Remove(partition, state);
                state = null;
            }
            if (state is null && _states.Count >= _maxPartitions)
            {
                Prune(elapsed);
                if (_states.Count >= _maxPartitions) return Failure(RateLimitFailure.PartitionCapacityExhausted);
            }

            RateLimitStoreTransition proposed;
            try
            {
                proposed = transition(new(state, observed, elapsed));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { return Failure(RateLimitFailure.InvalidState); }
            cancellationToken.ThrowIfCancellationRequested();
            if (proposed?.Result is not { } result || result.PolicyId != partition.PolicyId || result.ObservedAt != observed)
                return Failure(RateLimitFailure.InvalidState);

            // A failed transition has no authority to replace quota state, including fail-open results.
            if (result.Failure is not null) return RateLimitStoreResult.Committed(result);
            if (proposed.State is not { } replacement || replacement.Policy.PolicyId != partition.PolicyId ||
                replacement.LastTimestamp != elapsed || replacement.Used < 0 ||
                replacement.Used > RateLimitCapability.MaximumAllowance || replacement.Events is null)
                return Failure(RateLimitFailure.InvalidState);

            var oldEvents = EventCount(state);
            var newEvents = EventCount(replacement);
            var nextEvents = (long)_retainedEvents - oldEvents + newEvents;
            if (nextEvents > _maxRetainedEvents)
            {
                Prune(elapsed);
                nextEvents = (long)_retainedEvents - oldEvents + newEvents;
                if (nextEvents > _maxRetainedEvents) return Failure(RateLimitFailure.StateCapacityExhausted);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _states[partition] = replacement;
            _retainedEvents = (int)nextEvents;
            // After commit return the committed result even if cancellation arrives now.
            return RateLimitStoreResult.Committed(result);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> PruneExpiredAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadTime(out var elapsed, out _)) return 0;
            return Prune(elapsed);
        }
        finally { _gate.Release(); }
    }

    public async Task<InMemoryRateLimitStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new(_states.Count, _retainedEvents);
        }
        finally { _gate.Release(); }
    }

    private bool TryReadTime(out long elapsed, out DateTimeOffset observed)
    {
        elapsed = _lastElapsed;
        observed = _lastObserved;
        try
        {
            var sampled = _time.GetElapsedTime(_originTimestamp, _time.GetTimestamp()).Ticks;
            if (sampled < _lastElapsed) return false;
            observed = _time.GetUtcNow().ToUniversalTime();
            elapsed = sampled;
            _lastElapsed = elapsed;
            _lastObserved = observed;
            return true;
        }
        catch (Exception) { return false; }
    }

    private RateLimitStoreResult Failure(RateLimitFailure failure) => RateLimitStoreResult.Failed(failure, _lastObserved);
    private static int EventCount(RateLimitStateSnapshot? state) => state?.Policy is SlidingWindowPolicy ? state.Used : 0;
    private void Remove(RateLimitPartition partition, RateLimitStateSnapshot state)
    {
        _states.Remove(partition);
        _retainedEvents -= EventCount(state);
    }
    private int Prune(long elapsed)
    {
        var expired = new List<RateLimitPartition>();
        foreach (var pair in _states)
            if (pair.Value.RecoveryTimestamp is { } recovery && recovery <= elapsed) expired.Add(pair.Key);
        foreach (var key in expired) Remove(key, _states[key]);
        return expired.Count;
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _disposed = true;
            _states.Clear();
            _retainedEvents = 0;
        }
        finally { _gate.Release(); }
        // The managed gate stays valid so concurrent/future calls can observe the disposed flag safely.
    }
}

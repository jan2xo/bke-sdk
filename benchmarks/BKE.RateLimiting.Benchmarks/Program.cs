using System.Diagnostics;
using System.Text.Json;
using BKE.RateLimiting;

// Temporary certification-style harness. It intentionally uses no benchmark package
// so the Actions runner can compile it with only the SDK project reference.
var options = BenchmarkOptions.Parse(args);
Directory.CreateDirectory(options.OutputDirectory);
var rows = new List<Measurement>();

foreach (var algorithm in new[] { "fixed", "sliding", "token" })
foreach (var keyMode in new[] { "single", "many" })
foreach (var concurrent in new[] { false, true })
foreach (var repetition in new[] { 1, 2, 3 })
{
    var clock = new FrozenTimeProvider();
    using var store = new InMemoryRateLimitStore(clock, options.Partitions);
    var limiter = new BkeRateLimiter(store);
    RateLimitPolicy policy = algorithm switch
    {
        "fixed" => RateLimitPolicy.FixedWindow($"bench-{algorithm}", options.Limit, TimeSpan.FromMinutes(1)),
        "sliding" => RateLimitPolicy.SlidingWindow($"bench-{algorithm}", options.Limit, TimeSpan.FromMinutes(1)),
        _ => RateLimitPolicy.TokenBucket($"bench-{algorithm}", options.Limit, options.Limit, TimeSpan.FromSeconds(1))
    };
    var samples = new long[options.Operations];
    long admitted = 0, throttled = 0;
    var keys = Enumerable.Range(0, options.Operations).Select(i => keyMode == "single" ? "single" : $"key-{i % options.Partitions}").ToArray();
    var requests = keys.Select(key => new RateLimitRequest(key, policy)).ToArray();
    using (var warmStore = new InMemoryRateLimitStore(clock, options.Partitions))
    {
        var warmLimiter = new BkeRateLimiter(warmStore);
        for (var i = 0; i < 128; i++) await warmLimiter.EvaluateAsync(requests[0]);
    }
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
    var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
    var stopwatch = Stopwatch.StartNew();
    if (concurrent)
    {
        await Parallel.ForEachAsync(Enumerable.Range(0, options.Operations), new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism }, async (i, ct) =>
        {
            var sw = Stopwatch.GetTimestamp();
            var result = await limiter.EvaluateAsync(requests[i], ct);
            if (result.Decision == RateLimitDecision.Allowed) Interlocked.Increment(ref admitted); else if (result.Decision == RateLimitDecision.Throttled) Interlocked.Increment(ref throttled);
            samples[i] = Stopwatch.GetTimestamp() - sw;
        });
    }
    else
    {
        for (var i = 0; i < options.Operations; i++)
        {
            var sw = Stopwatch.GetTimestamp();
            var result = await limiter.EvaluateAsync(requests[i]);
            if (result.Decision == RateLimitDecision.Allowed) admitted++; else if (result.Decision == RateLimitDecision.Throttled) throttled++;
            samples[i] = Stopwatch.GetTimestamp() - sw;
        }
    }
    stopwatch.Stop();
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore;
    var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
    GC.KeepAlive(store);
    var ordered = samples.OrderBy(x => x).ToArray();
    if (admitted + throttled != options.Operations) throw new InvalidOperationException("Unexpected blocked benchmark operation.");
    rows.Add(new Measurement(algorithm, keyMode, concurrent, repetition, options.Operations / stopwatch.Elapsed.TotalSeconds,
        Percentile(ordered, .50), Percentile(ordered, .95), Percentile(ordered, .99),
        allocated / (double)options.Operations, admitted, throttled,
        memoryAfter - memoryBefore, Stopwatch.Frequency));
}

var jsonPath = Path.Combine(options.OutputDirectory, "rate-limiting-benchmark.json");
var lifecycleClock = new FrozenTimeProvider();
var lifecycleStore = new InMemoryRateLimitStore(lifecycleClock, options.Partitions);
var lifecycleLimiter = new BkeRateLimiter(lifecycleStore);
var lifecyclePolicy = RateLimitPolicy.FixedWindow("lifecycle", 1, TimeSpan.FromHours(1));
var lifecycleMemoryBefore = GC.GetTotalMemory(forceFullCollection: true);
var capacityBlocked = 0;
for (var i = 0; i < options.Partitions + 1; i++)
{
    var result = await lifecycleLimiter.EvaluateAsync(new RateLimitRequest($"lifecycle-{i}", lifecyclePolicy));
    if (result.Decision == RateLimitDecision.Blocked) capacityBlocked++;
}
var lifecycleMemoryAfter = GC.GetTotalMemory(forceFullCollection: true);
var beforePrune = await lifecycleStore.GetStatisticsAsync();
lifecycleClock.Advance(TimeSpan.FromDays(366));
await lifecycleStore.PruneExpiredAsync();
var afterPrune = await lifecycleStore.GetStatisticsAsync();
var refill = await lifecycleLimiter.EvaluateAsync(new RateLimitRequest("lifecycle-refill", lifecyclePolicy));
if (capacityBlocked != 1 || beforePrune.PartitionCount != options.Partitions || afterPrune.PartitionCount != 0 ||
    refill.Decision != RateLimitDecision.Allowed) throw new InvalidOperationException("Partition lifecycle certification failed.");
var lifecyclePath = Path.Combine(options.OutputDirectory, "rate-limiting-lifecycle.json");
await File.WriteAllTextAsync(lifecyclePath, JsonSerializer.Serialize(new { configuredPartitions = options.Partitions, attemptedPartitions = options.Partitions + 1, capacityBlocked, retainedMemoryDeltaBytes = lifecycleMemoryAfter - lifecycleMemoryBefore, beforePrune, afterPrune, refill.Decision, refill.Failure }, new JsonSerializerOptions { WriteIndented = true }));
await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
var csvPath = Path.Combine(options.OutputDirectory, "rate-limiting-benchmark.csv");
await File.WriteAllLinesAsync(csvPath, new[] { "algorithm,keyMode,concurrent,repetition,opsPerSecond,p50Ticks,p95Ticks,p99Ticks,allocatedBytesPerOp,admitted,throttled,memoryDeltaBytes,stopwatchFrequency" }
    .Concat(rows.Select(x => $"{x.Algorithm},{x.KeyMode},{x.Concurrent},{x.Repetition},{x.OpsPerSecond:F2},{x.P50Ticks},{x.P95Ticks},{x.P99Ticks},{x.AllocatedBytesPerOperation:F2},{x.Admitted},{x.Throttled},{x.MemoryDeltaBytes},{x.StopwatchFrequency}")));
await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "rate-limiting-benchmark.md"),
    "# BKE.RateLimiting benchmark\n\nRun from the repository root after the SDK project is available: `dotnet run --project benchmarks/BKE.RateLimiting.Benchmarks -c Release -- --operations=10000 --partitions=1000 --output=artifacts/benchmarks`. Measurements are runner evidence. Tick latencies use Stopwatch.Frequency=" + Stopwatch.Frequency + ". Replace this file only with a recorded Actions run; no results are claimed by source control.\n\nThe harness covers fixed/sliding/token algorithms, single/many keys, sequential/concurrent calls, admitted/throttled counts, throughput, p50/p95/p99 sampled latency, process-wide allocation sampling, retained memory delta, partition overflow, and prune statistics. Keys and requests are precomputed; warmup runs use a separate store. Allocation and retained-memory values describe the whole measured process interval and require consistent runner conditions.\n\n" + string.Join("\n", rows.Select(x => $"- {x.Algorithm}/{x.KeyMode}/{(x.Concurrent ? "concurrent" : "sequential")}: {x.OpsPerSecond:F2} ops/s")));

var environment = new { sourceSha = Environment.GetEnvironmentVariable("GITHUB_SHA"), framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, processors = Environment.ProcessorCount, options.Parallelism, options.Operations, options.Partitions, repetitions = 3 };
await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "environment.json"), JsonSerializer.Serialize(environment, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("BENCHMARK_ENVIRONMENT=" + JsonSerializer.Serialize(environment));
Console.WriteLine("BENCHMARK_RESULTS=" + JsonSerializer.Serialize(rows));
Console.WriteLine("BENCHMARK_LIFECYCLE=" + await File.ReadAllTextAsync(lifecyclePath));

static long Percentile(long[] values, double p) => values[Math.Clamp((int)Math.Ceiling(values.Length * p) - 1, 0, values.Length - 1)];
record Measurement(string Algorithm, string KeyMode, bool Concurrent, int Repetition, double OpsPerSecond, long P50Ticks, long P95Ticks, long P99Ticks, double AllocatedBytesPerOperation, long Admitted, long Throttled, long MemoryDeltaBytes, long StopwatchFrequency);

sealed record BenchmarkOptions(string OutputDirectory, int Operations, int Partitions, int Limit, int Parallelism)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        string Value(string name, string fallback) => args.FirstOrDefault(x => x.StartsWith(name + "=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? fallback;
        return new(Value("--output", "benchmark-output"), int.Parse(Value("--operations", "50000")), int.Parse(Value("--partitions", "1000")), int.Parse(Value("--limit", "1000000")), int.Parse(Value("--parallelism", Math.Max(4, Environment.ProcessorCount).ToString())));
    }
}

sealed class FrozenTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
    private long _timestamp;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public override long GetTimestamp() => _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan duration) { _utcNow = _utcNow.Add(duration); _timestamp += duration.Ticks; }
}

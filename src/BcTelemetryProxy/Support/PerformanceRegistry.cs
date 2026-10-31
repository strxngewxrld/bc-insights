using System.Globalization;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Model;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.Support;

public sealed record PerformanceKey(string Service, string Instance, string? Environment, string? Version,
    string? EventId, string Operation, string? Extension, string? Publisher, string? ExtensionVersion,
    string? ObjectType, string? ObjectId, string? Tenant, string? Company);

public sealed record PerformanceSample(DateTime TimeUtc, double DurationMs, bool Success,
    string TraceId, IReadOnlyDictionary<string, string> Details);

public sealed record PerformanceSummary(PerformanceKey Key, int Count, int Failed, double TotalMs,
    double AverageMs, double P95Ms, double MaxMs, PerformanceSample Slowest, string NextStep);

public sealed record PerformanceOverview(DateTime SinceUtc, DateTime UntilUtc, int ObservedSpans,
    long EvictedSamples, string Scope, IReadOnlyList<PerformanceSummary> Operations);

/// <summary>
/// Bounded, compact samples of observed spans, not a profiler or end-user response-time measurement.
/// Only already-filtered attributes are retained; no SQL text, messages or stack traces are copied.
/// </summary>
public sealed class PerformanceRegistry(IOptions<SupportOptions> options)
{
    private readonly object _lock = new();
    private readonly Queue<(PerformanceKey Key, PerformanceSample Sample)> _samples = new();
    private long _evicted;
    private static readonly string[] DetailKeys =
    ["bc.session.id", "bc.operation.id", "sqlServerSessionId", "sqlExecutes", "sqlRowsRead",
     "sqlStatementsExecuted", "executionTimeInMs", "serverExecutionTimeInMs", "totalTimeInMs",
     "http.response.status_code", "numberOfRows", "alObjectName"];

    public void Record(SpanItem span)
    {
        if (!options.Value.Enabled || span.Duration <= TimeSpan.Zero)
            return;

        string? Get(string key)
        {
            var value = Convert.ToString(span.Attributes.FirstOrDefault(a => a.Key == key).Value, CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(value) ? null : value[..Math.Min(value.Length, 256)];
        }

        var key = new PerformanceKey(span.Source.ServiceName, span.Source.ServiceInstanceId,
            span.Source.DeploymentEnvironment, span.Source.ServiceVersion, Get("bc.event.id"), span.Name[..Math.Min(span.Name.Length, 120)],
            Get("extensionName"), Get("extensionPublisher"), Get("extensionVersion"),
            Get("alObjectType"), Get("alObjectId"), Get("bc.tenant.id"), Get("bc.company.name"));
        var details = new Dictionary<string, string>();
        foreach (var name in DetailKeys)
            if (Get(name) is { } value) details[name] = value;
        var sample = new PerformanceSample(span.StartUtc + span.Duration, span.Duration.TotalMilliseconds,
            span.Success, span.TraceId.ToHexString(), details);
        lock (_lock)
        {
            _samples.Enqueue((key, sample));
            while (_samples.Count > Math.Clamp(options.Value.MaxPerformanceSamples, 1, 200000))
            {
                _samples.Dequeue();
                _evicted++;
            }
        }
    }

    public PerformanceOverview Overview(double hours = 24, string? instance = null, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var since = now.AddHours(-Math.Clamp(hours, 0.1, 24));
        (PerformanceKey Key, PerformanceSample Sample)[] samples;
        long evicted;
        lock (_lock)
        {
            samples = _samples.Where(s => s.Sample.TimeUtc >= since && s.Sample.TimeUtc <= now &&
                (instance is null || string.Equals(s.Key.Instance, instance, StringComparison.OrdinalIgnoreCase))).ToArray();
            evicted = _evicted;
        }
        return new(since, now, samples.Length, evicted,
            "Observed positive-duration spans only; BC thresholded/sampled telemetry, overlapping spans may double-count time. Memory resets on restart. Evictions can shorten coverage. P95 uses nearest rank.",
            Summarize(samples));
    }

    internal static IReadOnlyList<PerformanceSummary> Summarize(IEnumerable<(PerformanceKey Key, PerformanceSample Sample)> samples)
        => samples.GroupBy(s => s.Key).Select(g =>
        {
            var sorted = g.Select(s => s.Sample).OrderBy(s => s.DurationMs).ToArray();
            var total = sorted.Sum(s => s.DurationMs);
            return new PerformanceSummary(g.Key, sorted.Length, sorted.Count(s => !s.Success), total,
                total / sorted.Length, sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1].DurationMs,
                sorted[^1].DurationMs, sorted[^1], NextStep(g.Key));
        }).OrderByDescending(s => s.TotalMs).Take(100).ToArray();

    private static string NextStep(PerformanceKey key) => key.EventId switch
    {
        "RT0005" => "SQL: correlate trace and sqlServerSessionId with blocking, waits and I/O; inspect query plan, indexes and AL filters before changing SQL.",
        "RT0012" or "RT0028" => "Locks: correlate SQL blocking/deadlocks; inspect transaction duration, lock order and AL write loops.",
        "RT0019" => "Outgoing HTTP: compare endpoint latency and HTTP status; inspect timeouts, retries and external service health.",
        _ => "AL/operation: inspect the slowest trace, AL object and extension; compare sqlExecutes/sqlRowsRead, profile in a test environment and compare the same workload/build after changes.",
    };
}

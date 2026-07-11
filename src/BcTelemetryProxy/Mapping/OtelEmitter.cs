using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.Mapping;

/// <summary>Sink for the individual OpenTelemetry signals produced by the mapper.</summary>
public interface IOtelEmitter
{
    void EmitLog(
        LogLevel level,
        string? body,
        string? eventName,
        IReadOnlyList<KeyValuePair<string, object?>> attributes);

    void EmitSpan(
        string name,
        ActivityKind kind,
        DateTime startUtc,
        TimeSpan duration,
        ActivityTraceId traceId,
        ActivitySpanId? parentSpanId,
        bool success,
        string? statusDescription,
        IReadOnlyList<KeyValuePair<string, object?>> tags);

    void EmitMetric(AiMetricPoint point, MetricKind kind, IReadOnlyList<KeyValuePair<string, object?>> tags);
}

/// <summary>
/// Emits translated Business Central telemetry through the OpenTelemetry SDK:
/// logs via an <see cref="ILogger"/> wired to the OpenTelemetry logging provider,
/// spans via an <see cref="ActivitySource"/> and metrics via a <see cref="Meter"/>.
/// The number of metric instruments is bounded to avoid unbounded memory growth
/// from runaway metric names; beyond the cap, points are forwarded as logs.
/// </summary>
public sealed class OtelEmitter : IOtelEmitter, IDisposable
{
    private readonly ActivitySource _activitySource = new(TelemetryConstants.ActivitySourceName);
    private readonly Meter _meter = new(TelemetryConstants.MeterName);
    private readonly ConcurrentDictionary<string, Instrument> _instruments = new();
    private readonly ILogger<OtelEmitter> _logger;
    private readonly int _maxInstruments;

    public OtelEmitter(ILogger<OtelEmitter> logger, IOptions<MetricsOptions> metricsOptions)
    {
        _logger = logger;
        _maxInstruments = metricsOptions.Value.MaxInstruments;
    }

    /// <summary>Number of distinct metric instruments currently created.</summary>
    public int InstrumentCount => _instruments.Count;

    public void EmitLog(
        LogLevel level,
        string? body,
        string? eventName,
        IReadOnlyList<KeyValuePair<string, object?>> attributes)
    {
        if (!_logger.IsEnabled(level))
        {
            return;
        }

        var state = new LogState(body, attributes);
        _logger.Log(level, new EventId(0, eventName), state, exception: null, LogState.Format);
    }

    public void EmitSpan(
        string name,
        ActivityKind kind,
        DateTime startUtc,
        TimeSpan duration,
        ActivityTraceId traceId,
        ActivitySpanId? parentSpanId,
        bool success,
        string? statusDescription,
        IReadOnlyList<KeyValuePair<string, object?>> tags)
    {
        // Preserve the BC operation id as the trace id. When there is no valid
        // parent span, use a zero (default) span id so the activity is a root
        // span within that trace rather than a child of an invented parent.
        var parentContext = new ActivityContext(
            traceId,
            parentSpanId ?? default,
            ActivityTraceFlags.Recorded);

        var activity = _activitySource.StartActivity(
            name,
            kind,
            parentContext,
            tags,
            links: null,
            startTime: startUtc);

        if (activity is null)
        {
            // No listener (e.g. tracing disabled) — nothing to emit.
            return;
        }

        activity.SetStatus(success ? ActivityStatusCode.Ok : ActivityStatusCode.Error, statusDescription);
        activity.SetEndTime(startUtc + duration);
        activity.Stop();
    }

    public void EmitMetric(AiMetricPoint point, MetricKind kind, IReadOnlyList<KeyValuePair<string, object?>> tags)
    {
        var enriched = WithMetricStats(point, kind, tags);

        var instrument = GetInstrument(point.Name, kind);
        switch (instrument)
        {
            case Counter<double> counter:
                counter.Add(point.Value, enriched);
                break;
            case Histogram<double> histogram:
                histogram.Record(point.Value, enriched);
                break;
            default:
                // Instrument cap reached — forward loss-light as a log record.
                EmitLog(LogLevel.Information, $"BC metric {point.Name}", "metric",
                    WithMetricStats(point, kind, tags, includeName: true));
                break;
        }
    }

    private Instrument? GetInstrument(string name, MetricKind kind)
    {
        if (_instruments.TryGetValue(name, out var existing))
        {
            return existing;
        }

        if (_instruments.Count >= _maxInstruments)
        {
            _logger.LogDebug("Metric instrument cap {Cap} reached; forwarding {Metric} as a log", _maxInstruments, name);
            return null;
        }

        return _instruments.GetOrAdd(name, n => kind == MetricKind.Counter
            ? _meter.CreateCounter<double>(n)
            : _meter.CreateHistogram<double>(n));
    }

    private static KeyValuePair<string, object?>[] WithMetricStats(
        AiMetricPoint point,
        MetricKind kind,
        IReadOnlyList<KeyValuePair<string, object?>> tags,
        bool includeName = false)
    {
        var list = new List<KeyValuePair<string, object?>>(tags)
        {
            new("bc.metric.kind", kind.ToString().ToLowerInvariant()),
        };

        if (includeName)
        {
            list.Add(new("bc.metric.name", point.Name));
            list.Add(new("bc.metric.value", point.Value));
        }

        if (point.Count is { } count)
        {
            list.Add(new("bc.metric.count", count));
        }

        if (point.Min is { } min)
        {
            list.Add(new("bc.metric.min", min));
        }

        if (point.Max is { } max)
        {
            list.Add(new("bc.metric.max", max));
        }

        if (point.StandardDeviation is { } stddev)
        {
            list.Add(new("bc.metric.stddev", stddev));
        }

        return list.ToArray();
    }

    public void Dispose()
    {
        _activitySource.Dispose();
        _meter.Dispose();
    }

    /// <summary>
    /// Log state that carries the translated custom dimensions as structured
    /// key/value pairs so the OpenTelemetry logging provider surfaces them as
    /// log attributes, while the formatter yields the BC message as the body.
    /// </summary>
    private sealed class LogState(string? body, IReadOnlyList<KeyValuePair<string, object?>> attributes)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public static readonly Func<LogState, Exception?, string> Format = (state, _) => state._body ?? string.Empty;

        private readonly string? _body = body;

        public int Count => attributes.Count;

        public KeyValuePair<string, object?> this[int index] => attributes[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => attributes.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

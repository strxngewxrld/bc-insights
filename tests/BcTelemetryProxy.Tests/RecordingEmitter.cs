using System.Diagnostics;
using BcTelemetryProxy.Mapping;
using Microsoft.Extensions.Logging;

namespace BcTelemetryProxy.Tests;

/// <summary>Test double that captures everything the mapper emits.</summary>
public sealed class RecordingEmitter : IOtelEmitter
{
    public List<LogEntry> Logs { get; } = new();
    public List<SpanEntry> Spans { get; } = new();
    public List<MetricEntry> Metrics { get; } = new();

    public void EmitLog(
        LogLevel level,
        string? body,
        string? eventName,
        IReadOnlyList<KeyValuePair<string, object?>> attributes)
        => Logs.Add(new LogEntry(level, body, eventName, ToDict(attributes)));

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
        => Spans.Add(new SpanEntry(name, kind, startUtc, duration, traceId, parentSpanId, success, statusDescription, ToDict(tags)));

    public void EmitMetric(AiMetricPoint point, MetricKind kind, IReadOnlyList<KeyValuePair<string, object?>> tags)
        => Metrics.Add(new MetricEntry(point, kind, ToDict(tags)));

    private static Dictionary<string, object?> ToDict(IReadOnlyList<KeyValuePair<string, object?>> pairs)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var pair in pairs)
        {
            dict[pair.Key] = pair.Value;
        }

        return dict;
    }

    public sealed record LogEntry(LogLevel Level, string? Body, string? EventName, Dictionary<string, object?> Attributes);

    public sealed record SpanEntry(
        string Name,
        ActivityKind Kind,
        DateTime StartUtc,
        TimeSpan Duration,
        ActivityTraceId TraceId,
        ActivitySpanId? ParentSpanId,
        bool Success,
        string? StatusDescription,
        Dictionary<string, object?> Tags);

    public sealed record MetricEntry(AiMetricPoint Point, MetricKind Kind, Dictionary<string, object?> Tags);
}

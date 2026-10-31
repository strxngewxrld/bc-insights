using System.Diagnostics;
using BcTelemetryProxy.Mapping;
using BcTelemetryProxy.Model;
using Microsoft.Extensions.Logging;

namespace BcTelemetryProxy.Tests;

/// <summary>Test double that captures everything the mapper emits.</summary>
public sealed class RecordingEmitter : IOtelEmitter
{
    public List<LogEntry> Logs { get; } = new();
    public List<SpanEntry> Spans { get; } = new();
    public List<MetricEntry> Metrics { get; } = new();

    public void EmitLog(LogItem log)
        => Logs.Add(new LogEntry(log.Level, log.Body, log.EventName, ToDict(log.Attributes), log));

    public void EmitSpan(SpanItem span)
        => Spans.Add(new SpanEntry(span.Name, span.Kind, span.StartUtc, span.Duration, span.TraceId, span.ParentSpanId,
            span.Success, span.StatusDescription, ToDict(span.Attributes), span));

    public void EmitMetric(MetricItem metric)
        => Metrics.Add(new MetricEntry(metric.Point, metric.Kind, ToDict(metric.Attributes), metric));

    private static Dictionary<string, object?> ToDict(IReadOnlyList<KeyValuePair<string, object?>> pairs)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var pair in pairs)
        {
            dict[pair.Key] = pair.Value;
        }

        return dict;
    }

    public sealed record LogEntry(LogLevel Level, string? Body, string? EventName, Dictionary<string, object?> Attributes, LogItem Item);

    public sealed record SpanEntry(
        string Name,
        ActivityKind Kind,
        DateTime StartUtc,
        TimeSpan Duration,
        ActivityTraceId TraceId,
        ActivitySpanId? ParentSpanId,
        bool Success,
        string? StatusDescription,
        Dictionary<string, object?> Tags,
        SpanItem Item);

    public sealed record MetricEntry(AiMetricPoint Point, MetricKind Kind, Dictionary<string, object?> Tags, MetricItem Item);
}

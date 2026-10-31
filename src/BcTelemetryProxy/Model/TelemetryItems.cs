using System.Diagnostics;
using BcTelemetryProxy.Mapping;
using Microsoft.Extensions.Logging;

namespace BcTelemetryProxy.Model;

/// <summary>
/// Identifies where a telemetry item came from. Becomes the OTLP resource, so
/// several BC server instances on several hosts behind one proxy stay distinct
/// (<c>service.instance.id</c>, <c>host.name</c>, <c>bc.server_instance</c>).
/// Record equality makes it the grouping key for export batches.
/// </summary>
public sealed record TelemetrySource(
    string ServiceName,
    string ServiceNamespace,
    string? HostName,
    string? ServerInstance,
    string? DeploymentEnvironment)
{
    private static readonly IReadOnlyDictionary<string, string> NoAttributes = new Dictionary<string, string>();

    /// <summary>
    /// Additional configured resource attributes (e.g. <c>gitlab.project.id</c>).
    /// Shared instances per configuration, so record equality still groups sources.
    /// </summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = NoAttributes;

    /// <summary>BC server version (<c>service.version</c>), from the telemetry's <c>componentVersion</c>.</summary>
    public string? ServiceVersion { get; init; }

    /// <summary>Fallback used by tests and when nothing is configured.</summary>
    public static TelemetrySource Default { get; } = new("business-central", "dynamics365-bc", null, null, null);

    /// <summary><c>host/instance</c>, or whichever part is known.</summary>
    public string ServiceInstanceId => (HostName, ServerInstance) switch
    {
        ({ } host, { } instance) => $"{host}/{instance}",
        ({ } host, null) => host,
        (null, { } instance) => instance,
        _ => "unknown",
    };
}

/// <summary>A translated log record, carrying the original BC timestamp.</summary>
public sealed record LogItem(
    TelemetrySource Source,
    DateTime TimeUtc,
    LogLevel Level,
    string? Body,
    string? EventName,
    ActivityTraceId? TraceId,
    ActivitySpanId? SpanId,
    IReadOnlyList<KeyValuePair<string, object?>> Attributes);

/// <summary>A translated span (AI request or dependency).</summary>
public sealed record SpanItem(
    TelemetrySource Source,
    string Name,
    ActivityKind Kind,
    DateTime StartUtc,
    TimeSpan Duration,
    ActivityTraceId TraceId,
    ActivitySpanId SpanId,
    ActivitySpanId? ParentSpanId,
    bool Success,
    string? StatusDescription,
    IReadOnlyList<KeyValuePair<string, object?>> Attributes,
    IReadOnlyList<SpanEvent>? Events = null);

/// <summary>A span event, e.g. <c>exception</c> with <c>exception.type/message/stacktrace</c>.</summary>
public sealed record SpanEvent(string Name, DateTime TimeUtc, IReadOnlyList<KeyValuePair<string, object?>> Attributes);

/// <summary>
/// A metric data point. A <see cref="MetricKind.Counter"/> with
/// <see cref="StartUtc"/> is exported as a cumulative monotonic OTLP sum;
/// everything else as a gauge.
/// </summary>
public sealed record MetricItem(
    TelemetrySource Source,
    DateTime TimeUtc,
    AiMetricPoint Point,
    MetricKind Kind,
    IReadOnlyList<KeyValuePair<string, object?>> Attributes,
    string? Unit = null,
    DateTime? StartUtc = null,
    string? Description = null);

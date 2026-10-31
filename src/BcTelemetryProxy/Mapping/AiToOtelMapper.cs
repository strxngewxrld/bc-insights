using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BcTelemetryProxy.Model;
using Microsoft.Extensions.Logging;

namespace BcTelemetryProxy.Mapping;

/// <summary>Outcome of translating a single envelope.</summary>
public enum MappingStatus
{
    Mapped,
    Unsupported,
    Failed,
}

/// <summary>
/// Dispatches an AI envelope to the mapping for its telemetry type. Trace
/// context, attribute processing, JSON access and metric classification live in
/// dedicated helpers (<see cref="TraceContextMapper"/>, <see cref="AttributeMapper"/>,
/// <see cref="JsonElementExtensions"/>, <see cref="MetricMapper"/>).
///
///   MessageData          -> log record
///   EventData            -> log record
///   ExceptionData        -> log record (exception.* semantics)
///   RequestData          -> server span
///   RemoteDependencyData -> client span
///   MetricData           -> metric
///   (anything else)      -> explicit "unsupported" log, counted
/// </summary>
public sealed class AiToOtelMapper
{
    private readonly IOtelEmitter _emitter;
    private readonly AttributeMapper _attributes;
    private readonly ILogger<AiToOtelMapper> _logger;
    private long _unsupportedCount;

    public AiToOtelMapper(IOtelEmitter emitter, AttributeMapper attributes, ILogger<AiToOtelMapper> logger)
    {
        _emitter = emitter;
        _attributes = attributes;
        _logger = logger;
    }

    /// <summary>Total number of unsupported telemetry items seen since start.</summary>
    public long UnsupportedCount => Interlocked.Read(ref _unsupportedCount);

    public MappingStatus Map(Envelope envelope) => Map(envelope, TelemetrySource.Default);

    public MappingStatus Map(Envelope envelope, TelemetrySource source)
    {
        var baseType = envelope.Data?.BaseType;
        if (baseType is null || envelope.Data is null)
        {
            return MappingStatus.Failed;
        }

        var data = envelope.Data.BaseData;
        try
        {
            switch (baseType)
            {
                case "MessageData":
                    MapMessage(envelope, data, source);
                    return MappingStatus.Mapped;
                case "EventData":
                    MapEvent(envelope, data, source);
                    return MappingStatus.Mapped;
                case "ExceptionData":
                    MapException(envelope, data, source);
                    return MappingStatus.Mapped;
                case "RequestData":
                    MapRequest(envelope, data, source);
                    return MappingStatus.Mapped;
                case "RemoteDependencyData":
                    MapDependency(envelope, data, source);
                    return MappingStatus.Mapped;
                case "MetricData":
                    MapMetric(envelope, data, source);
                    return MappingStatus.Mapped;
                default:
                    MapUnsupported(envelope, data, baseType, source);
                    return MappingStatus.Unsupported;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to map envelope of type {BaseType}", baseType);
            return MappingStatus.Failed;
        }
    }

    // -- Logs -----------------------------------------------------------------

    private void MapMessage(Envelope env, JsonElement data, TelemetrySource source)
    {
        EmitLog(env, source,
            SeverityMap.FromAppInsights(data.GetIntOrNull("severityLevel")),
            body: data.GetStringOrNull("message"),
            eventName: data.GetCustomProperty("eventId"),
            attributes: BuildAttributes(env, data));
    }

    private void MapEvent(Envelope env, JsonElement data, TelemetrySource source)
    {
        EmitLog(env, source,
            LogLevel.Information,
            body: data.GetStringOrNull("name") ?? env.Name,
            eventName: data.GetCustomProperty("eventId") ?? data.GetStringOrNull("name"),
            attributes: BuildAttributes(env, data));
    }

    private void MapException(Envelope env, JsonElement data, TelemetrySource source)
    {
        var level = SeverityMap.FromAppInsights(data.GetIntOrNull("severityLevel") ?? 3);
        var attributes = NewAttributeList(env, data);
        string? body = env.Name;

        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("exceptions", out var exceptions) &&
            exceptions.ValueKind == JsonValueKind.Array &&
            exceptions.GetArrayLength() > 0)
        {
            var first = exceptions[0];
            var type = first.GetStringOrNull("typeName");
            var message = first.GetStringOrNull("message");
            var stack = first.GetStringOrNull("stack") ?? first.GetStringOrNull("parsedStack");
            body = message ?? type;
            AddTag(attributes, "exception.type", type);
            AddTag(attributes, "exception.message", message);
            AddTag(attributes, "exception.stacktrace", stack);
        }

        EmitLog(env, source, level, body, eventName: data.GetCustomProperty("eventId") ?? "exception",
            attributes: _attributes.Filter(attributes));
    }

    private void MapUnsupported(Envelope env, JsonElement data, string baseType, TelemetrySource source)
    {
        Interlocked.Increment(ref _unsupportedCount);
        _logger.LogWarning("Unsupported AI telemetry type {BaseType}", baseType);

        var attributes = NewAttributeList(env, data);
        AddTag(attributes, "telemetry.mapping.status", "unsupported");
        AddTag(attributes, "ai.base_type", baseType);
        EmitLog(env, source,
            LogLevel.Warning,
            body: $"Unsupported telemetry type {baseType}",
            eventName: "unsupported",
            attributes: _attributes.Filter(attributes));
    }

    /// <summary>
    /// Logs keep the original BC timestamp and, when BC supplied an operation id,
    /// the trace context, so logs and spans of one operation correlate.
    /// </summary>
    private void EmitLog(
        Envelope env,
        TelemetrySource source,
        LogLevel level,
        string? body,
        string? eventName,
        IReadOnlyList<KeyValuePair<string, object?>> attributes)
    {
        ActivityTraceId? traceId = string.IsNullOrWhiteSpace(env.OperationId)
            ? null
            : TraceContextMapper.ResolveTraceId(env.OperationId);
        var spanId = traceId is null ? null : TraceContextMapper.ResolveParentSpanId(env.OperationParentId);

        _emitter.EmitLog(new LogItem(source, env.TimestampUtc, level, body, eventName, traceId, spanId, attributes));
    }

    // -- Spans ----------------------------------------------------------------

    private void MapRequest(Envelope env, JsonElement data, TelemetrySource source)
    {
        var tags = NewAttributeList(env, data);
        AttributeMapper.AddStatusCode(tags, data.GetStringOrNull("responseCode"));
        AddTag(tags, "url.full", data.GetStringOrNull("url"));
        EmitSpan(env, data, source, ActivityKind.Server,
            name: data.GetStringOrNull("name") ?? env.OperationName ?? "request",
            statusDescription: data.GetStringOrNull("responseCode"),
            tags: tags);
    }

    private void MapDependency(Envelope env, JsonElement data, TelemetrySource source)
    {
        var tags = NewAttributeList(env, data);
        AddTag(tags, "bc.dependency.type", data.GetStringOrNull("type"));
        AddTag(tags, "bc.dependency.target", data.GetStringOrNull("target"));
        AddTag(tags, "bc.dependency.data", data.GetStringOrNull("data"));
        AddTag(tags, "bc.dependency.result_code", data.GetStringOrNull("resultCode"));
        EmitSpan(env, data, source, ActivityKind.Client,
            name: data.GetStringOrNull("name") ?? "dependency",
            statusDescription: data.GetStringOrNull("resultCode"),
            tags: tags);
    }

    private void EmitSpan(
        Envelope env,
        JsonElement data,
        TelemetrySource source,
        ActivityKind kind,
        string name,
        string? statusDescription,
        List<KeyValuePair<string, object?>> tags)
    {
        _emitter.EmitSpan(new SpanItem(
            source,
            name,
            kind,
            StartUtc: env.TimestampUtc,
            Duration: ParseDuration(data.GetStringOrNull("duration")),
            TraceId: TraceContextMapper.ResolveTraceId(env.OperationId),
            SpanId: TraceContextMapper.ResolveSpanId(data.GetStringOrNull("id")),
            ParentSpanId: TraceContextMapper.ResolveParentSpanId(env.OperationParentId),
            Success: data.GetBoolOrNull("success") ?? true,
            StatusDescription: statusDescription,
            Attributes: _attributes.Filter(tags)));
    }

    // -- Metrics --------------------------------------------------------------

    private void MapMetric(Envelope env, JsonElement data, TelemetrySource source)
    {
        var tags = _attributes.Filter(NewAttributeList(env, data));
        foreach (var point in MetricMapper.Parse(data))
        {
            var kind = MetricMapper.Classify(point);
            _emitter.EmitMetric(new MetricItem(source, env.TimestampUtc, point, kind, WithMetricStats(point, kind, tags)));
        }
    }

    /// <summary>Pre-aggregated statistics travel loss-free as attributes of the data point.</summary>
    private static KeyValuePair<string, object?>[] WithMetricStats(
        AiMetricPoint point,
        MetricKind kind,
        IReadOnlyList<KeyValuePair<string, object?>> tags)
    {
        var list = new List<KeyValuePair<string, object?>>(tags)
        {
            new("bc.metric.kind", kind.ToString().ToLowerInvariant()),
        };

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

    // -- Shared ---------------------------------------------------------------

    private IReadOnlyList<KeyValuePair<string, object?>> BuildAttributes(Envelope env, JsonElement data)
        => _attributes.Filter(NewAttributeList(env, data));

    private static List<KeyValuePair<string, object?>> NewAttributeList(Envelope env, JsonElement data)
    {
        var attributes = new List<KeyValuePair<string, object?>>();
        AttributeMapper.AddContext(attributes, env);
        AttributeMapper.AddCustomProperties(attributes, data);
        return attributes;
    }

    private static void AddTag(List<KeyValuePair<string, object?>> tags, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            tags.Add(new(key, value));
        }
    }

    private static TimeSpan ParseDuration(string? duration)
    {
        if (string.IsNullOrEmpty(duration))
        {
            return TimeSpan.Zero;
        }

        // AI duration is formatted as "[d.]hh:mm:ss[.fffffff]".
        return TimeSpan.TryParse(duration, CultureInfo.InvariantCulture, out var ts)
            ? ts
            : TimeSpan.Zero;
    }
}

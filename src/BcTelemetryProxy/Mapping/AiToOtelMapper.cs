using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Model;
using BcTelemetryProxy.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
public sealed partial class AiToOtelMapper
{
    private readonly IOtelEmitter _emitter;
    private readonly AttributeMapper _attributes;
    private readonly ILogger<AiToOtelMapper> _logger;
    private readonly MappingOptions _mapping;
    private readonly HashSet<string> _databaseEvents;
    private readonly HashSet<string> _outgoingEvents;
    private readonly string[] _durationKeys;
    private readonly ErrorRegistry? _errors;
    private readonly bool _logNewErrors;
    private long _unsupportedCount;

    public AiToOtelMapper(
        IOtelEmitter emitter,
        AttributeMapper attributes,
        ILogger<AiToOtelMapper> logger,
        IOptions<MappingOptions>? mapping = null,
        ErrorRegistry? errors = null,
        IOptions<SupportOptions>? support = null)
    {
        _emitter = emitter;
        _attributes = attributes;
        _logger = logger;
        _mapping = mapping?.Value ?? new MappingOptions();
        _errors = errors;
        _logNewErrors = support?.Value.LogNewErrors ?? true;
        _databaseEvents = new HashSet<string>(_mapping.DatabaseEvents, StringComparer.OrdinalIgnoreCase);
        _outgoingEvents = new HashSet<string>(_mapping.OutgoingEvents, StringComparer.OrdinalIgnoreCase);

        // Configuration binding appends list entries to the defaults; keep each key once.
        _durationKeys = _mapping.DurationDimensions.Distinct(StringComparer.Ordinal).ToArray();
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
        EmitLogAndDerivedSpan(env, data, source,
            SeverityMap.FromAppInsights(data),
            body: data.GetStringOrNull("message"),
            eventName: data.GetCustomProperty("eventId"));
    }

    private void MapEvent(Envelope env, JsonElement data, TelemetrySource source)
    {
        EmitLogAndDerivedSpan(env, data, source,
            LogLevel.Information,
            body: data.GetStringOrNull("name") ?? env.Name,
            eventName: data.GetCustomProperty("eventId") ?? data.GetStringOrNull("name"));
    }

    /// <summary>
    /// Emits the log and, for events that carry a duration or report an error, a
    /// span (BC logs at the end of an operation). Errors carry an <c>exception</c>
    /// span event, which feeds exception views, and a <c>bc.error.fingerprint</c>.
    /// The log is linked to that span; all of it shares the operation's trace.
    /// </summary>
    private void EmitLogAndDerivedSpan(
        Envelope env,
        JsonElement data,
        TelemetrySource source,
        LogLevel level,
        string? body,
        string? eventName,
        IReadOnlyList<KeyValuePair<string, object?>>? attributes = null,
        ExceptionInfo? exception = null)
    {
        attributes ??= BuildAttributes(env, data);
        string? errorType = null;
        if (eventName is not null && _mapping.ErrorEvents.TryGetValue(eventName, out errorType) && level < LogLevel.Error)
        {
            level = LogLevel.Error;
        }

        var duration = _mapping.DeriveSpans ? EventDuration(data) : null;
        var failed = _mapping.DeriveSpans && level >= LogLevel.Error;
        if (duration is null && !failed)
        {
            EmitLog(env, source, level, body, eventName, attributes);
            return;
        }

        var parent = TraceContextMapper.ResolveParentSpanId(env.OperationParentId);
        var traceId = parent is null ? OperationTraceId(env) ?? ActivityTraceId.CreateRandom() : TraceContextMapper.ResolveTraceId(env.OperationId);
        var spanId = ActivitySpanId.CreateRandom();

        var (kind, extra) = SpanShape(eventName, data, source);
        if (duration is null && kind == ActivityKind.Server)
        {
            kind = ActivityKind.Internal;
        }

        var spanAttributes = new List<KeyValuePair<string, object?>>(attributes);
        if (eventName is not null)
        {
            spanAttributes.Add(new("bc.event.id", eventName));
        }

        spanAttributes.AddRange(extra);

        SpanEvent[]? events = null;
        if (failed)
        {
            exception ??= new ExceptionInfo(
                errorType ?? data.GetCustomProperty("failureReason") ?? eventName ?? "BusinessCentralError",
                data.GetCustomProperty("alErrorMessage") ?? body,
                data.GetCustomProperty("alStackTrace"));
            events = new[] { exception.ToEvent(env.TimestampUtc) };

            var fingerprint = ErrorRegistry.Fingerprint(
                eventName, exception.Type, data.GetCustomProperty("alObjectType"), data.GetCustomProperty("alObjectId"),
                data.GetCustomProperty("extensionId"), SpanName(exception.Message, null));
            spanAttributes.Add(new("bc.error.fingerprint", fingerprint));
            attributes = new List<KeyValuePair<string, object?>>(attributes) { new("bc.error.fingerprint", fingerprint) };
            RecordError(source, fingerprint, exception, eventName, env.TimestampUtc, traceId, spanAttributes);
        }

        var length = duration ?? TimeSpan.Zero;
        _emitter.EmitSpan(new SpanItem(
            source,
            Name: SpanName(body, eventName),
            Kind: kind,
            StartUtc: env.TimestampUtc - length,
            Duration: length,
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: parent,
            Success: IsSuccess(level, data),
            StatusDescription: data.GetCustomProperty("failureReason") ?? data.GetCustomProperty("result") ?? exception?.Type,
            Attributes: spanAttributes,
            Events: events));

        _emitter.EmitLog(new LogItem(source, env.TimestampUtc, level, body, eventName, traceId, spanId, attributes));
    }

    /// <summary>
    /// Trace of the BC operation (<c>ai.operation.id</c>, one per client action,
    /// web service call or task), so its events end up in one trace.
    /// </summary>
    private ActivityTraceId? OperationTraceId(Envelope env)
        => _mapping.CorrelateOperations && env.OperationId is { } operation
            ? TraceContextMapper.ResolveTraceId(operation)
            : null;

    /// <summary>
    /// Counts the error for support reports; the first occurrence of a kind also
    /// becomes a <c>bc.error.first_seen</c> log (alert: "new error").
    /// </summary>
    private void RecordError(
        TelemetrySource source,
        string fingerprint,
        ExceptionInfo exception,
        string? eventName,
        DateTime timeUtc,
        ActivityTraceId traceId,
        IReadOnlyList<KeyValuePair<string, object?>> attributes)
    {
        if (_errors?.Record(source, fingerprint, exception.Type, exception.Message, eventName, timeUtc, traceId, attributes, exception.StackTrace) != true ||
            !_logNewErrors)
        {
            return;
        }

        var first = new List<KeyValuePair<string, object?>>
        {
            new("bc.error.fingerprint", fingerprint),
            new("exception.type", exception.Type),
        };
        if (eventName is not null)
        {
            first.Add(new("bc.event.id", eventName));
        }

        foreach (var key in new[] { "extensionName", "extensionPublisher", "extensionVersion", "alObjectType", "alObjectId", "alObjectName" })
        {
            if (attributes.FirstOrDefault(a => a.Key == key).Value is { } value)
            {
                first.Add(new(key, value));
            }
        }

        _emitter.EmitLog(new LogItem(source, timeUtc, LogLevel.Warning,
            $"New error: {exception.Type}: {FirstLine(exception.Message)}", "bc.error.first_seen", traceId, null, _attributes.Filter(first)));
    }

    private static string? FirstLine(string? text)
    {
        var line = text?.Split('\n', 2)[0].Trim();
        return line is { Length: > 200 } ? line[..200] : line;
    }

    private TimeSpan? EventDuration(JsonElement data)
    {
        foreach (var key in _durationKeys)
        {
            var value = data.GetCustomProperty(key);
            if (value is null)
            {
                continue;
            }

            // e.g. alJobQueueExecutionTimeInMs = "88" (which would also parse as 88 days)
            if (key.EndsWith("InMs", StringComparison.Ordinal))
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms >= 0)
                {
                    return TimeSpan.FromMilliseconds(ms);
                }

                continue;
            }

            if (TimeSpan.TryParseExact(value, "c", CultureInfo.InvariantCulture, out var duration) && duration >= TimeSpan.Zero)
            {
                return duration;
            }
        }

        return null;
    }

    private (ActivityKind Kind, List<KeyValuePair<string, object?>> Attributes) SpanShape(string? eventName, JsonElement data, TelemetrySource source)
    {
        var extra = new List<KeyValuePair<string, object?>>();
        if (eventName is not null && _databaseEvents.Contains(eventName))
        {
            // Client span to the database tier; the server comes from the instance's
            // CustomSettings.config or Resource attributes (bc.database.server/name).
            extra.Add(new("db.system.name", "microsoft.sql_server"));
            extra.Add(new("db.system", "mssql"));
            extra.Add(new("peer.service", _mapping.DatabaseServiceName));
            if (source.Attributes.TryGetValue("bc.database.server", out var server))
            {
                extra.Add(new("server.address", server.Split('\\', ',')[0]));
                extra.Add(new("db.instance", server));
            }

            if (source.Attributes.TryGetValue("bc.database.name", out var database))
            {
                extra.Add(new("db.namespace", database));
                extra.Add(new("db.name", database));
            }

            return (ActivityKind.Client, extra);
        }

        if (eventName is not null && _outgoingEvents.Contains(eventName))
        {
            var endpoint = data.GetCustomProperty("endpoint");
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                extra.Add(new("server.address", uri.Host));
                extra.Add(new("server.port", (long)uri.Port));
            }

            var method = data.GetCustomProperty("httpMethod");
            if (!string.IsNullOrEmpty(method))
            {
                extra.Add(new("http.request.method", method));
            }

            return (ActivityKind.Client, extra);
        }

        return (ActivityKind.Server, extra);
    }

    /// <summary>A failing result, an HTTP 5xx or an error-level event marks the span as failed.</summary>
    private static bool IsSuccess(LogLevel level, JsonElement data)
    {
        if (level >= LogLevel.Error)
        {
            return false;
        }

        var result = data.GetCustomProperty("result");
        if (!string.IsNullOrEmpty(result) && !Envelope.IsPlaceholder(result) &&
            !result.Equals("Success", StringComparison.OrdinalIgnoreCase) &&
            !result.Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !(int.TryParse(data.GetCustomProperty("httpStatusCode"), out var status) && status >= 500);
    }

    /// <summary>
    /// Span names group operations, so they must have low cardinality: ids in BC
    /// messages ("Task 1132…-…f created: 448 …", "Job queue entry finished: 8C8F…")
    /// are replaced by placeholders; the values stay in the attributes.
    /// </summary>
    public static string SpanName(string? body, string? eventName)
    {
        var name = string.IsNullOrWhiteSpace(body) ? eventName ?? "bc.event" : body.Trim();
        var newline = name.IndexOfAny(new[] { '\r', '\n' });
        if (newline > 0)
        {
            name = name[..newline];
        }

        name = GuidPattern().Replace(name, "{id}");
        name = TimestampPattern().Replace(name, "{time}");
        name = NumberPattern().Replace(name, "{n}");
        return name.Length <= 120 ? name : name[..120];
    }

    [GeneratedRegex(@"\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?Z?")]
    private static partial Regex TimestampPattern();

    [GeneratedRegex(@"(?<![\w.])\d+(\.\d+)*(?![\w.])")]
    private static partial Regex NumberPattern();

    private sealed record ExceptionInfo(string Type, string? Message, string? StackTrace)
    {
        public SpanEvent ToEvent(DateTime timeUtc)
        {
            var attributes = new List<KeyValuePair<string, object?>> { new("exception.type", Type) };
            if (!string.IsNullOrEmpty(Message))
            {
                attributes.Add(new("exception.message", Message));
            }

            if (!string.IsNullOrEmpty(StackTrace))
            {
                attributes.Add(new("exception.stacktrace", StackTrace));
            }

            return new SpanEvent("exception", timeUtc, attributes);
        }
    }

    private void MapException(Envelope env, JsonElement data, TelemetrySource source)
    {
        var level = SeverityMap.FromAppInsights(data, LogLevel.Error);
        var attributes = NewAttributeList(env, data);
        string? body = env.Name;
        string? type = null, message = null, stack = null;

        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("exceptions", out var exceptions) &&
            exceptions.ValueKind == JsonValueKind.Array &&
            exceptions.GetArrayLength() > 0)
        {
            var first = exceptions[0];
            type = first.GetStringOrNull("typeName");
            message = first.GetStringOrNull("message");
            stack = first.GetStringOrNull("stack") ?? first.GetStringOrNull("parsedStack");
            body = message ?? type;
            AddTag(attributes, "exception.type", type);
            AddTag(attributes, "exception.message", message);
            AddTag(attributes, "exception.stacktrace", stack);
        }

        EmitLogAndDerivedSpan(env, data, source, level, body,
            eventName: data.GetCustomProperty("eventId") ?? "exception",
            attributes: _attributes.Filter(attributes),
            exception: new ExceptionInfo(type ?? "exception", message ?? body, stack));
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
    /// Logs keep the original BC timestamp. They carry the trace of their operation
    /// (<see cref="MappingOptions.CorrelateOperations"/>) and, when they belong to a
    /// span (valid <c>ai.operation.parentId</c>), its span id. The id is always kept
    /// as <c>bc.operation.id</c>.
    /// </summary>
    private void EmitLog(
        Envelope env,
        TelemetrySource source,
        LogLevel level,
        string? body,
        string? eventName,
        IReadOnlyList<KeyValuePair<string, object?>> attributes)
    {
        var spanId = env.OperationId is null ? null : TraceContextMapper.ResolveParentSpanId(env.OperationParentId);
        var traceId = spanId is null ? OperationTraceId(env) : TraceContextMapper.ResolveTraceId(env.OperationId);

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

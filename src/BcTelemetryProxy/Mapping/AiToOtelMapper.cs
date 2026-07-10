using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BcTelemetryProxy.Model;
using Microsoft.Extensions.Logging;

namespace BcTelemetryProxy.Mapping;

/// <summary>
/// Translates Business Central Application Insights envelopes into OpenTelemetry
/// signals and hands them to an <see cref="IOtelEmitter"/>.
///
/// Mapping overview:
///   MessageData          -> log record (severity + eventId + custom dimensions)
///   EventData            -> log record (Info)
///   ExceptionData        -> log record (Error, exception.* semantics)
///   RequestData          -> server span
///   RemoteDependencyData -> client span
///   MetricData           -> metric (histogram record)
/// </summary>
public sealed class AiToOtelMapper
{
    private readonly IOtelEmitter _emitter;

    public AiToOtelMapper(IOtelEmitter emitter) => _emitter = emitter;

    public void Map(Envelope envelope)
    {
        var baseType = envelope.Data?.BaseType;
        if (baseType is null || envelope.Data is null)
        {
            return;
        }

        var data = envelope.Data.BaseData;
        switch (baseType)
        {
            case "MessageData":
                MapMessage(envelope, data);
                break;
            case "EventData":
                MapEvent(envelope, data);
                break;
            case "ExceptionData":
                MapException(envelope, data);
                break;
            case "RequestData":
                MapRequest(envelope, data);
                break;
            case "RemoteDependencyData":
                MapDependency(envelope, data);
                break;
            case "MetricData":
                MapMetric(envelope, data);
                break;
            default:
                // Unknown telemetry type — surface it as an informational log so
                // nothing is silently dropped.
                MapEvent(envelope, data);
                break;
        }
    }

    // -- Logs -----------------------------------------------------------------

    private void MapMessage(Envelope env, JsonElement data)
    {
        var level = SeverityMap.FromAppInsights(GetInt(data, "severityLevel"));
        _emitter.EmitLog(
            level,
            body: GetString(data, "message"),
            eventName: GetProperty(data, "eventId"),
            attributes: BuildAttributes(env, data));
    }

    private void MapEvent(Envelope env, JsonElement data)
    {
        _emitter.EmitLog(
            LogLevel.Information,
            body: GetString(data, "name") ?? env.Name,
            eventName: GetProperty(data, "eventId") ?? GetString(data, "name"),
            attributes: BuildAttributes(env, data));
    }

    private void MapException(Envelope env, JsonElement data)
    {
        var level = SeverityMap.FromAppInsights(GetInt(data, "severityLevel") ?? 3);
        var attributes = BuildAttributes(env, data);
        string? body = env.Name;

        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("exceptions", out var exceptions) &&
            exceptions.ValueKind == JsonValueKind.Array &&
            exceptions.GetArrayLength() > 0)
        {
            var first = exceptions[0];
            var type = GetString(first, "typeName");
            var message = GetString(first, "message");
            var stack = GetString(first, "stack") ?? GetString(first, "parsedStack");
            body = message ?? type;
            AddTag(attributes, "exception.type", type);
            AddTag(attributes, "exception.message", message);
            AddTag(attributes, "exception.stacktrace", stack);
        }

        _emitter.EmitLog(level, body, eventName: "exception", attributes: attributes);
    }

    // -- Spans ----------------------------------------------------------------

    private void MapRequest(Envelope env, JsonElement data)
    {
        var tags = BuildAttributes(env, data);
        AddTag(tags, "http.response.status_code", GetString(data, "responseCode"));
        AddTag(tags, "url.full", GetString(data, "url"));
        _emitter.EmitSpan(
            name: GetString(data, "name") ?? env.OperationName ?? "request",
            kind: ActivityKind.Server,
            startUtc: env.TimestampUtc,
            duration: ParseDuration(GetString(data, "duration")),
            traceId: TraceId(env),
            parentSpanId: ParentSpanId(env),
            success: GetBool(data, "success") ?? true,
            statusDescription: GetString(data, "responseCode"),
            tags: tags);
    }

    private void MapDependency(Envelope env, JsonElement data)
    {
        var tags = BuildAttributes(env, data);
        AddTag(tags, "bc.dependency.type", GetString(data, "type"));
        AddTag(tags, "bc.dependency.target", GetString(data, "target"));
        AddTag(tags, "bc.dependency.data", GetString(data, "data"));
        AddTag(tags, "bc.dependency.result_code", GetString(data, "resultCode"));
        _emitter.EmitSpan(
            name: GetString(data, "name") ?? "dependency",
            kind: ActivityKind.Client,
            startUtc: env.TimestampUtc,
            duration: ParseDuration(GetString(data, "duration")),
            traceId: TraceId(env),
            parentSpanId: ParentSpanId(env),
            success: GetBool(data, "success") ?? true,
            statusDescription: GetString(data, "resultCode"),
            tags: tags);
    }

    // -- Metrics --------------------------------------------------------------

    private void MapMetric(Envelope env, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("metrics", out var metrics) ||
            metrics.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var tags = BuildAttributes(env, data);
        foreach (var metric in metrics.EnumerateArray())
        {
            var name = GetString(metric, "name");
            var value = GetDouble(metric, "value");
            if (name is not null && value is not null)
            {
                _emitter.EmitMetric(name, value.Value, tags);
            }
        }
    }

    // -- Shared builders ------------------------------------------------------

    private static List<KeyValuePair<string, object?>> BuildAttributes(Envelope env, JsonElement data)
    {
        var attributes = new List<KeyValuePair<string, object?>>();
        foreach (var (key, value) in ContextPairs(env))
        {
            AddTag(attributes, key, value);
        }

        foreach (var (key, value) in EnumerateProperties(data))
        {
            AddTag(attributes, key, value);
        }

        return attributes;
    }

    private static IEnumerable<KeyValuePair<string, string?>> ContextPairs(Envelope env)
    {
        yield return new("service.instance.id", env.CloudRoleInstance);
        yield return new("bc.cloud.role", env.CloudRole);
        yield return new("bc.operation.name", env.OperationName);
        yield return new("bc.operation.id", env.OperationId);
        yield return new("enduser.id", env.UserId);
        yield return new("bc.session.id", env.SessionId);
    }

    // -- JSON helpers ---------------------------------------------------------

    private static IEnumerable<KeyValuePair<string, string?>> EnumerateProperties(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("properties", out var props) &&
            props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                yield return new(prop.Name, prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.GetRawText());
            }
        }
    }

    private static string? GetProperty(JsonElement data, string propertyName)
    {
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("properties", out var props) &&
            props.ValueKind == JsonValueKind.Object &&
            props.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static string? GetString(JsonElement data, string name)
        => data.ValueKind == JsonValueKind.Object &&
           data.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? GetInt(JsonElement data, string name)
        => data.ValueKind == JsonValueKind.Object &&
           data.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.Number &&
           v.TryGetInt32(out var i)
            ? i
            : null;

    private static double? GetDouble(JsonElement data, string name)
        => data.ValueKind == JsonValueKind.Object &&
           data.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.Number &&
           v.TryGetDouble(out var d)
            ? d
            : null;

    private static bool? GetBool(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : null,
            _ => null,
        };
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

    // -- Trace context helpers ------------------------------------------------

    private static ActivityTraceId TraceId(Envelope env)
        => TryTraceId(env, out var id) ? id : ActivityTraceId.CreateRandom();

    private static ActivitySpanId? ParentSpanId(Envelope env)
        => TryParentSpanId(env, out var id) ? id : null;

    private static bool TryTraceId(Envelope env, out ActivityTraceId traceId)
    {
        var hex = Normalize(env.OperationId, 32);
        if (hex is not null)
        {
            traceId = ActivityTraceId.CreateFromString(hex.AsSpan());
            return true;
        }

        traceId = default;
        return false;
    }

    private static bool TryParentSpanId(Envelope env, out ActivitySpanId spanId)
    {
        var hex = Normalize(env.OperationParentId, 16);
        if (hex is not null)
        {
            spanId = ActivitySpanId.CreateFromString(hex.AsSpan());
            return true;
        }

        spanId = default;
        return false;
    }

    /// <summary>
    /// Coerces an AI operation id into a lowercase hex string of exactly
    /// <paramref name="length"/> chars (padding/truncating as needed), or null
    /// when it contains no usable hex.
    /// </summary>
    private static string? Normalize(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Span<char> buffer = stackalloc char[length];
        var count = 0;
        foreach (var c in value)
        {
            if (Uri.IsHexDigit(c))
            {
                buffer[count++] = char.ToLowerInvariant(c);
                if (count == length)
                {
                    break;
                }
            }
        }

        if (count == 0)
        {
            return null;
        }

        while (count < length)
        {
            buffer[count++] = '0';
        }

        return new string(buffer);
    }

    private static void AddTag(List<KeyValuePair<string, object?>> tags, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            tags.Add(new(key, value));
        }
    }
}

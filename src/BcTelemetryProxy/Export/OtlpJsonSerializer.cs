using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using BcTelemetryProxy.Mapping;
using BcTelemetryProxy.Model;
using BcTelemetryProxy.Telemetry;
using Microsoft.Extensions.Logging;

namespace BcTelemetryProxy.Export;

/// <summary>Writer settings shared by the exporter and the debug capture.</summary>
public static class OtlpJson
{
    /// <summary>
    /// Non-ASCII text (umlauts in BC messages) is written as UTF-8 instead of
    /// \uXXXX escapes; the output is never embedded in HTML.
    /// </summary>
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// Writes OTLP/JSON export requests (opentelemetry-proto, JSON mapping: hex trace
/// and span ids, 64-bit integers as strings). Items are grouped by
/// <see cref="TelemetrySource"/> into one resource entry each.
/// </summary>
public static class OtlpJsonSerializer
{
    private static readonly string ScopeVersion =
        typeof(OtlpJsonSerializer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    public static void WriteLogs(Utf8JsonWriter w, IEnumerable<LogItem> items, DateTime observedUtc)
    {
        w.WriteStartObject();
        w.WriteStartArray("resourceLogs");
        foreach (var group in items.GroupBy(i => i.Source))
        {
            w.WriteStartObject();
            WriteResource(w, group.Key);
            w.WriteStartArray("scopeLogs");
            w.WriteStartObject();
            WriteScope(w);
            w.WriteStartArray("logRecords");
            foreach (var log in group)
            {
                w.WriteStartObject();
                w.WriteString("timeUnixNano", UnixNanos(log.TimeUtc));
                w.WriteString("observedTimeUnixNano", UnixNanos(observedUtc));
                w.WriteNumber("severityNumber", SeverityNumber(log.Level));
                w.WriteString("severityText", SeverityText(log.Level));
                if (log.Body is not null)
                {
                    w.WriteStartObject("body");
                    w.WriteString("stringValue", log.Body);
                    w.WriteEndObject();
                }

                WriteAttributes(w, log.Attributes, log.EventName is null ? null : new("event.name", log.EventName));
                if (log.TraceId is { } traceId)
                {
                    w.WriteString("traceId", traceId.ToHexString());
                }

                if (log.SpanId is { } spanId)
                {
                    w.WriteString("spanId", spanId.ToHexString());
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static void WriteSpans(Utf8JsonWriter w, IEnumerable<SpanItem> items)
    {
        w.WriteStartObject();
        w.WriteStartArray("resourceSpans");
        foreach (var group in items.GroupBy(i => i.Source))
        {
            w.WriteStartObject();
            WriteResource(w, group.Key);
            w.WriteStartArray("scopeSpans");
            w.WriteStartObject();
            WriteScope(w);
            w.WriteStartArray("spans");
            foreach (var span in group)
            {
                w.WriteStartObject();
                w.WriteString("traceId", span.TraceId.ToHexString());
                w.WriteString("spanId", span.SpanId.ToHexString());
                if (span.ParentSpanId is { } parent)
                {
                    w.WriteString("parentSpanId", parent.ToHexString());
                }

                w.WriteString("name", span.Name);
                w.WriteNumber("kind", SpanKind(span.Kind));
                w.WriteString("startTimeUnixNano", UnixNanos(span.StartUtc));
                w.WriteString("endTimeUnixNano", UnixNanos(span.StartUtc + span.Duration));
                WriteAttributes(w, span.Attributes);
                if (span.Events is { Count: > 0 } events)
                {
                    w.WriteStartArray("events");
                    foreach (var e in events)
                    {
                        w.WriteStartObject();
                        w.WriteString("timeUnixNano", UnixNanos(e.TimeUtc));
                        w.WriteString("name", e.Name);
                        WriteAttributes(w, e.Attributes);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                }

                w.WriteStartObject("status");
                if (span.Success)
                {
                    // Unset: OTel convention for successful spans.
                    w.WriteNumber("code", 0);
                }
                else
                {
                    w.WriteNumber("code", 2);
                    if (span.StatusDescription is not null)
                    {
                        w.WriteString("message", span.StatusDescription);
                    }
                }

                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>
    /// AI metric points are already aggregated by the sender, so each becomes a
    /// gauge data point at its original time; count/min/max/stddev travel as
    /// <c>bc.metric.*</c> attributes (see the mapper).
    /// </summary>
    public static void WriteMetrics(Utf8JsonWriter w, IEnumerable<MetricItem> items)
    {
        w.WriteStartObject();
        w.WriteStartArray("resourceMetrics");
        foreach (var group in items.GroupBy(i => i.Source))
        {
            w.WriteStartObject();
            WriteResource(w, group.Key);
            w.WriteStartArray("scopeMetrics");
            w.WriteStartObject();
            WriteScope(w);
            w.WriteStartArray("metrics");
            foreach (var metric in group.GroupBy(m => m.Point.Name, StringComparer.Ordinal))
            {
                var first = metric.First();
                var cumulative = first is { Kind: MetricKind.Counter or MetricKind.UpDownCounter, StartUtc: not null };

                w.WriteStartObject();
                w.WriteString("name", metric.Key);
                if (first.Description is not null)
                {
                    w.WriteString("description", first.Description);
                }

                if (first.Unit is not null)
                {
                    w.WriteString("unit", first.Unit);
                }

                w.WriteStartObject(cumulative ? "sum" : "gauge");
                w.WriteStartArray("dataPoints");
                foreach (var point in metric)
                {
                    w.WriteStartObject();
                    if (cumulative && point.StartUtc is { } start)
                    {
                        w.WriteString("startTimeUnixNano", UnixNanos(start));
                    }

                    w.WriteString("timeUnixNano", UnixNanos(point.TimeUtc));
                    w.WriteNumber("asDouble", point.Point.Value);
                    WriteAttributes(w, point.Attributes);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                if (cumulative)
                {
                    w.WriteNumber("aggregationTemporality", 2); // CUMULATIVE
                    w.WriteBoolean("isMonotonic", first.Kind == MetricKind.Counter);
                }

                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteResource(Utf8JsonWriter w, TelemetrySource source)
    {
        var attributes = new List<KeyValuePair<string, object?>>
        {
            new("service.name", source.ServiceName),
            new("service.namespace", source.ServiceNamespace),
            new("service.instance.id", source.ServiceInstanceId),
            new("service.version", source.ServiceVersion),
            new("host.name", source.HostName),
            new("bc.server_instance", source.ServerInstance),
            new("os.type", "windows"), // Business Central servers run on Windows only
            // Current semantic convention plus the legacy key many UIs
            // (incl. GitLab Observability) still filter on.
            new("deployment.environment.name", source.DeploymentEnvironment),
            new("deployment.environment", source.DeploymentEnvironment),
            new("telemetry.sdk.name", "bc-telemetry-proxy"),
            new("telemetry.sdk.language", "dotnet"),
            new("telemetry.sdk.version", ScopeVersion),
        };

        foreach (var (key, value) in source.Attributes)
        {
            if (!attributes.Exists(a => a.Key == key))
            {
                attributes.Add(new(key, value));
            }
        }

        w.WriteStartObject("resource");
        WriteAttributes(w, attributes);
        w.WriteEndObject();
    }

    private static void WriteScope(Utf8JsonWriter w)
    {
        w.WriteStartObject("scope");
        w.WriteString("name", TelemetryConstants.ScopeName);
        w.WriteString("version", ScopeVersion);
        w.WriteEndObject();
    }

    private static void WriteAttributes(
        Utf8JsonWriter w,
        IReadOnlyList<KeyValuePair<string, object?>> attributes,
        KeyValuePair<string, object?>? extra = null)
    {
        w.WriteStartArray("attributes");
        if (extra is { } e)
        {
            WriteKeyValue(w, e.Key, e.Value);
        }

        foreach (var (key, value) in attributes)
        {
            WriteKeyValue(w, key, value);
        }

        w.WriteEndArray();
    }

    private static void WriteKeyValue(Utf8JsonWriter w, string key, object? value)
    {
        if (value is null || value is string { Length: 0 })
        {
            return;
        }

        w.WriteStartObject();
        w.WriteString("key", key);
        w.WriteStartObject("value");
        switch (value)
        {
            case string s:
                w.WriteString("stringValue", s);
                break;
            case bool b:
                w.WriteBoolean("boolValue", b);
                break;
            case int or long or short or byte:
                w.WriteString("intValue", Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                break;
            case double d when double.IsFinite(d):
                w.WriteNumber("doubleValue", d);
                break;
            case float f when float.IsFinite(f):
                w.WriteNumber("doubleValue", f);
                break;
            default:
                w.WriteString("stringValue", Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static string UnixNanos(DateTime utc)
    {
        var ticks = DateTime.SpecifyKind(utc, DateTimeKind.Utc).Ticks - DateTime.UnixEpoch.Ticks;
        return (Math.Max(ticks, 0) * 100).ToString(CultureInfo.InvariantCulture);
    }

    private static int SpanKind(ActivityKind kind) => kind switch
    {
        ActivityKind.Internal => 1,
        ActivityKind.Server => 2,
        ActivityKind.Client => 3,
        ActivityKind.Producer => 4,
        ActivityKind.Consumer => 5,
        _ => 0,
    };

    private static int SeverityNumber(LogLevel level) => level switch
    {
        LogLevel.Trace => 1,
        LogLevel.Debug => 5,
        LogLevel.Information => 9,
        LogLevel.Warning => 13,
        LogLevel.Error => 17,
        LogLevel.Critical => 21,
        _ => 0,
    };

    private static string SeverityText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "UNSPECIFIED",
    };
}

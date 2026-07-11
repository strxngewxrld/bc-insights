using System.Text.Json;

namespace BcTelemetryProxy.Mapping;

/// <summary>Internal, backend-neutral representation of one AI metric data point.</summary>
public sealed record AiMetricPoint(
    string Name,
    double Value,
    int? Count,
    double? Min,
    double? Max,
    double? StandardDeviation);

/// <summary>The OpenTelemetry instrument shape a metric point maps onto.</summary>
public enum MetricKind
{
    /// <summary>Single, point-in-time measurement.</summary>
    Gauge,

    /// <summary>Monotonically increasing count.</summary>
    Counter,

    /// <summary>Pre-aggregated distribution (count/min/max/stddev present).</summary>
    Histogram,
}

/// <summary>Parses and classifies AI metric points into an OTLP-friendly shape.</summary>
public static class MetricMapper
{
    private static readonly string[] CounterNameHints =
    {
        "count", "total", "number of", "requests", "errors", "calls",
    };

    public static IEnumerable<AiMetricPoint> Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("metrics", out var metrics) ||
            metrics.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var metric in metrics.EnumerateArray())
        {
            var name = metric.GetStringOrNull("name");
            var value = metric.GetDoubleOrNull("value");
            if (name is null || value is null)
            {
                continue;
            }

            yield return new AiMetricPoint(
                name,
                value.Value,
                metric.GetIntOrNull("count"),
                metric.GetDoubleOrNull("min"),
                metric.GetDoubleOrNull("max"),
                metric.GetDoubleOrNull("stdDev"));
        }
    }

    /// <summary>
    /// Best-effort classification. Aggregated points (count &gt; 1 or any of
    /// min/max/stddev) are distributions; simple counters are recognised by
    /// name; everything else is treated as a gauge-like single measurement.
    /// </summary>
    public static MetricKind Classify(AiMetricPoint point)
    {
        var aggregated = (point.Count is > 1)
            || point.Min.HasValue
            || point.Max.HasValue
            || point.StandardDeviation.HasValue;

        if (aggregated)
        {
            return MetricKind.Histogram;
        }

        var lower = point.Name.ToLowerInvariant();
        foreach (var hint in CounterNameHints)
        {
            if (lower.Contains(hint))
            {
                return MetricKind.Counter;
            }
        }

        return MetricKind.Gauge;
    }
}

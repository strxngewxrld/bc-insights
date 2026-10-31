using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace BcTelemetryProxy.HostTelemetry;

/// <summary>Maps Business Central performance counter names to OTLP metric names and units.</summary>
public static class BcCounterNames
{
    /// <summary>
    /// "% Primary key cache hit rate" -> (bc.server.primary_key_cache_hit_rate, %),
    /// "Heartbeat time (ms)" -> (bc.server.heartbeat_time, ms),
    /// "Server operations/sec" -> (bc.server.server_operations_per_sec, 1/s).
    /// </summary>
    public static (string Name, string? Unit) MetricName(string counterName)
    {
        string? unit = null;
        var text = counterName.Trim();
        if (text.StartsWith('%'))
        {
            unit = "%";
        }

        if (text.Contains("(ms)", StringComparison.OrdinalIgnoreCase))
        {
            unit = "ms";
            text = text.Replace("(ms)", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        if (text.EndsWith("/sec", StringComparison.OrdinalIgnoreCase))
        {
            unit = "1/s";
            text = text[..^4] + " per sec";
        }

        var sb = new StringBuilder("bc.server.");
        var underscore = false;
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
                underscore = false;
            }
            else if (!underscore && sb[^1] != '.')
            {
                sb.Append('_');
                underscore = true;
            }
        }

        return (sb.ToString().TrimEnd('_'), unit);
    }

    /// <summary>Denominators of fraction/average counters; meaningless on their own.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsBaseCounter(PerformanceCounterType type)
        => type is PerformanceCounterType.RawBase or PerformanceCounterType.AverageBase
            or PerformanceCounterType.CounterMultiBase or PerformanceCounterType.SampleBase;

    /// <summary>
    /// The counter type decides what <c>NextValue()</c> returns (e.g. timer
    /// averages are seconds, whatever the name says); the name is the fallback.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? Unit(PerformanceCounterType type, string? nameUnit) => type switch
    {
        PerformanceCounterType.RawFraction or PerformanceCounterType.SampleFraction => "%",
        PerformanceCounterType.RateOfCountsPerSecond32 or PerformanceCounterType.RateOfCountsPerSecond64 => "1/s",
        PerformanceCounterType.AverageTimer32 or PerformanceCounterType.ElapsedTime => "s",
        _ => nameUnit,
    };
}

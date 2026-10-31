using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BcTelemetryProxy.Mapping;

/// <summary>
/// Maps the Application Insights <c>severityLevel</c> (0..4) used by Business
/// Central telemetry onto <see cref="LogLevel"/> values, which the OpenTelemetry
/// logging provider then translates into OTLP log severities.
/// </summary>
public static class SeverityMap
{
    /// <summary>
    /// Reads <c>severityLevel</c> from AI <c>baseData</c>. The Application Insights
    /// SDK used by Business Central serialises it as a name ("Information",
    /// "Warning", ...); the numeric form is accepted too.
    /// </summary>
    public static LogLevel FromAppInsights(JsonElement data, LogLevel fallback = LogLevel.Information)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("severityLevel", out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var level) => FromAppInsights(level, fallback),
            JsonValueKind.String => value.GetString()?.ToLowerInvariant() switch
            {
                "verbose" => LogLevel.Debug,
                "information" => LogLevel.Information,
                "warning" => LogLevel.Warning,
                "error" => LogLevel.Error,
                "critical" => LogLevel.Critical,
                _ => fallback,
            },
            _ => fallback,
        };
    }

    /// <summary>
    /// AI severity levels: 0=Verbose, 1=Information, 2=Warning, 3=Error, 4=Critical.
    /// </summary>
    public static LogLevel FromAppInsights(int? severityLevel, LogLevel fallback = LogLevel.Information)
    {
        return severityLevel switch
        {
            0 => LogLevel.Debug,
            1 => LogLevel.Information,
            2 => LogLevel.Warning,
            3 => LogLevel.Error,
            4 => LogLevel.Critical,
            _ => fallback,
        };
    }
}

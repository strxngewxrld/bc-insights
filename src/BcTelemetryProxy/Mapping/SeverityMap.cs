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
    /// AI severity levels: 0=Verbose, 1=Information, 2=Warning, 3=Error, 4=Critical.
    /// </summary>
    public static LogLevel FromAppInsights(int? severityLevel)
    {
        return severityLevel switch
        {
            0 => LogLevel.Debug,
            1 => LogLevel.Information,
            2 => LogLevel.Warning,
            3 => LogLevel.Error,
            4 => LogLevel.Critical,
            _ => LogLevel.Information,
        };
    }
}

namespace BcTelemetryProxy.Configuration;

/// <summary>Bounds the number of dynamically created OpenTelemetry instruments.</summary>
public sealed class MetricsOptions
{
    public const string SectionName = "Metrics";

    /// <summary>
    /// Maximum number of distinct metric instruments created from BC metric
    /// names. Beyond this cap, points are forwarded loss-light as log records
    /// instead, preventing unbounded memory growth from runaway metric names.
    /// </summary>
    public int MaxInstruments { get; set; } = 500;
}

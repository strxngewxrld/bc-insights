namespace BcTelemetryProxy.Configuration;

/// <summary>
/// Defaults for the OTLP resource of each telemetry source, plus per-source
/// overrides. A source is identified by host and BC server instance (from the
/// ingestion URL, the envelope, or the defaults below).
/// </summary>
public sealed class ResourceOptions
{
    public const string SectionName = "Resource";

    public string ServiceName { get; set; } = "business-central";

    public string ServiceNamespace { get; set; } = "dynamics365-bc";

    public string? DeploymentEnvironment { get; set; } = "production";

    /// <summary>Host name used when neither the URL nor the envelope provides one.</summary>
    public string? HostName { get; set; }

    /// <summary>BC server instance used when the ingestion URL does not name one.</summary>
    public string? BcServerInstance { get; set; }

    /// <summary>
    /// Per-source overrides keyed by <c>host/instance</c>, <c>instance</c> or
    /// <c>host</c> (case-insensitive, most specific match wins).
    /// </summary>
    public Dictionary<string, SourceOverride> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SourceOverride
{
    public string? ServiceName { get; set; }

    public string? DeploymentEnvironment { get; set; }
}

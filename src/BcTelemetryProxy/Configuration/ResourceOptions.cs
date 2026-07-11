namespace BcTelemetryProxy.Configuration;

/// <summary>
/// Proxy-level resource attributes describing the Business Central environment
/// this proxy fronts. Null values fall back to sensible machine defaults.
/// </summary>
public sealed class ResourceOptions
{
    public const string SectionName = "Resource";

    public string ServiceName { get; set; } = "business-central";

    public string ServiceNamespace { get; set; } = "dynamics365-bc";

    public string? ServiceInstanceId { get; set; }

    public string? DeploymentEnvironment { get; set; } = "production";

    public string? HostName { get; set; }

    public string? BcServerInstance { get; set; }

    public string? BcTenantId { get; set; }
}

namespace BcTelemetryProxy.Configuration;

/// <summary>
/// Configurable attribute processing applied before telemetry is exported, so
/// sensitive values in AI properties or BC context are not forwarded verbatim.
/// Matching is case-insensitive on the attribute key.
/// </summary>
public sealed class TelemetryPrivacyOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>
    /// When non-empty, only these attribute keys are exported (allowlist). All
    /// other keys are dropped. Empty means "allow everything not denied".
    /// </summary>
    public List<string> AllowedAttributes { get; set; } = new();

    /// <summary>Attribute keys removed entirely (denylist).</summary>
    public List<string> DeniedAttributes { get; set; } = new();

    /// <summary>Attribute keys whose value is replaced with a redaction marker.</summary>
    public List<string> RedactedAttributes { get; set; } = new();

    /// <summary>Attribute keys whose value is replaced with a stable SHA-256 hash.</summary>
    public List<string> HashedAttributes { get; set; } = new();

    /// <summary>Marker written in place of a redacted value.</summary>
    public string RedactionMarker { get; set; } = "[REDACTED]";
}

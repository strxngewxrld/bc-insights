namespace BcTelemetryProxy.Telemetry;

/// <summary>
/// Shared names used to register the OpenTelemetry sources so that the SDK
/// pipeline (in <c>Program.cs</c>) and the emitter agree on what to listen to.
/// </summary>
public static class TelemetryConstants
{
    /// <summary>Logical service name reported to GitLab Observability.</summary>
    public const string ServiceName = "business-central";

    /// <summary>ActivitySource name for BC request/dependency spans.</summary>
    public const string ActivitySourceName = "BusinessCentral.Telemetry";

    /// <summary>Meter name for BC metric telemetry.</summary>
    public const string MeterName = "BusinessCentral.Telemetry";

    /// <summary>Logger (instrumentation scope) name for BC trace/message logs.</summary>
    public const string LoggerName = "BusinessCentral.Telemetry";
}

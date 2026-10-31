namespace BcTelemetryProxy.Configuration;

/// <summary>
/// Telemetry the proxy collects itself on the machine it runs on (Windows only):
/// server metrics, Business Central performance counters and Windows Event Log
/// entries of the BC server. Most useful with one proxy per BC server.
/// </summary>
public sealed class HostTelemetryOptions
{
    public const string SectionName = "HostTelemetry";

    /// <summary>Master switch; ignored (off) on non-Windows hosts.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Sampling interval for metrics.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>CPU, memory, file systems, disks, network, uptime (OpenTelemetry <c>system.*</c> names).</summary>
    public bool SystemMetrics { get; set; } = true;

    /// <summary>All counters of <see cref="BcCounterCategory"/>, one resource per BC server instance.</summary>
    public bool BcCounters { get; set; } = true;

    /// <summary>Performance counter category of the BC server (unchanged since NAV).</summary>
    public string BcCounterCategory { get; set; } = "Microsoft Dynamics NAV";

    /// <summary>Forward Windows Event Log entries of <see cref="EventLogChannels"/> as logs.</summary>
    public bool EventLog { get; set; } = true;

    public List<string> EventLogChannels { get; set; } = new() { "Microsoft-DynamicsNAV-Server/Admin" };

    /// <summary>Highest Windows event level forwarded: 1=Critical, 2=Error, 3=Warning, 4=Information.</summary>
    public int EventLogMaxLevel { get; set; } = 3;
}

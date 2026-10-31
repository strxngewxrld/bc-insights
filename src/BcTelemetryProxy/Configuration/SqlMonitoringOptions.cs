namespace BcTelemetryProxy.Configuration;

/// <summary>
/// Agentless SQL Server monitoring: the proxy reads dynamic management views of
/// the BC database servers (also remote ones in a 2-tier setup). The proxy's
/// identity needs <c>VIEW SERVER STATE</c> (SQL 2022: <c>VIEW SERVER PERFORMANCE STATE</c>).
/// </summary>
public sealed class SqlMonitoringOptions
{
    public const string SectionName = "SqlMonitoring";

    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Monitor the database servers of the BC instances on this machine (CustomSettings.config).</summary>
    public bool AutoDiscover { get; set; } = true;

    /// <summary>Additional servers: display name -> connection string.</summary>
    public Dictionary<string, string> Servers { get; set; } = new();

    /// <summary>Connection string for discovered servers; <c>{server}</c> is replaced.</summary>
    public string ConnectionStringTemplate { get; set; } =
        "Server={server};Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Application Name=BcTelemetryProxy;Connect Timeout=10";

    /// <summary>Requests blocked at least this long are logged once (0 = off).</summary>
    public int BlockingThresholdSeconds { get; set; } = 10;

    /// <summary>Report deadlocks from the <c>system_health</c> session (independent of BC's EnableDeadlockMonitoring).</summary>
    public bool Deadlocks { get; set; } = true;

    public int QueryTimeoutSeconds { get; set; } = 15;
}

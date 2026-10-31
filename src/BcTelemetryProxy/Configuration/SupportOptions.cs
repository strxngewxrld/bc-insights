namespace BcTelemetryProxy.Configuration;

/// <summary>Error overview and support reports (<c>/support/errors</c>).</summary>
public sealed class SupportOptions
{
    public const string SectionName = "Support";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Serve <c>/support/*</c> to local callers only (reports contain error messages,
    /// SQL statements and stack traces). Set to false only behind access control.
    /// </summary>
    public bool LocalOnly { get; set; } = true;

    /// <summary>Distinct error kinds kept in memory; the least recently seen is dropped first.</summary>
    public int MaxErrorKinds { get; set; } = 500;

    /// <summary>Latest occurrences kept per error kind.</summary>
    public int SamplesPerError { get; set; } = 3;

    /// <summary>Bound on compact span samples used by the 24-hour performance overview.</summary>
    public int MaxPerformanceSamples { get; set; } = 20000;

    /// <summary>
    /// Emit a <c>bc.error.first_seen</c> log the first time an error kind occurs
    /// (since the proxy started), e.g. for an alert that notifies the partner.
    /// </summary>
    public bool LogNewErrors { get; set; } = true;

    /// <summary>
    /// Optional link to a trace in the backend for reports, <c>{traceId}</c> is replaced,
    /// e.g. the trace URL of GitLab Observability.
    /// </summary>
    public string? TraceUrlTemplate { get; set; }
}

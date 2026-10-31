namespace BcTelemetryProxy.Configuration;

/// <summary>How Business Central signals are shaped for the backend.</summary>
public sealed class MappingOptions
{
    public const string SectionName = "Mapping";

    /// <summary>
    /// BC on-premises reports operations (sign-in, SQL, AL, web service calls,
    /// reports, ...) as log events with a duration but sends no request spans.
    /// When enabled, every such event also becomes a span, so APM views (latency,
    /// throughput, error rate per operation) work, and the log links to that span.
    /// </summary>
    public bool DeriveSpans { get; set; } = true;

    /// <summary>
    /// All events of one BC operation (<c>ai.operation.id</c>: one client action,
    /// web service call or background task) share one trace id, so a trace shows
    /// everything the action caused (sign-in, AL, SQL, errors) and every log of it.
    /// </summary>
    public bool CorrelateOperations { get; set; } = true;

    /// <summary>Custom dimensions holding the duration, first match wins.</summary>
    /// <remarks>"hh:mm:ss.fffffff" values or, for keys ending in <c>InMs</c>, milliseconds.</remarks>
    public List<string> DurationDimensions { get; set; } = new() { "totalTime", "executionTime", "serverExecutionTime", "alJobQueueExecutionTimeInMs" };

    /// <summary>
    /// Event ids exported as client spans to the database (long running SQL, lock
    /// timeout, deadlock), with <c>db.*</c>/<c>server.address</c> of the instance's database.
    /// </summary>
    public List<string> DatabaseEvents { get; set; } = new() { "RT0005", "RT0012", "RT0028" };

    /// <summary>Event ids exported as client spans to an external endpoint.</summary>
    public List<string> OutgoingEvents { get; set; } = new() { "RT0019" };

    /// <summary>
    /// Event ids that are errors whatever their severity, with their exception type
    /// (BC logs deadlocks as Information).
    /// </summary>
    public Dictionary<string, string> ErrorEvents { get; set; } = new()
    {
        ["RT0012"] = "DatabaseLockTimeout",
        ["RT0028"] = "DatabaseDeadlock",
    };

    /// <summary><c>service.name</c> of the database tier (<c>peer.service</c> of database spans, SQL monitoring).</summary>
    public string DatabaseServiceName { get; set; } = "sql-server";
}

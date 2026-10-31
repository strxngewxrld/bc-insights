namespace BcTelemetryProxy.Configuration;

/// <summary>
/// Where and how the proxy exports OTLP/HTTP (JSON). The endpoint is the base URL
/// of the backend; <c>/v1/logs</c>, <c>/v1/traces</c> and <c>/v1/metrics</c> are
/// appended per signal. Works with GitLab Observability directly or with any
/// OTLP receiver (e.g. a Collector).
/// </summary>
public sealed class OtlpOptions
{
    public const string SectionName = "Otlp";

    public string Endpoint { get; set; } = "http://localhost:4318";

    /// <summary>Extra request headers, e.g. an auth token if the backend requires one.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gzip-compress export requests.</summary>
    public bool Compression { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Items buffered in memory awaiting export. When full, ingestion answers 503
    /// so Business Central keeps and retries the batch instead of losing it.
    /// </summary>
    public int MaxQueueSize { get; set; } = 100_000;

    /// <summary>Upper bound of items per export request.</summary>
    public int MaxBatchSize { get; set; } = 2_000;

    /// <summary>How long to wait for more items before sending a partial batch.</summary>
    public int BatchDelayMilliseconds { get; set; } = 2_000;

    /// <summary>Total time a failing batch is retried before it is dropped.</summary>
    public int MaxRetrySeconds { get; set; } = 300;
}

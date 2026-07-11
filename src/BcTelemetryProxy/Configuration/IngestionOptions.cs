namespace BcTelemetryProxy.Configuration;

/// <summary>Size and count limits protecting the ingestion endpoint.</summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Maximum accepted compressed (on-the-wire) request body size.</summary>
    public long MaxCompressedRequestBytes { get; set; } = 10 * 1024 * 1024; // 10 MiB

    /// <summary>Maximum accepted total decompressed payload size.</summary>
    public long MaxDecompressedRequestBytes { get; set; } = 100 * 1024 * 1024; // 100 MiB

    /// <summary>Maximum size of a single NDJSON envelope line.</summary>
    public int MaxEnvelopeBytes { get; set; } = 1 * 1024 * 1024; // 1 MiB

    /// <summary>Maximum number of envelopes accepted in a single request.</summary>
    public int MaxEnvelopesPerRequest { get; set; } = 10_000;
}

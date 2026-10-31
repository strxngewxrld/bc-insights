namespace BcTelemetryProxy.Configuration;

/// <summary>
/// Troubleshooting capture of raw ingested envelopes and exported OTLP payloads.
/// Off unless <see cref="CaptureDirectory"/> is set. The capture is written
/// before privacy filtering, so it may contain personal data: enable it only
/// temporarily and protect the directory.
/// </summary>
public sealed class DebugOptions
{
    public const string SectionName = "Debug";

    /// <summary>Directory for <c>capture-yyyyMMdd.ndjson</c> files; null/empty disables the capture.</summary>
    public string? CaptureDirectory { get; set; }

    /// <summary>Total bytes written per process lifetime before the capture stops itself.</summary>
    public long MaxCaptureBytes { get; set; } = 100 * 1024 * 1024; // 100 MiB

    /// <summary>Also capture the uncompressed OTLP JSON sent to the backend.</summary>
    public bool CaptureExport { get; set; } = true;

    public bool Enabled => !string.IsNullOrWhiteSpace(CaptureDirectory);
}

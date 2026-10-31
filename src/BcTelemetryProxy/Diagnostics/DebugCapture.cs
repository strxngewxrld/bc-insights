using System.Text.Json;
using BcTelemetryProxy.Configuration;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.Diagnostics;

/// <summary>
/// Appends raw ingested envelopes and exported OTLP payloads to a daily NDJSON
/// file (<c>capture-yyyyMMdd.ndjson</c>) so the mapping can be checked against
/// real Business Central data. Each line is one record:
/// <c>{"time","kind":"ingest","source","status","envelope"|"raw"}</c> or
/// <c>{"time","kind":"export","signal","body"}</c>. Capture failures never
/// affect ingestion or export.
/// </summary>
public sealed class DebugCapture
{
    private readonly DebugOptions _options;
    private readonly ILogger<DebugCapture> _logger;
    private readonly object _gate = new();
    private long _written;
    private bool _stopped;

    public DebugCapture(IOptions<DebugOptions> options, ILogger<DebugCapture> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (_options.Enabled)
        {
            _logger.LogWarning(
                "Debug capture is ON: raw, unfiltered telemetry is written to {Directory} (limit {Limit} bytes)",
                _options.CaptureDirectory, _options.MaxCaptureBytes);
        }
    }

    public bool Enabled => _options.Enabled && !_stopped;

    public bool ExportEnabled => Enabled && _options.CaptureExport;

    public void CaptureEnvelope(string source, string status, string? raw)
    {
        if (!Enabled)
        {
            return;
        }

        Write(w =>
        {
            w.WriteString("kind", "ingest");
            w.WriteString("source", source);
            w.WriteString("status", status);
            if (raw is null)
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                w.WritePropertyName("envelope");
                doc.WriteTo(w);
            }
            catch (JsonException)
            {
                w.WriteString("raw", raw);
            }
        });
    }

    public void CaptureExport(string signal, Action<Utf8JsonWriter> body)
    {
        if (!ExportEnabled)
        {
            return;
        }

        Write(w =>
        {
            w.WriteString("kind", "export");
            w.WriteString("signal", signal);
            w.WritePropertyName("body");
            body(w);
        });
    }

    private void Write(Action<Utf8JsonWriter> content)
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, Export.OtlpJson.WriterOptions))
            {
                writer.WriteStartObject();
                writer.WriteString("time", DateTime.UtcNow);
                content(writer);
                writer.WriteEndObject();
            }

            buffer.WriteByte((byte)'\n');

            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                if (_written + buffer.Length > _options.MaxCaptureBytes)
                {
                    _stopped = true;
                    _logger.LogWarning("Debug capture stopped after {Bytes} bytes (Debug:MaxCaptureBytes)", _written);
                    return;
                }

                Directory.CreateDirectory(_options.CaptureDirectory!);
                var path = Path.Combine(_options.CaptureDirectory!, $"capture-{DateTime.UtcNow:yyyyMMdd}.ndjson");
                using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                buffer.Position = 0;
                buffer.CopyTo(file);
                _written += buffer.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Debug capture write failed");
        }
    }
}

using System.IO.Compression;
using System.Text.Json;
using BcTelemetryProxy.Model;

namespace BcTelemetryProxy.Ingestion;

/// <summary>
/// Reads an Application Insights ingestion request body. The body is
/// newline-delimited JSON (NDJSON), one <see cref="Envelope"/> per line, and is
/// usually gzip-compressed (Content-Encoding: gzip). Business Central also uses
/// <c>application/x-json-stream</c> as the content type.
/// </summary>
public static class EnvelopeReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Reads and parses all envelopes from the request stream. Malformed lines
    /// are skipped and reported via <paramref name="malformedLines"/> so the
    /// caller can build an Application-Insights-style partial-success response.
    /// </summary>
    public static async Task<IReadOnlyList<Envelope>> ReadAsync(
        Stream body,
        bool gzip,
        CancellationToken cancellationToken = default)
    {
        var stream = gzip ? new GZipStream(body, CompressionMode.Decompress) : body;
        var envelopes = new List<Envelope>();

        try
        {
            using var reader = new StreamReader(stream, leaveOpen: !gzip);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                Envelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<Envelope>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    // Skip a single malformed line rather than failing the batch.
                    continue;
                }

                if (envelope is not null)
                {
                    envelopes.Add(envelope);
                }
            }
        }
        finally
        {
            if (gzip)
            {
                await stream.DisposeAsync();
            }
        }

        return envelopes;
    }
}

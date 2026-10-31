using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Model;

namespace BcTelemetryProxy.Ingestion;

/// <summary>Overall outcome of reading a request body.</summary>
public enum ReadOutcome
{
    Ok,
    InvalidGzip,
    CompressedLimitExceeded,
    DecompressedLimitExceeded,
    TooManyEnvelopes,
}

/// <summary>Per-envelope parse status.</summary>
public enum EnvelopeItemStatus
{
    Parsed,
    Malformed,
    TooLarge,
}

public sealed record EnvelopeReadItem(int Index, Envelope? Envelope, EnvelopeItemStatus Status)
{
    /// <summary>The raw NDJSON line; only kept when the caller asks for it (debug capture).</summary>
    public string? Raw { get; init; }
}

public sealed record EnvelopeReadResult(ReadOutcome Outcome, IReadOnlyList<EnvelopeReadItem> Items);

/// <summary>
/// Reads an Application Insights ingestion request body (NDJSON, one
/// <see cref="Envelope"/> per line, usually gzip-compressed) while enforcing the
/// configured size and count limits. Never throws on malformed content: each
/// line is reported with its status so the caller can build a partial-success
/// response, and request-level limit breaches are returned as an
/// <see cref="ReadOutcome"/> rather than an exception.
/// </summary>
public static class EnvelopeReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<EnvelopeReadResult> ReadAsync(
        Stream body,
        bool gzip,
        IngestionOptions options,
        CancellationToken cancellationToken = default,
        bool keepRaw = false)
    {
        var items = new List<EnvelopeReadItem>();

        var compressedLimited = new LimitingStream(body, options.MaxCompressedRequestBytes);
        Stream payload = gzip ? new GZipStream(compressedLimited, CompressionMode.Decompress) : compressedLimited;
        var decompressedLimited = new LimitingStream(payload, options.MaxDecompressedRequestBytes);

        try
        {
            using var reader = new StreamReader(decompressedLimited, Encoding.UTF8);
            var index = 0;
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (items.Count >= options.MaxEnvelopesPerRequest)
                {
                    return new EnvelopeReadResult(ReadOutcome.TooManyEnvelopes, items);
                }

                if (Encoding.UTF8.GetByteCount(line) > options.MaxEnvelopeBytes)
                {
                    items.Add(new EnvelopeReadItem(index++, null, EnvelopeItemStatus.TooLarge)
                    {
                        Raw = keepRaw ? line[..Math.Min(line.Length, 4096)] : null,
                    });
                    continue;
                }

                var item = ParseLine(line, index++);
                items.Add(keepRaw ? item with { Raw = line } : item);
            }
        }
        catch (StreamLimitExceededException ex)
        {
            var outcome = ex.Limit == options.MaxCompressedRequestBytes
                ? ReadOutcome.CompressedLimitExceeded
                : ReadOutcome.DecompressedLimitExceeded;
            return new EnvelopeReadResult(outcome, items);
        }
        catch (InvalidDataException)
        {
            // Corrupt gzip stream.
            return new EnvelopeReadResult(ReadOutcome.InvalidGzip, items);
        }
        finally
        {
            if (gzip)
            {
                await payload.DisposeAsync();
            }
        }

        return new EnvelopeReadResult(ReadOutcome.Ok, items);
    }

    private static EnvelopeReadItem ParseLine(string line, int index)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(line, JsonOptions);
            return envelope is not null
                ? new EnvelopeReadItem(index, envelope, EnvelopeItemStatus.Parsed)
                : new EnvelopeReadItem(index, null, EnvelopeItemStatus.Malformed);
        }
        catch (JsonException)
        {
            return new EnvelopeReadItem(index, null, EnvelopeItemStatus.Malformed);
        }
    }
}

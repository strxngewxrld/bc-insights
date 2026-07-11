using System.IO.Compression;
using System.Text;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Ingestion;

namespace BcTelemetryProxy.Tests;

public class EnvelopeReaderTests
{
    private const string Line1 = """{"name":"a","data":{"baseType":"MessageData","baseData":{"message":"m1"}}}""";
    private const string Line2 = """{"name":"b","data":{"baseType":"EventData","baseData":{"name":"e1"}}}""";

    private static IngestionOptions Options(Action<IngestionOptions>? configure = null)
    {
        var options = new IngestionOptions();
        configure?.Invoke(options);
        return options;
    }

    [Fact]
    public async Task Reads_valid_plain_ndjson()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Line1 + "\n" + Line2));
        var result = await EnvelopeReader.ReadAsync(stream, gzip: false, Options());

        Assert.Equal(ReadOutcome.Ok, result.Outcome);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, i => Assert.Equal(EnvelopeItemStatus.Parsed, i.Status));
    }

    [Fact]
    public async Task Reads_gzip_ndjson()
    {
        using var stream = Gzip(Line1 + "\n" + Line2);
        var result = await EnvelopeReader.ReadAsync(stream, gzip: true, Options());

        Assert.Equal(ReadOutcome.Ok, result.Outcome);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task Invalid_gzip_is_reported()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not gzip at all"));
        var result = await EnvelopeReader.ReadAsync(stream, gzip: true, Options());

        Assert.Equal(ReadOutcome.InvalidGzip, result.Outcome);
    }

    [Fact]
    public async Task Malformed_line_is_marked_but_batch_continues()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Line1 + "\nnot-json\n" + Line2));
        var result = await EnvelopeReader.ReadAsync(stream, gzip: false, Options());

        Assert.Equal(ReadOutcome.Ok, result.Outcome);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(EnvelopeItemStatus.Parsed, result.Items[0].Status);
        Assert.Equal(EnvelopeItemStatus.Malformed, result.Items[1].Status);
        Assert.Equal(EnvelopeItemStatus.Parsed, result.Items[2].Status);
    }

    [Fact]
    public async Task Decompressed_limit_is_enforced()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Line1 + "\n" + Line2));
        var result = await EnvelopeReader.ReadAsync(stream, gzip: false, Options(o => o.MaxDecompressedRequestBytes = 10));

        Assert.Equal(ReadOutcome.DecompressedLimitExceeded, result.Outcome);
    }

    [Fact]
    public async Task Too_many_envelopes_is_enforced()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Line1 + "\n" + Line2));
        var result = await EnvelopeReader.ReadAsync(stream, gzip: false, Options(o => o.MaxEnvelopesPerRequest = 1));

        Assert.Equal(ReadOutcome.TooManyEnvelopes, result.Outcome);
    }

    [Fact]
    public async Task Oversized_envelope_is_marked_too_large()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Line1));
        var result = await EnvelopeReader.ReadAsync(stream, gzip: false, Options(o => o.MaxEnvelopeBytes = 5));

        Assert.Equal(ReadOutcome.Ok, result.Outcome);
        Assert.Equal(EnvelopeItemStatus.TooLarge, Assert.Single(result.Items).Status);
    }

    internal static MemoryStream Gzip(string text)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }

        output.Position = 0;
        return output;
    }
}

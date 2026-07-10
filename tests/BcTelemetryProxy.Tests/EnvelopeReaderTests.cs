using System.IO.Compression;
using System.Text;
using BcTelemetryProxy.Ingestion;

namespace BcTelemetryProxy.Tests;

public class EnvelopeReaderTests
{
    [Fact]
    public async Task Reads_gzip_ndjson_and_skips_blank_and_malformed_lines()
    {
        var ndjson = string.Join("\n",
            """{"name":"a","data":{"baseType":"MessageData","baseData":{"message":"m1"}}}""",
            "",
            "this-is-not-json",
            """{"name":"b","data":{"baseType":"EventData","baseData":{"name":"e1"}}}""");

        using var compressed = Gzip(ndjson);
        var envelopes = await EnvelopeReader.ReadAsync(compressed, gzip: true);

        Assert.Equal(2, envelopes.Count);
        Assert.Equal("a", envelopes[0].Name);
        Assert.Equal("b", envelopes[1].Name);
    }

    [Fact]
    public async Task Reads_plain_ndjson_when_not_gzipped()
    {
        var ndjson = """{"name":"a","data":{"baseType":"MessageData","baseData":{"message":"m1"}}}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ndjson));

        var envelopes = await EnvelopeReader.ReadAsync(stream, gzip: false);

        Assert.Single(envelopes);
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

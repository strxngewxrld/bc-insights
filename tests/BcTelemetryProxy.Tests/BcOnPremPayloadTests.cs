using System.Net;
using System.Text.Json;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Diagnostics;
using BcTelemetryProxy.Ingestion;
using BcTelemetryProxy.Mapping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.Tests;

/// <summary>
/// Regression tests built from a real (sanitised) envelope sent by a BC 29
/// on-premises server, see samples/bc-onprem-rt0004.ndjson.
/// </summary>
public class BcOnPremPayloadTests
{
    private static readonly string RealEnvelope = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "samples", "bc-onprem-rt0004.ndjson")).Trim();

    private readonly RecordingEmitter _emitter = new();

    private RecordingEmitter.LogEntry MapReal()
    {
        TestFactory.Mapper(_emitter).Map(TestFactory.ParseEnvelope(RealEnvelope));
        return Assert.Single(_emitter.Logs);
    }

    [Fact]
    public void Placeholders_are_not_exported()
    {
        var log = MapReal();

        Assert.DoesNotContain(log.Attributes, a => a.Value is "N/A" or "NaN");
        Assert.False(log.Attributes.ContainsKey("parentSessionId"));
    }

    [Fact]
    public void Dimensions_are_renamed_typed_and_not_duplicated()
    {
        var a = MapReal().Attributes;

        Assert.Equal("CRONUS AG", a["bc.company.name"]);
        Assert.Equal("WebClient", a["bc.client_type"]);
        Assert.Equal("29.0.55365.0", a["bc.version"]);
        Assert.Equal(9L, a["sqlExecutes"]);
        Assert.Equal(false, a["guestUser"]);
        Assert.Equal(22.6688, (double)a["serverExecutionTimeInMs"]!, 4);
        Assert.False(a.ContainsKey("serverExecutionTime"));
        Assert.False(a.ContainsKey("companyName"));
        Assert.False(a.ContainsKey("eventId"));
    }

    [Fact]
    public void Shared_operation_id_does_not_create_a_trace_for_logs()
    {
        var log = MapReal();

        Assert.Equal("RT0004", log.EventName);
        Assert.Equal(LogLevel.Information, log.Level);
        Assert.Null(log.Item.TraceId);
        Assert.Equal("4205630c-2e82-35e4-17bc-f3ce73b76ad1", log.Attributes["bc.operation.id"]);
    }

    [Theory]
    [InlineData("\"Verbose\"", LogLevel.Debug)]
    [InlineData("\"Warning\"", LogLevel.Warning)]
    [InlineData("\"Error\"", LogLevel.Error)]
    [InlineData("\"Critical\"", LogLevel.Critical)]
    [InlineData("2", LogLevel.Warning)]
    [InlineData("\"unknown\"", LogLevel.Information)]
    public void Severity_names_and_numbers_are_mapped(string severity, LogLevel expected)
    {
        TestFactory.Mapper(_emitter).Map(TestFactory.ParseEnvelope(
            """{"data":{"baseType":"MessageData","baseData":{"message":"m","severityLevel":""" + severity + "}}}"));

        Assert.Equal(expected, Assert.Single(_emitter.Logs).Level);
    }

    [Fact]
    public void Guid_operation_id_becomes_its_hex_trace_id()
        => Assert.Equal("4205630c2e8235e417bcf3ce73b76ad1",
            TraceContextMapper.ResolveTraceId("4205630c-2e82-35e4-17bc-f3ce73b76ad1").ToHexString());

    [Fact]
    public void Host_falls_back_to_node_name_when_role_instance_is_a_placeholder()
    {
        var resolver = new SourceResolver(Options.Create(new ResourceOptions()));

        var source = resolver.Resolve(null, null, TestFactory.ParseEnvelope(RealEnvelope), IPAddress.Parse("10.0.0.5"));

        Assert.Equal("SRV01", source.HostName);
    }
}

public class ExportStatisticsTests
{
    [Fact]
    public void Degraded_after_an_error_until_the_next_success()
    {
        var stats = new BcTelemetryProxy.Export.ExportStatistics();
        Assert.False(stats.IsDegraded);

        stats.RecordSuccess("logs", 1);
        Thread.Sleep(5);
        stats.RecordError("logs: down");
        Assert.True(stats.IsDegraded);

        Thread.Sleep(5);
        stats.RecordSuccess("logs", 1);
        Assert.False(stats.IsDegraded);
        Assert.Equal("logs: down", stats.LastError);
    }
}

public class DebugCaptureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bcproxy-capture-" + Guid.NewGuid().ToString("N"));

    private DebugCapture Capture(long maxBytes = 1024 * 1024) => new(
        Options.Create(new DebugOptions { CaptureDirectory = _dir, MaxCaptureBytes = maxBytes }),
        NullLogger<DebugCapture>.Instance);

    [Fact]
    public void Disabled_by_default()
    {
        var capture = new DebugCapture(Options.Create(new DebugOptions()), NullLogger<DebugCapture>.Instance);

        capture.CaptureEnvelope("SRV01/BC290", "Mapped", "{}");

        Assert.False(capture.Enabled);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Writes_envelopes_raw_lines_and_exports_as_ndjson()
    {
        var capture = Capture();

        capture.CaptureEnvelope("SRV01/BC290", "Mapped", """{"name":"x"}""");
        capture.CaptureEnvelope("SRV01/BC290", "Malformed", "{not json");
        capture.CaptureExport("logs", w => { w.WriteStartObject(); w.WriteString("k", "v"); w.WriteEndObject(); });

        var lines = File.ReadAllLines(Directory.GetFiles(_dir).Single()).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal("x", lines[0].GetProperty("envelope").GetProperty("name").GetString());
        Assert.Equal("{not json", lines[1].GetProperty("raw").GetString());
        Assert.Equal("logs", lines[2].GetProperty("signal").GetString());
        Assert.Equal("v", lines[2].GetProperty("body").GetProperty("k").GetString());
    }

    [Fact]
    public void Stops_at_the_size_limit()
    {
        var capture = Capture(maxBytes: 300);

        for (var i = 0; i < 10; i++)
        {
            capture.CaptureEnvelope("SRV01/BC290", "Mapped", """{"name":"0123456789"}""");
        }

        Assert.False(capture.Enabled);
        Assert.True(new FileInfo(Directory.GetFiles(_dir).Single()).Length <= 300);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

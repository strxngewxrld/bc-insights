using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Export;
using BcTelemetryProxy.Ingestion;
using BcTelemetryProxy.Mapping;
using BcTelemetryProxy.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.Tests;

public class SourceResolverTests
{
    private static readonly Envelope TaggedEnvelope =
        TestFactory.ParseEnvelope("""{"tags":{"ai.cloud.roleInstance":"TAG-HOST"}}""");

    private static SourceResolver Resolver(ResourceOptions? options = null) => new(Options.Create(options ?? new ResourceOptions()));

    [Fact]
    public void Url_segments_win_over_envelope_tags()
    {
        var source = Resolver().Resolve("SRV01", "BC290", TaggedEnvelope, IPAddress.Loopback);

        Assert.Equal("SRV01", source.HostName);
        Assert.Equal("BC290", source.ServerInstance);
        Assert.Equal("SRV01/BC290", source.ServiceInstanceId);
    }

    [Fact]
    public void Falls_back_to_tag_then_configuration_then_caller()
    {
        var options = new ResourceOptions { BcServerInstance = "BC290" };
        Assert.Equal("TAG-HOST", Resolver(options).Resolve(null, null, TaggedEnvelope, null).HostName);

        var empty = TestFactory.ParseEnvelope("{}");
        var source = Resolver(options).Resolve(null, null, empty, IPAddress.Parse("10.1.2.3"));
        Assert.Equal("10.1.2.3", source.HostName);
        Assert.Equal("BC290", source.ServerInstance);
    }

    [Fact]
    public void Most_specific_override_applies()
    {
        var options = new ResourceOptions
        {
            DeploymentEnvironment = "production",
            Sources =
            {
                ["BC290"] = new SourceOverride { DeploymentEnvironment = "test" },
                ["SRV02/BC290"] = new SourceOverride { DeploymentEnvironment = "staging", ServiceName = "bc-staging" },
            },
        };
        var resolver = Resolver(options);
        var empty = TestFactory.ParseEnvelope("{}");

        Assert.Equal("test", resolver.Resolve("SRV01", "BC290", empty, null).DeploymentEnvironment);
        var staging = resolver.Resolve("SRV02", "BC290", empty, null);
        Assert.Equal("staging", staging.DeploymentEnvironment);
        Assert.Equal("bc-staging", staging.ServiceName);
        Assert.Equal("production", resolver.Resolve("SRV01", "BC250", empty, null).DeploymentEnvironment);
    }

    [Fact]
    public void Empty_configured_values_count_as_unset()
    {
        // appsettings "HostName": null binds as "".
        var resolver = Resolver(new ResourceOptions { HostName = "", BcServerInstance = "" });

        var source = resolver.Resolve(null, null, TestFactory.ParseEnvelope("{}"), IPAddress.Parse("10.1.2.3"));

        Assert.Equal("10.1.2.3", source.HostName);
        Assert.Null(source.ServerInstance);
        Assert.Equal(Environment.MachineName, resolver.Local("BC290").HostName);
    }

    [Theory]
    [InlineData("SRV-01.corp_x", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("<script>", false)]
    public void Validates_url_segments(string value, bool valid)
        => Assert.Equal(valid, SourceResolver.IsValidSegment(value));
}

public class MapperEnrichmentTests
{
    private readonly RecordingEmitter _emitter = new();

    [Fact]
    public void Log_keeps_bc_timestamp_trace_context_and_source()
    {
        var source = new TelemetrySource("business-central", "ns", "SRV01", "BC290", "test");
        TestFactory.Mapper(_emitter).Map(TestFactory.ParseEnvelope("""
        {"time":"2026-07-10T08:15:30.5Z","tags":{"ai.operation.id":"0123456789abcdef0123456789abcdef","ai.operation.parentId":"abcdef0123456789"},
         "data":{"baseType":"MessageData","baseData":{"message":"m","severityLevel":1,"properties":{"eventId":"RT0005"}}}}
        """), source);

        var log = Assert.Single(_emitter.Logs).Item;
        Assert.Equal(new DateTime(2026, 7, 10, 8, 15, 30, 500, DateTimeKind.Utc), log.TimeUtc);
        Assert.Equal("0123456789abcdef0123456789abcdef", log.TraceId?.ToHexString());
        Assert.Equal("abcdef0123456789", log.SpanId?.ToHexString());
        Assert.Same(source, log.Source);
    }

    [Fact]
    public void Log_without_operation_id_has_no_trace_context()
    {
        TestFactory.Mapper(_emitter).Map(TestFactory.ParseEnvelope("""
        {"data":{"baseType":"MessageData","baseData":{"message":"m"}}}
        """));

        var log = Assert.Single(_emitter.Logs).Item;
        Assert.Null(log.TraceId);
        Assert.Null(log.SpanId);
    }

    [Fact]
    public void Durations_get_numeric_companions_and_counters_are_numbers()
    {
        TestFactory.Mapper(_emitter).Map(TestFactory.ParseEnvelope("""
        {"data":{"baseType":"MessageData","baseData":{"message":"m","properties":{
          "executionTime":"00:00:01.2500000","serverExecutionTime":"00:00:00.5000000","serverExecutionTimeInMs":"500",
          "sqlExecutes":"12","alObjectId":"22"}}}}
        """));

        var a = Assert.Single(_emitter.Logs).Attributes;
        Assert.False(a.ContainsKey("executionTime"));
        Assert.False(a.ContainsKey("serverExecutionTime"));
        Assert.Equal(1250d, a["executionTimeInMs"]);
        Assert.Equal(500L, a["serverExecutionTimeInMs"]);
        Assert.Equal(12L, a["sqlExecutes"]);
        Assert.Equal("22", a["alObjectId"]);
    }

    [Fact]
    public void Request_id_becomes_span_id()
    {
        TestFactory.Mapper(_emitter).Map(TestFactory.ParseEnvelope("""
        {"data":{"baseType":"RequestData","baseData":{"id":"1122334455667788","name":"r","success":true}}}
        """));

        Assert.Equal("1122334455667788", Assert.Single(_emitter.Spans).Item.SpanId.ToHexString());
    }
}

public class OtlpJsonSerializerTests
{
    private static readonly TelemetrySource Srv1 = new("business-central", "ns", "SRV01", "BC290", "test");
    private static readonly TelemetrySource Srv2 = new("business-central", "ns", "SRV02", "BC290", "test");

    private static JsonDocument Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
        }

        return JsonDocument.Parse(stream.ToArray());
    }

    private static LogItem Log(TelemetrySource source, string body) => new(
        source,
        new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc),
        LogLevel.Warning,
        body,
        "RT0005",
        ActivityTraceId.CreateFromString("0123456789abcdef0123456789abcdef"),
        null,
        new KeyValuePair<string, object?>[] { new("sqlExecutes", 3L), new("companyName", "CRONUS"), new("ms", 1.5) });

    [Fact]
    public void Logs_are_grouped_per_source_with_otlp_json_encoding()
    {
        using var doc = Write(w => OtlpJsonSerializer.WriteLogs(w, new[] { Log(Srv1, "a"), Log(Srv2, "b"), Log(Srv1, "c") }, DateTime.UtcNow));

        var resources = doc.RootElement.GetProperty("resourceLogs");
        Assert.Equal(2, resources.GetArrayLength());

        var first = resources[0];
        var resourceAttributes = Attributes(first.GetProperty("resource"));
        Assert.Equal("SRV01/BC290", resourceAttributes["service.instance.id"].GetProperty("stringValue").GetString());
        Assert.Equal("test", resourceAttributes["deployment.environment"].GetProperty("stringValue").GetString());

        var records = first.GetProperty("scopeLogs")[0].GetProperty("logRecords");
        Assert.Equal(2, records.GetArrayLength());
        var record = records[0];
        Assert.Equal("1783670400000000000", record.GetProperty("timeUnixNano").GetString());
        Assert.Equal(13, record.GetProperty("severityNumber").GetInt32());
        Assert.Equal("0123456789abcdef0123456789abcdef", record.GetProperty("traceId").GetString());
        Assert.False(record.TryGetProperty("spanId", out _));

        var attributes = Attributes(record);
        Assert.Equal("RT0005", attributes["event.name"].GetProperty("stringValue").GetString());
        Assert.Equal("3", attributes["sqlExecutes"].GetProperty("intValue").GetString());
        Assert.Equal(1.5, attributes["ms"].GetProperty("doubleValue").GetDouble());
    }

    [Fact]
    public void Failed_span_has_error_status_and_parent()
    {
        var span = new SpanItem(Srv1, "SELECT", ActivityKind.Client, new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMilliseconds(5), ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(),
            ActivitySpanId.CreateFromString("abcdef0123456789"), Success: false, "1", Array.Empty<KeyValuePair<string, object?>>());

        using var doc = Write(w => OtlpJsonSerializer.WriteSpans(w, new[] { span }));

        var s = doc.RootElement.GetProperty("resourceSpans")[0].GetProperty("scopeSpans")[0].GetProperty("spans")[0];
        Assert.Equal(3, s.GetProperty("kind").GetInt32());
        Assert.Equal("abcdef0123456789", s.GetProperty("parentSpanId").GetString());
        Assert.Equal("1783670400005000000", s.GetProperty("endTimeUnixNano").GetString());
        Assert.Equal(2, s.GetProperty("status").GetProperty("code").GetInt32());
    }

    [Fact]
    public void Span_events_and_service_version_are_serialized()
    {
        var source = Srv1 with { ServiceVersion = "29.0.55365.0" };
        var at = new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc);
        var span = new SpanItem(source, "Error dialog shown: MetadataNotFound", ActivityKind.Internal, at, TimeSpan.Zero,
            ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), null, Success: false, "MetadataNotFound",
            Array.Empty<KeyValuePair<string, object?>>(),
            new[] { new SpanEvent("exception", at, new KeyValuePair<string, object?>[] { new("exception.type", "MetadataNotFound") }) });

        using var doc = Write(w => OtlpJsonSerializer.WriteSpans(w, new[] { span }));

        var resource = doc.RootElement.GetProperty("resourceSpans")[0];
        Assert.Equal("29.0.55365.0", Attributes(resource.GetProperty("resource"))["service.version"].GetProperty("stringValue").GetString());
        var e = resource.GetProperty("scopeSpans")[0].GetProperty("spans")[0].GetProperty("events")[0];
        Assert.Equal("exception", e.GetProperty("name").GetString());
        Assert.Equal("1783670400000000000", e.GetProperty("timeUnixNano").GetString());
        Assert.Equal("MetadataNotFound", Attributes(e)["exception.type"].GetProperty("stringValue").GetString());
    }

    [Fact]
    public void Cumulative_counters_are_sums_and_gauges_keep_their_unit()
    {
        var at = new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc);
        var sum = new MetricItem(Srv1, at, new AiMetricPoint("system.network.io", 1000, null, null, null, null), MetricKind.Counter,
            new KeyValuePair<string, object?>[] { new("network.io.direction", "receive") }, "By", at.AddHours(-1));
        var gauge = new MetricItem(Srv1, at, new AiMetricPoint("system.cpu.utilization", 0.25, null, null, null, null), MetricKind.Gauge,
            Array.Empty<KeyValuePair<string, object?>>(), "1");

        using var doc = Write(w => OtlpJsonSerializer.WriteMetrics(w, new[] { sum, gauge }));

        var metrics = doc.RootElement.GetProperty("resourceMetrics")[0].GetProperty("scopeMetrics")[0].GetProperty("metrics");
        var network = metrics[0];
        Assert.Equal("By", network.GetProperty("unit").GetString());
        Assert.Equal(2, network.GetProperty("sum").GetProperty("aggregationTemporality").GetInt32());
        Assert.True(network.GetProperty("sum").GetProperty("isMonotonic").GetBoolean());
        Assert.Equal("1783666800000000000", network.GetProperty("sum").GetProperty("dataPoints")[0].GetProperty("startTimeUnixNano").GetString());
        Assert.Equal(0.25, metrics[1].GetProperty("gauge").GetProperty("dataPoints")[0].GetProperty("asDouble").GetDouble());
    }

    [Fact]
    public void Up_down_counters_are_non_monotonic_cumulative_sums()
    {
        var at = new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc);
        var memory = new MetricItem(Srv1, at, new AiMetricPoint("system.memory.usage", 1024, null, null, null, null), MetricKind.UpDownCounter,
            new KeyValuePair<string, object?>[] { new("state", "used") }, "By", at.AddHours(-1));

        using var doc = Write(w => OtlpJsonSerializer.WriteMetrics(w, new[] { memory }));

        var sum = doc.RootElement.GetProperty("resourceMetrics")[0].GetProperty("scopeMetrics")[0].GetProperty("metrics")[0].GetProperty("sum");
        Assert.Equal(2, sum.GetProperty("aggregationTemporality").GetInt32());
        Assert.False(sum.GetProperty("isMonotonic").GetBoolean());
    }

    [Fact]
    public void Configured_resource_attributes_are_added_without_overriding_standard_ones()
    {
        var resolver = new SourceResolver(Options.Create(new ResourceOptions
        {
            Attributes = { ["gitlab.project.id"] = "42", ["service.name"] = "ignored" },
            Sources = { ["SRV01/BC290"] = new SourceOverride { Attributes = { ["gitlab.project.id"] = "43" } } },
        }));
        var empty = TestFactory.ParseEnvelope("{}");
        var a = resolver.Resolve("SRV01", "BC290", empty, null);
        var b = resolver.Resolve("SRV01", "BC290", empty, null);
        Assert.Equal(a, b);

        using var doc = Write(w => OtlpJsonSerializer.WriteLogs(w, new[] { Log(a, "x"), Log(resolver.Resolve("SRV02", "BC290", empty, null), "y") }, DateTime.UtcNow));

        var resources = doc.RootElement.GetProperty("resourceLogs");
        var first = Attributes(resources[0].GetProperty("resource"));
        Assert.Equal("43", first["gitlab.project.id"].GetProperty("stringValue").GetString());
        Assert.Equal("business-central", first["service.name"].GetProperty("stringValue").GetString());
        Assert.Equal("42", Attributes(resources[1].GetProperty("resource"))["gitlab.project.id"].GetProperty("stringValue").GetString());
    }

    private static Dictionary<string, JsonElement> Attributes(JsonElement owner)
        => owner.GetProperty("attributes").EnumerateArray()
            .ToDictionary(a => a.GetProperty("key").GetString()!, a => a.GetProperty("value"));
}

public class OtlpExporterTests
{
    private static OtlpExporter Exporter(FakeHandler handler, int maxQueue = 1000) => new(
        Options.Create(new OtlpOptions
        {
            Endpoint = "https://o11y.example.com:14318/",
            BatchDelayMilliseconds = 10,
            MaxQueueSize = maxQueue,
            MaxRetrySeconds = 30,
            Headers = { ["X-Test"] = "1" },
        }),
        NullLogger<OtlpExporter>.Instance,
        handler);

    private static LogItem Log() => new(TelemetrySource.Default, DateTime.UtcNow, LogLevel.Information, "hello", null, null, null,
        Array.Empty<KeyValuePair<string, object?>>());

    [Fact]
    public async Task Exports_gzipped_json_to_signal_path_with_headers()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        using var exporter = Exporter(handler);
        await exporter.StartAsync(CancellationToken.None);

        exporter.EmitLog(Log());
        var request = await handler.NextAsync();
        await WaitUntil(() => exporter.Statistics.ExportedLogs == 1);
        await exporter.StopAsync(CancellationToken.None);

        Assert.Equal("https://o11y.example.com:14318/v1/logs", request.Uri);
        Assert.Equal("gzip", request.ContentEncoding);
        Assert.Equal("1", request.TestHeader);
        Assert.Contains("\"hello\"", request.Body);
        Assert.Equal(1, exporter.Statistics.ExportedLogs);
    }

    [Fact]
    public async Task Retries_transient_failures()
    {
        var handler = new FakeHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var exporter = Exporter(handler);
        await exporter.StartAsync(CancellationToken.None);

        exporter.EmitLog(Log());
        await handler.NextAsync();
        await handler.NextAsync();
        await WaitUntil(() => exporter.Statistics.ExportedLogs == 1);
        await exporter.StopAsync(CancellationToken.None);

        Assert.Equal(1, exporter.Statistics.ExportedLogs);
        Assert.Equal(0, exporter.Statistics.Failed);
    }

    [Fact]
    public async Task Permanent_failure_is_counted_and_not_retried()
    {
        var handler = new FakeHandler(HttpStatusCode.BadRequest);
        using var exporter = Exporter(handler);
        await exporter.StartAsync(CancellationToken.None);

        exporter.EmitLog(Log());
        await handler.NextAsync();
        await WaitUntil(() => exporter.Statistics.Failed == 1);
        await exporter.StopAsync(CancellationToken.None);

        Assert.Equal(1, exporter.Statistics.Failed);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Capacity_reflects_queue_bound()
    {
        using var exporter = Exporter(new FakeHandler(HttpStatusCode.OK), maxQueue: 2);
        Assert.True(exporter.HasCapacity(2));
        exporter.EmitLog(Log());
        Assert.False(exporter.HasCapacity(2));
    }

    [Fact]
    public void Full_queue_counts_every_dropped_item()
    {
        using var exporter = Exporter(new FakeHandler(HttpStatusCode.OK), maxQueue: 2);
        for (var i = 0; i < 5; i++) exporter.EmitLog(Log());
        Assert.Equal(2, exporter.QueueLength);
        Assert.Equal(3, exporter.Statistics.Dropped);
    }

    [Fact]
    public async Task Partial_rejection_is_not_counted_as_exported_or_healthy()
    {
        var handler = new FakeHandler(HttpStatusCode.OK)
        {
            ResponseBody = """{"partialSuccess":{"rejectedLogRecords":"1","errorMessage":"invalid attributes"}}""",
        };
        using var exporter = Exporter(handler);
        exporter.EmitLog(Log());
        exporter.EmitLog(Log());
        await exporter.StartAsync(CancellationToken.None);
        await handler.NextAsync();
        await WaitUntil(() => exporter.Statistics.ExportedLogs == 1);
        await exporter.StopAsync(CancellationToken.None);
        Assert.Equal(1, exporter.Statistics.Rejected);
        Assert.Equal(1, exporter.Statistics.ExportedLogs);
        Assert.True(exporter.Statistics.IsDegraded);
        Assert.Null(exporter.Statistics.LastSuccessUtc);
    }

    [Fact]
    public void Metrics_success_cannot_hide_trace_export_failure()
    {
        var stats = new ExportStatistics();
        stats.RecordError("traces: HTTP 400", "traces");
        stats.RecordSuccess("metrics", 10);
        Assert.True(stats.IsDegraded);
        stats.RecordSuccess("traces", 1);
        Assert.False(stats.IsDegraded);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }
    }

    public sealed record CapturedRequest(string Uri, string? ContentEncoding, string? TestHeader, string Body);

    public sealed class FakeHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public string ResponseBody { get; init; } = "{}";
        private readonly BlockingCollection<CapturedRequest> _requests = new();
        private int _calls;

        public int Calls => _calls;

        public Task<CapturedRequest> NextAsync()
            => Task.Run(() => _requests.TryTake(out var r, TimeSpan.FromSeconds(15)) ? r : throw new TimeoutException());

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            var raw = await request.Content!.ReadAsByteArrayAsync(ct);
            var encoding = request.Content.Headers.ContentEncoding.FirstOrDefault();
            var body = encoding == "gzip"
                ? new StreamReader(new GZipStream(new MemoryStream(raw), CompressionMode.Decompress)).ReadToEnd()
                : Encoding.UTF8.GetString(raw);
            request.Headers.TryGetValues("X-Test", out var test);
            _requests.Add(new CapturedRequest(request.RequestUri!.ToString(), encoding, test?.FirstOrDefault(), body), ct);

            var status = statuses[Math.Min(call, statuses.Length) - 1];
            return new HttpResponseMessage(status) { Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json") };
        }
    }
}

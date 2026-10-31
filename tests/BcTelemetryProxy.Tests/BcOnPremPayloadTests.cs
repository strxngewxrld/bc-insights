using System.Net;
using System.Text.Json;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Diagnostics;
using BcTelemetryProxy.HostTelemetry;
using BcTelemetryProxy.Ingestion;
using BcTelemetryProxy.Mapping;
using BcTelemetryProxy.Model;
using BcTelemetryProxy.Support;
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
    public void Logs_carry_the_trace_of_their_operation()
    {
        TestFactory.Mapper(_emitter, mapping: new MappingOptions { DeriveSpans = false })
            .Map(TestFactory.ParseEnvelope(RealEnvelope));

        var log = Assert.Single(_emitter.Logs);
        Assert.Equal("RT0004", log.EventName);
        Assert.Equal(LogLevel.Information, log.Level);
        Assert.Equal("4205630c2e8235e417bcf3ce73b76ad1", log.Item.TraceId?.ToHexString());
        Assert.Null(log.Item.SpanId);
        Assert.Equal("4205630c-2e82-35e4-17bc-f3ce73b76ad1", log.Attributes["bc.operation.id"]);
    }

    [Fact]
    public void Event_with_duration_becomes_a_server_span_linked_to_its_log()
    {
        var log = MapReal();

        var span = Assert.Single(_emitter.Spans);
        Assert.Equal("Authorization Succeeded (Open Company)", span.Name);
        Assert.Equal(System.Diagnostics.ActivityKind.Server, span.Kind);
        Assert.Equal(TimeSpan.Parse("00:00:00.0244672", System.Globalization.CultureInfo.InvariantCulture), span.Duration);
        Assert.Equal(span.Item.StartUtc + span.Duration, log.Item.TimeUtc);
        Assert.True(span.Success);
        Assert.Null(span.ParentSpanId);
        Assert.Equal("RT0004", span.Tags["bc.event.id"]);
        Assert.Equal("CRONUS AG", span.Tags["bc.company.name"]);
        Assert.Equal(span.TraceId, log.Item.TraceId);
        Assert.Equal(span.Item.SpanId, log.Item.SpanId);
    }

    [Fact]
    public void Events_of_one_operation_share_one_trace()
    {
        var mapper = TestFactory.Mapper(_emitter);
        mapper.Map(TestFactory.ParseEnvelope(RealEnvelope));
        mapper.Map(TestFactory.ParseEnvelope(RealEnvelope.Replace("\"eventId\":\"RT0004\"", "\"eventId\":\"RT0018\"")));

        Assert.Equal(2, _emitter.Spans.Count);
        Assert.All(_emitter.Spans, s => Assert.Equal("4205630c2e8235e417bcf3ce73b76ad1", s.TraceId.ToHexString()));
        Assert.NotEqual(_emitter.Spans[0].Item.SpanId, _emitter.Spans[1].Item.SpanId);
    }

    [Fact]
    public void Operation_correlation_can_be_switched_off()
    {
        var mapper = TestFactory.Mapper(_emitter, mapping: new MappingOptions { CorrelateOperations = false });
        mapper.Map(TestFactory.ParseEnvelope(RealEnvelope));
        mapper.Map(TestFactory.ParseEnvelope(RealEnvelope));

        Assert.NotEqual(_emitter.Spans[0].TraceId, _emitter.Spans[1].TraceId);
    }

    [Fact]
    public void Database_events_are_client_spans_to_the_instance_database()
    {
        var source = TelemetrySource.Default with
        {
            Attributes = new Dictionary<string, string> { ["bc.database.server"] = "SQL01\\BC", ["bc.database.name"] = "BC Prod" },
        };

        TestFactory.Mapper(_emitter).Map(TestFactory.ParseEnvelope("""
        {"time":"2026-10-31T08:00:00Z","tags":{"ai.operation.id":"c6727c47-6c2e-c821-fcd9-4cc475d05415"},"data":{"baseType":"MessageData","baseData":{"message":"Operation exceeded time threshold (SQL query)","severityLevel":"Warning",
         "properties":{"eventId":"RT0005","executionTime":"00:00:01.5000000","sqlStatement":"SELECT 1"}}}}
        """), source);

        var span = Assert.Single(_emitter.Spans);
        Assert.Equal(System.Diagnostics.ActivityKind.Client, span.Kind);
        Assert.Equal("SQL01", span.Tags["server.address"]);
        Assert.Equal("SQL01\\BC", span.Tags["db.instance"]);
        Assert.Equal("BC Prod", span.Tags["db.namespace"]);
        Assert.Equal("sql-server", span.Tags["peer.service"]);
        Assert.Equal("c6727c476c2ec821fcd94cc475d05415", span.TraceId.ToHexString());
    }

    [Fact]
    public void Deadlocks_and_lock_timeouts_are_database_errors_whatever_their_severity()
    {
        var mapper = TestFactory.Mapper(_emitter);
        mapper.Map(TestFactory.ParseEnvelope("""
        {"data":{"baseType":"MessageData","baseData":{"message":"Database deadlock occurred","severityLevel":"Information",
         "properties":{"eventId":"RT0028","sqlServerSessionId":"57","sqlStatement":"UPDATE [CRONUS$Item] SET ...","alObjectId":"80"}}}}
        """));
        mapper.Map(TestFactory.ParseEnvelope("""
        {"data":{"baseType":"MessageData","baseData":{"message":"Database lock timed out","severityLevel":"Error","properties":{"eventId":"RT0012"}}}}
        """));

        Assert.All(_emitter.Spans, s => Assert.False(s.Success));
        Assert.All(_emitter.Spans, s => Assert.Equal(System.Diagnostics.ActivityKind.Client, s.Kind));
        Assert.Equal("DatabaseDeadlock", _emitter.Spans[0].Item.Events![0].Attributes.Single(a => a.Key == "exception.type").Value);
        Assert.Equal("DatabaseLockTimeout", _emitter.Spans[1].Item.Events![0].Attributes.Single(a => a.Key == "exception.type").Value);
        Assert.All(_emitter.Logs, l => Assert.Equal(LogLevel.Error, l.Level));
    }

    [Fact]
    public void Errors_get_a_fingerprint_and_a_new_kind_is_announced_once()
    {
        var errors = new ErrorRegistry();
        var mapper = TestFactory.Mapper(_emitter, errors: errors);
        const string error = """
        {"time":"2026-10-31T08:00:00Z","tags":{"ai.operation.id":"c6727c47-6c2e-c821-fcd9-4cc475d05415","ai.session.id":"5f526f81-123f-46f3-b573-8d293d8d2940"},
         "data":{"baseType":"MessageData","baseData":{"message":"Error dialog shown: MetadataNotFound","severityLevel":"Error",
         "properties":{"eventId":"RT0030","failureReason":"MetadataNotFound","alErrorMessage":"Page 99999999 not found.","alObjectType":"System","alObjectId":"0","componentVersion":"29.0.55365.0"}}}}
        """;
        mapper.Map(TestFactory.ParseEnvelope(error));
        mapper.Map(TestFactory.ParseEnvelope(error.Replace("99999999", "12345")));

        var fingerprint = (string)_emitter.Spans[0].Tags["bc.error.fingerprint"]!;
        Assert.Equal(fingerprint, _emitter.Spans[1].Tags["bc.error.fingerprint"]);
        Assert.Single(_emitter.Logs, l => l.EventName == "bc.error.first_seen");

        var summary = Assert.Single(errors.List(TimeSpan.FromDays(3650)));
        Assert.Equal(2, summary.Count);
        Assert.Equal(1, summary.Sessions);

        var report = SupportEndpoints.Report(summary, "https://o11y.example/trace/{traceId}");
        Assert.Contains("MetadataNotFound", report);
        Assert.Contains("RT0030", report);
        Assert.Contains("https://o11y.example/trace/c6727c476c2ec821fcd94cc475d05415", report);
        Assert.Contains("bc.session.id: `5f526f81-123f-46f3-b573-8d293d8d2940`", report);
    }

    [Fact]
    public void Local_instances_carry_their_database_but_remote_hosts_do_not()
    {
        var catalog = new BcInstanceCatalog(new Dictionary<string, BcDatabase> { ["BC290"] = BcDatabase.Create("SQL01", "BC", "BC Prod") });
        var resolver = new SourceResolver(Options.Create(new ResourceOptions { HostName = "SRV01" }), catalog);

        var local = resolver.Local("BC290");
        Assert.Equal("SQL01\\BC", local.Attributes["bc.database.server"]);
        Assert.Equal("BC Prod", local.Attributes["bc.database.name"]);
        Assert.Same(local.Attributes, resolver.Local("BC290").Attributes);
        Assert.False(resolver.Resolve("SRV02", "BC290", TestFactory.ParseEnvelope(RealEnvelope), null).Attributes.ContainsKey("bc.database.server"));
    }

    [Theory]
    [InlineData("localhost", "SQLEXPRESS", "SQLEXPRESS")]
    [InlineData("SQL01,1433", null, null)]
    [InlineData("sql01.corp.local", "BC", "BC")]
    public void Database_server_names_are_normalized(string server, string? instance, string? expectedInstance)
    {
        var db = BcDatabase.Create(server, instance, "Demo");
        var host = server == "localhost" ? Environment.MachineName : server.Split(',')[0];
        Assert.Equal(host, db.Host);
        Assert.Equal(expectedInstance is null ? host : $"{host}\\{expectedInstance}", db.DisplayName);
    }

    [Fact]
    public void Custom_settings_provide_the_database()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <appSettings>
              <add key="DatabaseServer" value="SQL01" />
              <add key="DatabaseInstance" value="" />
              <add key="DatabaseName" value="BC Prod" />
            </appSettings>
            """);
        try
        {
            Assert.Equal(new BcDatabase("SQL01", "SQL01", "BC Prod"), BcInstanceCatalog.ReadDatabase(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sql_monitoring_targets_are_discovered_once_per_server_and_configurable()
    {
        var catalog = new BcInstanceCatalog(new Dictionary<string, BcDatabase>
        {
            ["BC1"] = BcDatabase.Create("SQL01", "BC", "A"),
            ["BC2"] = BcDatabase.Create("SQL01", "BC", "B"),
        });
        var options = new SqlMonitoringOptions { Servers = { ["SQL02"] = "Server=SQL02;User ID=mon" } };

        var targets = SqlServerMonitor.Targets(options, catalog);

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.Name == "SQL01\\BC" && t.ConnectionString.StartsWith("Server=SQL01\\BC;", StringComparison.Ordinal));
        Assert.Contains(targets, t => t.Name == "SQL02");
        Assert.Empty(SqlServerMonitor.Targets(new SqlMonitoringOptions { AutoDiscover = false }, catalog));
    }

    [Fact]
    public void Deadlock_reports_are_parsed()
    {
        var deadlock = SqlServerMonitor.Deadlock.Parse("""
            <deadlock>
              <victim-list><victimProcess id="process2" /></victim-list>
              <process-list>
                <process id="process1" spid="61" clientapp="Microsoft Dynamics 365 Business Central Server" hostname="BC01" currentdbname="BC Prod" waitresource="KEY: 5:72 (a)"><inputbuf>UPDATE [CRONUS$Item] SET a=@0</inputbuf></process>
                <process id="process2" spid="57" clientapp="Microsoft Dynamics 365 Business Central Server" hostname="BC01" currentdbname="BC Prod" waitresource="KEY: 5:73 (b)"><inputbuf>UPDATE [CRONUS$Customer] SET b=@0</inputbuf></process>
              </process-list>
              <resource-list>
                <keylock objectname="BC Prod.dbo.CRONUS$Item" /><keylock objectname="BC Prod.dbo.CRONUS$Customer" />
              </resource-list>
            </deadlock>
            """);

        Assert.NotNull(deadlock);
        Assert.Equal(57, deadlock.Victim!.Spid);
        Assert.Equal("UPDATE [CRONUS$Customer] SET b=@0", deadlock.Victim.Statement);
        Assert.Equal("BC Prod.dbo.CRONUS$Customer, BC Prod.dbo.CRONUS$Item", deadlock.Objects);
        Assert.Equal(2, deadlock.Processes.Count);
    }

    [Fact]
    public void Long_running_sql_is_a_database_client_span_and_failures_are_errors()
    {
        var mapper = TestFactory.Mapper(_emitter);
        mapper.Map(TestFactory.ParseEnvelope("""
        {"time":"2026-10-31T08:00:00Z","data":{"baseType":"MessageData","baseData":{"message":"Operation exceeded time threshold (SQL query)","severityLevel":"Warning",
         "properties":{"eventId":"RT0005","executionTime":"00:00:01.5000000","sqlStatement":"SELECT 1"}}}}
        """));
        mapper.Map(TestFactory.ParseEnvelope("""
        {"time":"2026-10-31T08:00:01Z","data":{"baseType":"MessageData","baseData":{"message":"Outgoing web service request","severityLevel":"Information",
         "properties":{"eventId":"RT0019","serverExecutionTime":"00:00:00.2000000","endpoint":"https://api.example.com/x","httpMethod":"GET","httpStatusCode":"503"}}}}
        """));

        var sql = _emitter.Spans[0];
        Assert.Equal(System.Diagnostics.ActivityKind.Client, sql.Kind);
        Assert.Equal("microsoft.sql_server", sql.Tags["db.system.name"]);
        Assert.Equal(TimeSpan.FromSeconds(1.5), sql.Duration);
        Assert.True(sql.Success);

        var call = _emitter.Spans[1];
        Assert.Equal(System.Diagnostics.ActivityKind.Client, call.Kind);
        Assert.Equal("api.example.com", call.Tags["server.address"]);
        Assert.Equal("GET", call.Tags["http.request.method"]);
        Assert.False(call.Success);
    }

    [Fact]
    public void Informational_event_without_duration_stays_a_plain_log()
    {
        TestFactory.Mapper(_emitter).Map(TestFactory.ParseEnvelope("""
        {"data":{"baseType":"MessageData","baseData":{"message":"Task created","severityLevel":"Information","properties":{"eventId":"LC0040"}}}}
        """));

        Assert.Empty(_emitter.Spans);
        Assert.Null(Assert.Single(_emitter.Logs).Item.TraceId);
    }

    [Fact]
    public void Error_event_becomes_a_failed_span_with_an_exception_event()
    {
        TestFactory.Mapper(_emitter).Map(TestFactory.ParseEnvelope("""
        {"time":"2026-10-31T08:00:00Z","data":{"baseType":"MessageData","baseData":{"message":"Error dialog shown: MetadataNotFound","severityLevel":"Error",
         "properties":{"eventId":"RT0030","failureReason":"MetadataNotFound","alErrorMessage":"Page 99999999 not found.","alStackTrace":"MyCodeunit(CodeUnit 50100).Run line 3"}}}}
        """));

        var span = Assert.Single(_emitter.Spans);
        Assert.False(span.Success);
        Assert.Equal(System.Diagnostics.ActivityKind.Internal, span.Kind);
        Assert.Equal(TimeSpan.Zero, span.Duration);
        var exception = Assert.Single(span.Item.Events!);
        Assert.Equal("exception", exception.Name);
        var a = exception.Attributes.ToDictionary(x => x.Key, x => x.Value);
        Assert.Equal("MetadataNotFound", a["exception.type"]);
        Assert.Equal("Page 99999999 not found.", a["exception.message"]);
        Assert.Equal("MyCodeunit(CodeUnit 50100).Run line 3", a["exception.stacktrace"]);
        Assert.Equal(span.Item.SpanId, Assert.Single(_emitter.Logs).Item.SpanId);
    }

    [Fact]
    public void Exception_telemetry_carries_type_message_and_stack_into_the_exception_event()
    {
        TestFactory.Mapper(_emitter).Map(TestFactory.ParseEnvelope("""
        {"data":{"baseType":"ExceptionData","baseData":{"exceptions":[{"typeName":"NavCSideException","message":"boom","stack":"at X()"}]}}}
        """));

        var a = Assert.Single(Assert.Single(_emitter.Spans).Item.Events!).Attributes.ToDictionary(x => x.Key, x => x.Value);
        Assert.Equal("NavCSideException", a["exception.type"]);
        Assert.Equal("boom", a["exception.message"]);
        Assert.Equal("at X()", a["exception.stacktrace"]);
    }

    [Fact]
    public void Job_queue_execution_time_in_ms_makes_a_span()
    {
        TestFactory.Mapper(_emitter).Map(TestFactory.ParseEnvelope("""
        {"data":{"baseType":"MessageData","baseData":{"message":"Job queue entry finished: 8C8FC954-03E4-48C6-8AE5-91E646903F7D","severityLevel":"Information",
         "properties":{"eventId":"AL0000E26","alJobQueueExecutionTimeInMs":"88","alJobQueueNumberOfAttemptsToRun":"0"}}}}
        """));

        var span = Assert.Single(_emitter.Spans);
        Assert.Equal("Job queue entry finished: {id}", span.Name);
        Assert.Equal(TimeSpan.FromMilliseconds(88), span.Duration);
        Assert.Equal(0L, span.Tags["alJobQueueNumberOfAttemptsToRun"]);
    }

    [Theory]
    [InlineData("Task 11321362-c44f-4197-87eb-ff87b403862f main codeunit 448 completed.", "Task {id} main codeunit {n} completed.")]
    [InlineData("Task 11321362-c44f-4197-87eb-ff87b403862f created: 448 scheduled to run after 2026-10-31 09:37:44Z. Ready to run: True", "Task {id} created: {n} scheduled to run after {time}. Ready to run: True")]
    [InlineData("Authorization Succeeded (Open Company)", "Authorization Succeeded (Open Company)")]
    [InlineData("Web service called (ODataV4): Customers", "Web service called (ODataV4): Customers")]
    public void Span_names_have_ids_replaced(string message, string expected)
        => Assert.Equal(expected, AiToOtelMapper.SpanName(message, null));

    [Fact]
    public void Bc_version_becomes_service_version_also_for_locally_collected_metrics()
    {
        var resolver = new SourceResolver(Options.Create(new ResourceOptions { HostName = "SRV01" }));

        var source = resolver.Resolve("SRV01", "BC290", TestFactory.ParseEnvelope(RealEnvelope), null);

        Assert.Equal("29.0.55365.0", source.ServiceVersion);
        Assert.Equal(source, resolver.Local("BC290"));
        Assert.Null(resolver.Local(null).ServiceVersion);
    }

    [Theory]
    [InlineData("% Primary key cache hit rate", "bc.server.primary_key_cache_hit_rate", "%")]
    [InlineData("# Active sessions", "bc.server.active_sessions", null)]
    [InlineData("Heartbeat time (ms)", "bc.server.heartbeat_time", "ms")]
    [InlineData("Server operations/sec", "bc.server.server_operations_per_sec", "1/s")]
    [InlineData("Time (ms) since the list of running tasks last had capacity for new tasks", "bc.server.time_since_the_list_of_running_tasks_last_had_capacity_for_new_tasks", "ms")]
    public void Bc_counter_names_become_metric_names(string counter, string name, string? unit)
    {
        var (actualName, actualUnit) = BcTelemetryProxy.HostTelemetry.BcCounterNames.MetricName(counter);
        Assert.Equal(name, actualName);
        Assert.Equal(unit, actualUnit);
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
        stats.RecordError("logs: down", "logs");
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

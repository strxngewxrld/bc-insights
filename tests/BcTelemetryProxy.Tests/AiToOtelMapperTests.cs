using System.Diagnostics;
using System.Text.Json;
using BcTelemetryProxy.Mapping;
using BcTelemetryProxy.Model;
using Microsoft.Extensions.Logging;

namespace BcTelemetryProxy.Tests;

public class AiToOtelMapperTests
{
    private readonly RecordingEmitter _emitter = new();
    private readonly AiToOtelMapper _mapper;

    public AiToOtelMapperTests() => _mapper = new AiToOtelMapper(_emitter);

    private static Envelope Parse(string json)
        => JsonSerializer.Deserialize<Envelope>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void MessageData_maps_to_log_with_severity_body_and_eventId()
    {
        var env = Parse("""
        {"time":"2026-07-10T08:15:30Z","tags":{"ai.cloud.role":"BC","ai.operation.id":"0123456789abcdef0123456789abcdef"},
         "data":{"baseType":"MessageData","baseData":{"message":"hello","severityLevel":2,"properties":{"eventId":"RT0005","companyName":"CRONUS"}}}}
        """);

        _mapper.Map(env);

        var log = Assert.Single(_emitter.Logs);
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Equal("hello", log.Body);
        Assert.Equal("RT0005", log.EventName);
        Assert.Equal("RT0005", log.Attributes["eventId"]);
        Assert.Equal("CRONUS", log.Attributes["companyName"]);
        Assert.Equal("BC", log.Attributes["bc.cloud.role"]);
        Assert.Equal("0123456789abcdef0123456789abcdef", log.Attributes["bc.operation.id"]);
    }

    [Fact]
    public void RequestData_maps_to_server_span_with_duration_and_traceId()
    {
        var env = Parse("""
        {"time":"2026-07-10T08:15:31Z","tags":{"ai.operation.id":"0123456789abcdef0123456789abcdef"},
         "data":{"baseType":"RequestData","baseData":{"name":"OpenPage","duration":"00:00:00.4560000","responseCode":"200","success":true,"url":"al://page/22"}}}
        """);

        _mapper.Map(env);

        var span = Assert.Single(_emitter.Spans);
        Assert.Equal("OpenPage", span.Name);
        Assert.Equal(ActivityKind.Server, span.Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(456), span.Duration);
        Assert.True(span.Success);
        Assert.Equal("0123456789abcdef0123456789abcdef", span.TraceId.ToHexString());
        Assert.Equal("200", span.Tags["http.response.status_code"]);
        Assert.Equal("al://page/22", span.Tags["url.full"]);
    }

    [Fact]
    public void RemoteDependencyData_maps_to_client_span()
    {
        var env = Parse("""
        {"time":"2026-07-10T08:15:31Z","tags":{},
         "data":{"baseType":"RemoteDependencyData","baseData":{"name":"SELECT Customer","duration":"00:00:00.0230000","success":false,"resultCode":"1","type":"SQL","target":"BC-SQL-01"}}}
        """);

        _mapper.Map(env);

        var span = Assert.Single(_emitter.Spans);
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.False(span.Success);
        Assert.Equal("SQL", span.Tags["bc.dependency.type"]);
        Assert.Equal("BC-SQL-01", span.Tags["bc.dependency.target"]);
    }

    [Fact]
    public void ExceptionData_maps_to_error_log_with_exception_attributes()
    {
        var env = Parse("""
        {"time":"2026-07-10T08:15:32Z","tags":{},
         "data":{"baseType":"ExceptionData","baseData":{"severityLevel":3,"exceptions":[{"typeName":"NavDatabaseException","message":"boom","stack":"at X()"}],"properties":{"eventId":"RT0030"}}}}
        """);

        _mapper.Map(env);

        var log = Assert.Single(_emitter.Logs);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.Equal("boom", log.Body);
        Assert.Equal("NavDatabaseException", log.Attributes["exception.type"]);
        Assert.Equal("boom", log.Attributes["exception.message"]);
        Assert.Equal("at X()", log.Attributes["exception.stacktrace"]);
    }

    [Fact]
    public void MetricData_maps_to_metric()
    {
        var env = Parse("""
        {"time":"2026-07-10T08:15:33Z","tags":{"ai.cloud.role":"BC"},
         "data":{"baseType":"MetricData","baseData":{"metrics":[{"name":"Server sessions","value":42,"count":1}],"properties":{"eventId":"RT0009"}}}}
        """);

        _mapper.Map(env);

        var metric = Assert.Single(_emitter.Metrics);
        Assert.Equal("Server sessions", metric.Name);
        Assert.Equal(42, metric.Value);
        Assert.Equal("BC", metric.Tags["bc.cloud.role"]);
    }

    [Fact]
    public void EventData_maps_to_info_log()
    {
        var env = Parse("""
        {"time":"2026-07-10T08:15:34Z","tags":{},
         "data":{"baseType":"EventData","baseData":{"name":"CompanyOpen","properties":{"eventId":"AL0000E24"}}}}
        """);

        _mapper.Map(env);

        var log = Assert.Single(_emitter.Logs);
        Assert.Equal(LogLevel.Information, log.Level);
        Assert.Equal("CompanyOpen", log.Body);
        Assert.Equal("AL0000E24", log.EventName);
    }

    [Fact]
    public void Unknown_baseType_is_not_dropped()
    {
        var env = Parse("""
        {"time":"2026-07-10T08:15:35Z","tags":{},
         "data":{"baseType":"AvailabilityData","baseData":{"name":"ping","properties":{}}}}
        """);

        _mapper.Map(env);

        Assert.Single(_emitter.Logs);
    }
}

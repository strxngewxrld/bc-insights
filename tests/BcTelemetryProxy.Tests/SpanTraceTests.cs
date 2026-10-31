using BcTelemetryProxy.Mapping;

namespace BcTelemetryProxy.Tests;

public class SpanTraceTests
{
    private readonly RecordingEmitter _emitter = new();
    private readonly AiToOtelMapper _mapper;

    public SpanTraceTests() => _mapper = TestFactory.Mapper(_emitter);

    [Theory]
    [InlineData("1250", "00:00:09", 1250)]
    [InlineData("1250", null, 1250)]
    [InlineData("NaN", "00:00:01", 1000)]
    [InlineData("Infinity", "00:00:01", 1000)]
    [InlineData("1e100", "00:00:01", 1000)]
    public void Numeric_duration_companion_wins_and_invalid_values_fall_back(string numeric, string? duration, double expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            data = new { baseType = "MessageData", baseData = new { message = "SQL", properties = new
            { eventId = "RT0005", executionTime = duration, executionTimeInMs = numeric } } },
        });
        Assert.Equal(MappingStatus.Mapped, _mapper.Map(TestFactory.ParseEnvelope(json)));
        Assert.Equal(expected, Assert.Single(_emitter.Spans).Item.Duration.TotalMilliseconds);
    }

    [Fact]
    public void Request_without_parent_id_is_a_root_span()
    {
        var env = TestFactory.ParseEnvelope("""
        {"tags":{"ai.operation.id":"0123456789abcdef0123456789abcdef"},
         "data":{"baseType":"RequestData","baseData":{"name":"OpenPage","success":true}}}
        """);

        _mapper.Map(env);

        var span = Assert.Single(_emitter.Spans);
        Assert.Null(span.ParentSpanId);
    }

    [Fact]
    public void Request_with_valid_parent_id_keeps_it()
    {
        var env = TestFactory.ParseEnvelope("""
        {"tags":{"ai.operation.id":"0123456789abcdef0123456789abcdef","ai.operation.parentId":"abcdef0123456789"},
         "data":{"baseType":"RequestData","baseData":{"name":"OpenPage","success":true}}}
        """);

        _mapper.Map(env);

        var span = Assert.Single(_emitter.Spans);
        Assert.Equal("abcdef0123456789", span.ParentSpanId!.Value.ToHexString());
    }
}

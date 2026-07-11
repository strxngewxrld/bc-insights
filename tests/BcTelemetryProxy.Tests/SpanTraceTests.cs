using BcTelemetryProxy.Mapping;

namespace BcTelemetryProxy.Tests;

public class SpanTraceTests
{
    private readonly RecordingEmitter _emitter = new();
    private readonly AiToOtelMapper _mapper;

    public SpanTraceTests() => _mapper = TestFactory.Mapper(_emitter);

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

using BcTelemetryProxy.Mapping;

namespace BcTelemetryProxy.Tests;

public class TraceContextMapperTests
{
    [Fact]
    public void Valid_w3c_trace_id_is_preserved()
    {
        const string id = "0123456789abcdef0123456789abcdef";
        Assert.Equal(id, TraceContextMapper.ResolveTraceId(id).ToHexString());
    }

    [Fact]
    public void Invalid_operation_id_is_hashed_deterministically()
    {
        var first = TraceContextMapper.ResolveTraceId("{ABC-123-not-hex}");
        var second = TraceContextMapper.ResolveTraceId("{ABC-123-not-hex}");
        Assert.Equal(first.ToHexString(), second.ToHexString());
    }

    [Fact]
    public void Different_operation_ids_produce_different_trace_ids()
    {
        var a = TraceContextMapper.ResolveTraceId("operation-a").ToHexString();
        var b = TraceContextMapper.ResolveTraceId("operation-b").ToHexString();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Short_ids_are_not_naively_padded_or_collapsed()
    {
        // "abcd" and "abcd0" would collide under truncate/zero-pad normalisation.
        var a = TraceContextMapper.ResolveTraceId("abcd").ToHexString();
        var b = TraceContextMapper.ResolveTraceId("abcd0").ToHexString();
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("abcd0000000000000000000000000000", new[] { a, b });
    }

    [Fact]
    public void Missing_operation_id_yields_a_valid_random_trace_id()
    {
        var id = TraceContextMapper.ResolveTraceId(null);
        Assert.NotEqual("00000000000000000000000000000000", id.ToHexString());
    }

    [Fact]
    public void Valid_parent_span_id_is_preserved()
    {
        const string span = "abcdef0123456789";
        Assert.Equal(span, TraceContextMapper.ResolveParentSpanId(span)!.Value.ToHexString());
    }

    [Fact]
    public void Missing_or_invalid_parent_span_id_yields_no_parent()
    {
        Assert.Null(TraceContextMapper.ResolveParentSpanId(null));
        Assert.Null(TraceContextMapper.ResolveParentSpanId("not-a-span"));
        Assert.Null(TraceContextMapper.ResolveParentSpanId("0000000000000000"));
    }
}

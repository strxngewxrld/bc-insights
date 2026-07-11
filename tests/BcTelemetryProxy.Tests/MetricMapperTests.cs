using System.Text.Json;
using BcTelemetryProxy.Mapping;

namespace BcTelemetryProxy.Tests;

public class MetricMapperTests
{
    private static JsonElement Data(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Single_value_is_a_gauge()
    {
        var point = new AiMetricPoint("Server sessions", 42, Count: 1, null, null, null);
        Assert.Equal(MetricKind.Gauge, MetricMapper.Classify(point));
    }

    [Fact]
    public void Aggregated_point_is_a_histogram()
    {
        var point = new AiMetricPoint("Sql query duration", 1000, Count: 10, Min: 5, Max: 200, StandardDeviation: 12);
        Assert.Equal(MetricKind.Histogram, MetricMapper.Classify(point));
    }

    [Fact]
    public void Counter_name_is_a_counter()
    {
        var point = new AiMetricPoint("Number of requests", 5, Count: 1, null, null, null);
        Assert.Equal(MetricKind.Counter, MetricMapper.Classify(point));
    }

    [Fact]
    public void Parse_reads_all_statistical_fields()
    {
        var data = Data("""{"metrics":[{"name":"m","value":100,"count":4,"min":10,"max":40,"stdDev":8}]}""");
        var point = Assert.Single(MetricMapper.Parse(data));
        Assert.Equal("m", point.Name);
        Assert.Equal(100, point.Value);
        Assert.Equal(4, point.Count);
        Assert.Equal(10, point.Min);
        Assert.Equal(40, point.Max);
        Assert.Equal(8, point.StandardDeviation);
    }
}

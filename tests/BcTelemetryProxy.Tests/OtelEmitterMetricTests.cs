using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Mapping;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.Tests;

public class OtelEmitterMetricTests
{
    [Fact]
    public void Instrument_count_is_bounded_by_the_configured_cap()
    {
        using var emitter = new OtelEmitter(
            NullLogger<OtelEmitter>.Instance,
            Options.Create(new MetricsOptions { MaxInstruments = 5 }));

        var tags = Array.Empty<KeyValuePair<string, object?>>();
        for (var i = 0; i < 50; i++)
        {
            var point = new AiMetricPoint($"metric-{i}", i, Count: 1, null, null, null);
            emitter.EmitMetric(point, MetricKind.Gauge, tags);
        }

        Assert.Equal(5, emitter.InstrumentCount);
    }
}

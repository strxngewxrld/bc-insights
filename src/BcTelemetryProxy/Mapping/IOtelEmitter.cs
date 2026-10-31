using BcTelemetryProxy.Model;

namespace BcTelemetryProxy.Mapping;

/// <summary>Sink for the individual OpenTelemetry signals produced by the mapper.</summary>
public interface IOtelEmitter
{
    void EmitLog(LogItem log);

    void EmitSpan(SpanItem span);

    void EmitMetric(MetricItem metric);
}

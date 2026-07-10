using BcTelemetryProxy.Ingestion;
using BcTelemetryProxy.Mapping;
using BcTelemetryProxy.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// The local OpenTelemetry Collector this proxy exports to (OTLP/HTTP, port 4318).
// The Collector is the single component that talks to GitLab Observability.
var otlpEndpoint = builder.Configuration["Otlp:Endpoint"] ?? "http://localhost:4318";

void ConfigureOtlp(OtlpExporterOptions options)
{
    options.Endpoint = new Uri(otlpEndpoint);
    options.Protocol = OtlpExportProtocol.HttpProtobuf;
}

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(TelemetryConstants.ServiceName))
    .WithTracing(tracing => tracing
        .AddSource(TelemetryConstants.ActivitySourceName)
        .AddOtlpExporter(ConfigureOtlp))
    .WithMetrics(metrics => metrics
        .AddMeter(TelemetryConstants.MeterName)
        .AddOtlpExporter(ConfigureOtlp));

// Logs use the (fully supported) ILogger integration. IncludeFormattedMessage
// surfaces the BC message as the OTLP log body; custom dimensions flow through as
// structured attributes.
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(TelemetryConstants.ServiceName));
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = false;
    logging.ParseStateValues = true;
    logging.AddOtlpExporter(ConfigureOtlp);
});

// Translation pipeline.
builder.Services.AddSingleton<IOtelEmitter, OtelEmitter>();
builder.Services.AddSingleton<AiToOtelMapper>();

var app = builder.Build();

app.MapAppInsightsIngestion();

app.Run();

// Exposed so WebApplicationFactory-based integration tests can spin up the host.
public partial class Program;

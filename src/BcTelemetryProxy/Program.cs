using BcTelemetryProxy.Configuration;
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

// -- Options ----------------------------------------------------------------
builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection(IngestionOptions.SectionName));
builder.Services.Configure<TelemetryPrivacyOptions>(builder.Configuration.GetSection(TelemetryPrivacyOptions.SectionName));
builder.Services.Configure<MetricsOptions>(builder.Configuration.GetSection(MetricsOptions.SectionName));
builder.Services.Configure<ResourceOptions>(builder.Configuration.GetSection(ResourceOptions.SectionName));

var ingestion = builder.Configuration.GetSection(IngestionOptions.SectionName).Get<IngestionOptions>() ?? new IngestionOptions();
var resourceOptions = builder.Configuration.GetSection(ResourceOptions.SectionName).Get<ResourceOptions>() ?? new ResourceOptions();

// Backstop the compressed body size at the server level too.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = ingestion.MaxCompressedRequestBytes);

// The local OpenTelemetry Collector this proxy exports to (OTLP/HTTP, port 4318).
// The Collector is the single component that talks to the observability backend.
var otlpEndpoint = builder.Configuration["Otlp:Endpoint"] ?? "http://localhost:4318";

void ConfigureOtlp(OtlpExporterOptions options)
{
    options.Endpoint = new Uri(otlpEndpoint);
    options.Protocol = OtlpExportProtocol.HttpProtobuf;
}

// -- Resource ---------------------------------------------------------------
var hostName = resourceOptions.HostName ?? Environment.MachineName;
void ConfigureResource(ResourceBuilder resource)
{
    resource.AddService(
        serviceName: resourceOptions.ServiceName,
        serviceNamespace: resourceOptions.ServiceNamespace,
        serviceInstanceId: resourceOptions.ServiceInstanceId ?? hostName);

    var attributes = new List<KeyValuePair<string, object>> { new("host.name", hostName) };
    if (!string.IsNullOrEmpty(resourceOptions.DeploymentEnvironment))
    {
        attributes.Add(new("deployment.environment.name", resourceOptions.DeploymentEnvironment));
    }

    if (!string.IsNullOrEmpty(resourceOptions.BcServerInstance))
    {
        attributes.Add(new("bc.server_instance", resourceOptions.BcServerInstance));
    }

    if (!string.IsNullOrEmpty(resourceOptions.BcTenantId))
    {
        attributes.Add(new("bc.tenant.id", resourceOptions.BcTenantId));
    }

    resource.AddAttributes(attributes);
}

// -- OpenTelemetry pipeline -------------------------------------------------
builder.Services.AddOpenTelemetry()
    .ConfigureResource(ConfigureResource)
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
    var resource = ResourceBuilder.CreateDefault();
    ConfigureResource(resource);
    logging.SetResourceBuilder(resource);
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = false;
    logging.ParseStateValues = true;
    logging.AddOtlpExporter(ConfigureOtlp);
});

// -- Translation pipeline ---------------------------------------------------
builder.Services.AddSingleton<AttributeMapper>();
builder.Services.AddSingleton<IOtelEmitter, OtelEmitter>();
builder.Services.AddSingleton<AiToOtelMapper>();

var app = builder.Build();

app.MapAppInsightsIngestion();

app.Run();

// Exposed so WebApplicationFactory-based integration tests can spin up the host.
public partial class Program;

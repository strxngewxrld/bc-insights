using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Diagnostics;
using BcTelemetryProxy.Export;
using BcTelemetryProxy.Ingestion;
using BcTelemetryProxy.Mapping;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// -- Options ----------------------------------------------------------------
builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection(IngestionOptions.SectionName));
builder.Services.Configure<TelemetryPrivacyOptions>(builder.Configuration.GetSection(TelemetryPrivacyOptions.SectionName));
builder.Services.Configure<ResourceOptions>(builder.Configuration.GetSection(ResourceOptions.SectionName));
builder.Services.Configure<OtlpOptions>(builder.Configuration.GetSection(OtlpOptions.SectionName));
builder.Services.Configure<DebugOptions>(builder.Configuration.GetSection(DebugOptions.SectionName));

var ingestion = builder.Configuration.GetSection(IngestionOptions.SectionName).Get<IngestionOptions>() ?? new IngestionOptions();

// Backstop the compressed body size at the server level too.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = ingestion.MaxCompressedRequestBytes);

// -- Export -----------------------------------------------------------------
// The proxy exports OTLP/HTTP directly to the backend (e.g. GitLab
// Observability); a Collector in between is optional.
builder.Services.AddSingleton<DebugCapture>();
builder.Services.AddSingleton(sp => new OtlpExporter(
    sp.GetRequiredService<IOptions<OtlpOptions>>(),
    sp.GetRequiredService<ILogger<OtlpExporter>>(),
    capture: sp.GetRequiredService<DebugCapture>()));
builder.Services.AddSingleton<IOtelEmitter>(sp => sp.GetRequiredService<OtlpExporter>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<OtlpExporter>());

// -- Translation pipeline ---------------------------------------------------
builder.Services.AddSingleton<AttributeMapper>();
builder.Services.AddSingleton<SourceResolver>();
builder.Services.AddSingleton<AiToOtelMapper>();

var app = builder.Build();

app.MapAppInsightsIngestion();

app.Run();

// Exposed so WebApplicationFactory-based integration tests can spin up the host.
public partial class Program;

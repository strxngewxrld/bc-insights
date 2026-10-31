using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Diagnostics;
using BcTelemetryProxy.Export;
using BcTelemetryProxy.HostTelemetry;
using BcTelemetryProxy.Ingestion;
using BcTelemetryProxy.Mapping;
using BcTelemetryProxy.Support;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Runs under IIS, as a console app or as a Windows service (sc.exe create ...).
builder.Host.UseWindowsService();

// -- Options ----------------------------------------------------------------
builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection(IngestionOptions.SectionName));
builder.Services.Configure<TelemetryPrivacyOptions>(builder.Configuration.GetSection(TelemetryPrivacyOptions.SectionName));
builder.Services.Configure<ResourceOptions>(builder.Configuration.GetSection(ResourceOptions.SectionName));
builder.Services.Configure<OtlpOptions>(builder.Configuration.GetSection(OtlpOptions.SectionName));
builder.Services.Configure<DebugOptions>(builder.Configuration.GetSection(DebugOptions.SectionName));
builder.Services.Configure<MappingOptions>(builder.Configuration.GetSection(MappingOptions.SectionName));
builder.Services.Configure<HostTelemetryOptions>(builder.Configuration.GetSection(HostTelemetryOptions.SectionName));
builder.Services.Configure<SqlMonitoringOptions>(builder.Configuration.GetSection(SqlMonitoringOptions.SectionName));
builder.Services.Configure<SupportOptions>(builder.Configuration.GetSection(SupportOptions.SectionName));

var ingestion = builder.Configuration.GetSection(IngestionOptions.SectionName).Get<IngestionOptions>() ?? new IngestionOptions();

// Backstop the compressed body size at the server level too.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = ingestion.MaxCompressedRequestBytes);

// -- Export -----------------------------------------------------------------
// The proxy exports OTLP/HTTP directly to the backend (e.g. GitLab
// Observability); a Collector in between is optional.
builder.Services.AddSingleton<DebugCapture>();
builder.Services.AddSingleton<PerformanceRegistry>();
builder.Services.AddSingleton(sp => new OtlpExporter(
    sp.GetRequiredService<IOptions<OtlpOptions>>(),
    sp.GetRequiredService<ILogger<OtlpExporter>>(),
    capture: sp.GetRequiredService<DebugCapture>(),
    performance: sp.GetRequiredService<PerformanceRegistry>()));
builder.Services.AddSingleton<IOtelEmitter>(sp => sp.GetRequiredService<OtlpExporter>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<OtlpExporter>());

// -- Translation pipeline ---------------------------------------------------
builder.Services.AddSingleton<AttributeMapper>();
builder.Services.AddSingleton(sp => BcInstanceCatalog.Discover(sp.GetRequiredService<ILogger<BcInstanceCatalog>>()));
builder.Services.AddSingleton<ErrorRegistry>();
builder.Services.AddSingleton<SourceResolver>();
builder.Services.AddSingleton<AiToOtelMapper>();

// -- Host telemetry (Windows) -------------------------------------------------
// Server metrics, BC performance counters and BC event log of this machine;
// each service checks HostTelemetry options itself and idles when disabled.
if (OperatingSystem.IsWindows())
{
    builder.Services.AddHostedService<HostMetricsCollector>();
    builder.Services.AddHostedService<EventLogForwarder>();
}

// -- Database tier ------------------------------------------------------------
// Agentless SQL Server monitoring of the BC databases (also remote, 2-tier).
builder.Services.AddHostedService<SqlServerMonitor>();

var app = builder.Build();

app.MapAppInsightsIngestion();
app.MapSupport();

app.Run();

// Exposed so WebApplicationFactory-based integration tests can spin up the host.
public partial class Program;

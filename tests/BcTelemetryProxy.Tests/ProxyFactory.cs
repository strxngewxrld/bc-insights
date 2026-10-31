using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BcTelemetryProxy.Tests;

/// <summary>Test host without the collectors that read this machine (counters, event log, SQL Server).</summary>
public sealed class ProxyFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("HostTelemetry:Enabled", "false");
        builder.UseSetting("SqlMonitoring:Enabled", "false");
    }
}

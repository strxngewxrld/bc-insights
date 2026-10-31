using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Model;
using BcTelemetryProxy.Support;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.Tests;

public class PerformanceTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 17, 0, 0, DateTimeKind.Utc);
    private static SpanItem Span(double ms, string instance = "BC290", string version = "29.0", DateTime? at = null,
        bool success = true) => new(
            new TelemetrySource("business-central", "ns", "SRV01", instance, "test") { ServiceVersion = version },
            "Slow SQL", ActivityKind.Client, (at ?? Now).AddMilliseconds(-ms), TimeSpan.FromMilliseconds(ms),
            ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), null, success, null,
            new KeyValuePair<string, object?>[] { new("bc.event.id", "RT0005"), new("alObjectId", "50100"),
                new("extensionName", "Sales"), new("sqlExecutes", 10L), new("sqlStatement", "secret SQL"),
                new("alStackTrace", "secret stack") });

    [Fact]
    public void Ranks_by_total_and_computes_nearest_rank_p95_with_slowest_trace()
    {
        var registry = new PerformanceRegistry(Options.Create(new SupportOptions()));
        for (var i = 1; i <= 20; i++) registry.Record(Span(i * 100, success: i != 20));
        registry.Record(Span(5000, instance: "BC250"));
        var overview = registry.Overview(nowUtc: Now);
        var top = overview.Operations[0];
        Assert.Equal(21, overview.ObservedSpans);
        Assert.Equal("SRV01/BC290", top.Key.Instance);
        Assert.Equal(21000, top.TotalMs);
        Assert.Equal(1050, top.AverageMs);
        Assert.Equal(1900, top.P95Ms);
        Assert.Equal(2000, top.MaxMs);
        Assert.Equal(1, top.Failed);
        Assert.Equal("10", top.Slowest.Details["sqlExecutes"]);
        Assert.DoesNotContain("sqlStatement", top.Slowest.Details.Keys);
        Assert.DoesNotContain("alStackTrace", top.Slowest.Details.Keys);
        Assert.Contains("query plan", top.NextStep);
        var report = SupportEndpoints.PerformanceReport(overview, "https://example.com/trace/{traceId}");
        Assert.Contains(top.Slowest.TraceId, report);
        Assert.Contains("Sales", report);
        Assert.DoesNotContain("secret", report);
    }

    [Fact]
    public void Filters_event_time_instance_and_separates_builds()
    {
        var registry = new PerformanceRegistry(Options.Create(new SupportOptions()));
        registry.Record(Span(100, at: Now.AddHours(-2)));
        registry.Record(Span(200, at: Now.AddHours(1)));
        registry.Record(Span(300));
        registry.Record(Span(400, version: "29.1"));
        registry.Record(Span(500, instance: "BC250"));
        var overview = registry.Overview(1, "srv01/bc290", Now);
        Assert.Equal(2, overview.ObservedSpans);
        Assert.Equal(2, overview.Operations.Count);
    }

    [Fact]
    public void Bounds_memory_reports_evictions_and_honors_disabled_support()
    {
        var options = new SupportOptions { MaxPerformanceSamples = 2 };
        var registry = new PerformanceRegistry(Options.Create(options));
        registry.Record(Span(100)); registry.Record(Span(200)); registry.Record(Span(300));
        registry.Record(Span(0));
        var overview = registry.Overview(nowUtc: Now);
        Assert.Equal(2, overview.ObservedSpans);
        Assert.Equal(1, overview.EvictedSamples);
        Assert.Equal(500, Assert.Single(overview.Operations).TotalMs);
        options.Enabled = false;
        registry.Record(Span(400));
        Assert.Equal(500, Assert.Single(registry.Overview(nowUtc: Now).Operations).TotalMs);
    }

    [Fact]
    public async Task Ingestion_populates_overview_and_markdown_report()
    {
        await using var factory = new ProxyFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/TEST-HOST/TEST-BC/v2/track", new StringContent("""
            {"data":{"baseType":"MessageData","baseData":{"message":"Slow SQL","properties":{"eventId":"RT0005","executionTimeInMs":"1250","extensionName":"Test extension"}}}}
            """, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var overview = await client.GetFromJsonAsync<PerformanceOverview>("/support/performance?hours=1&instance=TEST-HOST%2FTEST-BC");
        Assert.Equal(1250, Assert.Single(overview!.Operations).MaxMs);
        var report = await client.GetStringAsync("/support/performance/report?hours=1");
        Assert.Contains("Test extension", report);
        Assert.Contains("TEST-HOST/TEST-BC", report);
    }
}

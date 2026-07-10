using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BcTelemetryProxy.Tests;

/// <summary>
/// Exercises the fake Application Insights ingestion endpoint end-to-end through
/// the real ASP.NET Core host (translation runs; OTLP export happens in the
/// background and is not asserted here).
/// </summary>
public class IngestionEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public IngestionEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Track_accepts_gzip_ndjson_and_returns_ai_summary()
    {
        var ndjson = string.Join("\n",
            """{"name":"Microsoft.ApplicationInsights.Message","time":"2026-07-10T08:15:30Z","tags":{"ai.cloud.role":"BC","ai.operation.id":"0123456789abcdef0123456789abcdef"},"data":{"baseType":"MessageData","baseData":{"message":"hello","severityLevel":1,"properties":{"eventId":"RT0005"}}}}""",
            """{"name":"Microsoft.ApplicationInsights.Request","time":"2026-07-10T08:15:31Z","tags":{"ai.operation.id":"0123456789abcdef0123456789abcdef"},"data":{"baseType":"RequestData","baseData":{"name":"OpenPage","duration":"00:00:00.4560000","responseCode":"200","success":true}}}""");

        using var content = new StreamContent(EnvelopeReaderTests.Gzip(ndjson));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-json-stream");
        content.Headers.ContentEncoding.Add("gzip");

        var client = _factory.CreateClient();
        var response = await client.PostAsync("/v2/track", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(2, doc.RootElement.GetProperty("itemsReceived").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("itemsAccepted").GetInt32());
    }

    [Fact]
    public async Task Unknown_control_endpoint_returns_ok()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/QuickPulseService.svc/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_returns_ok()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

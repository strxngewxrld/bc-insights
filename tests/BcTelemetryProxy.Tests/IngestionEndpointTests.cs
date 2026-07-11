using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace BcTelemetryProxy.Tests;

/// <summary>
/// Exercises the fake Application Insights ingestion endpoint end-to-end through
/// the real ASP.NET Core host (translation runs; OTLP export happens in the
/// background and is not asserted here).
/// </summary>
public class IngestionEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Message = """{"name":"Microsoft.ApplicationInsights.Message","time":"2026-07-10T08:15:30Z","tags":{"ai.operation.id":"0123456789abcdef0123456789abcdef"},"data":{"baseType":"MessageData","baseData":{"message":"hello","severityLevel":1,"properties":{"eventId":"RT0005"}}}}""";
    private const string Request = """{"name":"Microsoft.ApplicationInsights.Request","time":"2026-07-10T08:15:31Z","tags":{"ai.operation.id":"0123456789abcdef0123456789abcdef"},"data":{"baseType":"RequestData","baseData":{"name":"OpenPage","duration":"00:00:00.4560000","responseCode":"200","success":true}}}""";

    private readonly WebApplicationFactory<Program> _factory;

    public IngestionEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Track_accepts_gzip_ndjson_and_returns_ai_summary()
    {
        using var content = new StreamContent(EnvelopeReaderTests.Gzip(Message + "\n" + Request));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-json-stream");
        content.Headers.ContentEncoding.Add("gzip");

        var response = await _factory.CreateClient().PostAsync("/v2/track", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("itemsReceived").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("itemsAccepted").GetInt32());
    }

    [Fact]
    public async Task Malformed_line_yields_partial_success()
    {
        using var content = new StringContent(Message + "\nnot-json\n" + Request, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-json-stream");

        var response = await _factory.CreateClient().PostAsync("/v2/track", content);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, doc.RootElement.GetProperty("itemsReceived").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("itemsAccepted").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("errors").GetArrayLength());
    }

    [Fact]
    public async Task Too_many_envelopes_is_rejected()
    {
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ingestion:MaxEnvelopesPerRequest"] = "1",
            })));

        using var content = new StringContent(Message + "\n" + Request, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-json-stream");

        var response = await factory.CreateClient().PostAsync("/v2/track", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_control_endpoint_returns_ok()
    {
        var response = await _factory.CreateClient().GetAsync("/QuickPulseService.svc/ping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_returns_ok()
    {
        var response = await _factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

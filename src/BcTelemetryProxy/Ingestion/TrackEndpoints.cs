using BcTelemetryProxy.Mapping;

namespace BcTelemetryProxy.Ingestion;

/// <summary>
/// Exposes the endpoints that make the proxy look like an Azure Application
/// Insights ingestion service to Business Central.
/// </summary>
public static class TrackEndpoints
{
    public static void MapAppInsightsIngestion(this WebApplication app)
    {
        // The real ingestion endpoints BC posts telemetry to.
        app.MapPost("/v2/track", HandleTrackAsync);
        app.MapPost("/v2.1/track", HandleTrackAsync);

        // Health probe for load balancers / smoke tests.
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        // Any other AI control endpoint (Live Metrics/QuickPulse, ping, profile
        // lookups) is answered with 200 so the BC telemetry client does not log
        // ingestion failures. We intentionally do not translate those.
        app.MapMethods("/{**catchAll}", new[] { "GET", "POST" },
            () => Results.Ok(new IngestionResponse(0, 0)));
    }

    private static async Task<IResult> HandleTrackAsync(
        HttpContext context,
        AiToOtelMapper mapper,
        ILogger<AiToOtelMapper> logger)
    {
        var gzip = context.Request.Headers.ContentEncoding
            .Any(v => v is not null && v.Contains("gzip", StringComparison.OrdinalIgnoreCase));

        var envelopes = await EnvelopeReader.ReadAsync(context.Request.Body, gzip, context.RequestAborted);

        var accepted = 0;
        foreach (var envelope in envelopes)
        {
            try
            {
                mapper.Map(envelope);
                accepted++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to translate BC telemetry envelope {Name}", envelope.Name);
            }
        }

        // Application Insights returns 200 with an ingestion summary.
        return Results.Ok(new IngestionResponse(envelopes.Count, accepted));
    }
}

/// <summary>Application-Insights-compatible ingestion response body.</summary>
public sealed record IngestionResponse(int itemsReceived, int itemsAccepted)
{
    public object[] errors { get; init; } = Array.Empty<object>();
}

using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Mapping;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.Ingestion;

/// <summary>
/// Exposes the endpoints that make the proxy look like an Azure Application
/// Insights ingestion service to Business Central.
/// </summary>
public static class TrackEndpoints
{
    public static void MapAppInsightsIngestion(this WebApplication app)
    {
        app.MapPost("/v2/track", HandleTrackAsync);
        app.MapPost("/v2.1/track", HandleTrackAsync);

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
        IOptions<IngestionOptions> ingestionOptions,
        ILogger<AiToOtelMapper> logger)
    {
        var options = ingestionOptions.Value;

        // Reject early when the declared compressed size is already too large.
        if (context.Request.ContentLength is { } length && length > options.MaxCompressedRequestBytes)
        {
            return PayloadTooLarge("Compressed request exceeds the configured limit.");
        }

        var gzip = context.Request.Headers.ContentEncoding
            .Any(v => v is not null && v.Contains("gzip", StringComparison.OrdinalIgnoreCase));

        EnvelopeReadResult read;
        try
        {
            read = await EnvelopeReader.ReadAsync(context.Request.Body, gzip, options, context.RequestAborted);
        }
        catch (Exception ex)
        {
            // A faulty payload must never crash the process.
            logger.LogWarning(ex, "Failed to read ingestion request body");
            return Results.BadRequest(new IngestionResponse(0, 0)
            {
                errors = new object[] { new IngestionError(0, 400, "Malformed request body.") },
            });
        }

        switch (read.Outcome)
        {
            case ReadOutcome.CompressedLimitExceeded:
                return PayloadTooLarge("Compressed request exceeds the configured limit.");
            case ReadOutcome.DecompressedLimitExceeded:
                return PayloadTooLarge("Decompressed payload exceeds the configured limit.");
            case ReadOutcome.TooManyEnvelopes:
                return PayloadTooLarge("Too many envelopes in a single request.");
            case ReadOutcome.InvalidGzip:
                return Results.BadRequest(new IngestionResponse(0, 0)
                {
                    errors = new object[] { new IngestionError(0, 400, "Invalid gzip payload.") },
                });
        }

        var accepted = 0;
        var errors = new List<object>();
        foreach (var item in read.Items)
        {
            switch (item.Status)
            {
                case EnvelopeItemStatus.Malformed:
                    errors.Add(new IngestionError(item.Index, 400, "Envelope could not be parsed."));
                    break;
                case EnvelopeItemStatus.TooLarge:
                    errors.Add(new IngestionError(item.Index, 413, "Envelope exceeds the configured size limit."));
                    break;
                case EnvelopeItemStatus.Parsed:
                    switch (mapper.Map(item.Envelope!))
                    {
                        case MappingStatus.Mapped:
                        case MappingStatus.Unsupported:
                            // Both are forwarded; unsupported types are flagged and counted.
                            accepted++;
                            break;
                        case MappingStatus.Failed:
                            errors.Add(new IngestionError(item.Index, 400, "Envelope could not be translated."));
                            break;
                    }

                    break;
            }
        }

        var response = new IngestionResponse(read.Items.Count, accepted) { errors = errors.ToArray() };
        return errors.Count > 0
            ? Results.Json(response, statusCode: StatusCodes.Status206PartialContent)
            : Results.Ok(response);
    }

    private static IResult PayloadTooLarge(string message)
        => Results.Json(
            new IngestionResponse(0, 0) { errors = new object[] { new IngestionError(0, 413, message) } },
            statusCode: StatusCodes.Status413PayloadTooLarge);
}

/// <summary>Application-Insights-compatible ingestion response body.</summary>
public sealed record IngestionResponse(int itemsReceived, int itemsAccepted)
{
    public object[] errors { get; init; } = Array.Empty<object>();
}

/// <summary>Per-item error, correlated to the envelope index within the batch.</summary>
public sealed record IngestionError(int index, int statusCode, string message);

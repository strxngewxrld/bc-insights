using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Diagnostics;
using BcTelemetryProxy.Export;
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
        // The optional {host}/{instance} prefix identifies the sending BC server
        // instance; it comes from the IngestionEndpoint in BC's connection string,
        // e.g. https://proxy/SRV01/BC290/ -> POST /SRV01/BC290/v2/track.
        foreach (var prefix in new[] { "", "/{instance}", "/{host}/{instance}" })
        {
            app.MapPost(prefix + "/v2/track", HandleTrackAsync);
            app.MapPost(prefix + "/v2.1/track", HandleTrackAsync);
        }

        app.MapGet("/health", (OtlpExporter exporter) =>
        {
            var s = exporter.Statistics;
            return Results.Ok(new
            {
                status = s.IsDegraded ? "degraded" : "ok",
                queueLength = exporter.QueueLength,
                exported = new { logs = s.ExportedLogs, spans = s.ExportedSpans, metrics = s.ExportedMetrics },
                dropped = s.Dropped,
                failed = s.Failed,
                rejected = s.Rejected,
                lastSuccessUtc = s.LastSuccessUtc,
                lastError = s.LastError,
                lastErrorUtc = s.LastErrorUtc,
            });
        });

        // Any other AI control endpoint (Live Metrics/QuickPulse, ping, profile
        // lookups) is answered with 200 so the BC telemetry client does not log
        // ingestion failures. We intentionally do not translate those.
        app.MapMethods("/{**catchAll}", new[] { "GET", "POST" },
            () => Results.Ok(new IngestionResponse(0, 0)));
    }

    private static async Task<IResult> HandleTrackAsync(
        HttpContext context,
        AiToOtelMapper mapper,
        SourceResolver sources,
        OtlpExporter exporter,
        DebugCapture capture,
        IOptions<IngestionOptions> ingestionOptions,
        ILogger<AiToOtelMapper> logger)
    {
        var options = ingestionOptions.Value;

        var routeHost = context.Request.RouteValues["host"] as string;
        var routeInstance = context.Request.RouteValues["instance"] as string;
        if ((routeHost is not null && !SourceResolver.IsValidSegment(routeHost)) ||
            (routeInstance is not null && !SourceResolver.IsValidSegment(routeInstance)))
        {
            return Results.BadRequest(new IngestionResponse(0, 0)
            {
                errors = new object[] { new IngestionError(0, 400, "Invalid host or instance in the URL.") },
            });
        }

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
            read = await EnvelopeReader.ReadAsync(context.Request.Body, gzip, options, context.RequestAborted, capture.Enabled);
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

        // Backpressure: if the export queue cannot take the batch, let BC keep it
        // and retry later (503 is retryable for the Application Insights client).
        if (!exporter.HasCapacity(read.Items.Count))
        {
            logger.LogWarning("Export queue full; refusing {Count} envelope(s) with 503", read.Items.Count);
            return Results.Json(
                new IngestionResponse(read.Items.Count, 0)
                {
                    errors = read.Items.Select(i => (object)new IngestionError(i.Index, 503, "Proxy export queue is full.")).ToArray(),
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var caller = context.Connection.RemoteIpAddress;
        var routeSource = string.Join('/', new[] { routeHost, routeInstance }.Where(v => v is not null));
        var accepted = 0;
        var errors = new List<object>();
        foreach (var item in read.Items)
        {
            if (item.Status != EnvelopeItemStatus.Parsed)
            {
                capture.CaptureEnvelope(routeSource, item.Status.ToString(), item.Raw);
            }

            switch (item.Status)
            {
                case EnvelopeItemStatus.Malformed:
                    errors.Add(new IngestionError(item.Index, 400, "Envelope could not be parsed."));
                    break;
                case EnvelopeItemStatus.TooLarge:
                    errors.Add(new IngestionError(item.Index, 413, "Envelope exceeds the configured size limit."));
                    break;
                case EnvelopeItemStatus.Parsed:
                    var source = sources.Resolve(routeHost, routeInstance, item.Envelope!, caller);
                    var status = mapper.Map(item.Envelope!, source);
                    capture.CaptureEnvelope(source.ServiceInstanceId, status.ToString(), item.Raw);
                    switch (status)
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

        logger.LogDebug("Ingested {Received} envelope(s) from {Source}: {Accepted} accepted, {Errors} error(s)",
            read.Items.Count, routeSource.Length > 0 ? routeSource : caller?.ToString(), accepted, errors.Count);

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

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Diagnostics;
using BcTelemetryProxy.Mapping;
using BcTelemetryProxy.Model;
using BcTelemetryProxy.Support;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.Export;

/// <summary>
/// Buffers translated telemetry in a bounded in-memory queue and exports it in
/// batches as OTLP/HTTP JSON, with gzip, retry (exponential backoff, honouring
/// <c>Retry-After</c>) and backpressure: when the queue is full, ingestion is
/// refused with 503 so Business Central retries instead of the proxy dropping data.
/// </summary>
public sealed class OtlpExporter : BackgroundService, IOtelEmitter
{
    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    private readonly Channel<object> _queue;
    private readonly OtlpOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<OtlpExporter> _logger;
    private readonly Uri _logsUri;
    private readonly Uri _tracesUri;
    private readonly Uri _metricsUri;
    private readonly ExportStatistics _stats = new();
    private readonly DebugCapture? _capture;
    private readonly PerformanceRegistry? _performance;

    public OtlpExporter(
        IOptions<OtlpOptions> options,
        ILogger<OtlpExporter> logger,
        HttpMessageHandler? handler = null,
        DebugCapture? capture = null,
        PerformanceRegistry? performance = null)
    {
        _options = options.Value;
        _logger = logger;
        _capture = capture;
        _performance = performance;
        _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(_options.MaxQueueSize)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
        };
        foreach (var (name, value) in _options.Headers)
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }

        var baseUri = _options.Endpoint.TrimEnd('/');
        _logsUri = new Uri($"{baseUri}/v1/logs");
        _tracesUri = new Uri($"{baseUri}/v1/traces");
        _metricsUri = new Uri($"{baseUri}/v1/metrics");
    }

    public ExportStatistics Statistics => _stats;

    public int QueueLength => _queue.Reader.Count;

    /// <summary>True when <paramref name="count"/> more items fit into the queue.</summary>
    public bool HasCapacity(int count) => _queue.Reader.Count + count <= _options.MaxQueueSize;

    public void EmitLog(LogItem log) => Enqueue(log);

    public void EmitSpan(SpanItem span)
    {
        _performance?.Record(span);
        Enqueue(span);
    }

    public void EmitMetric(MetricItem metric) => Enqueue(metric);

    private void Enqueue(object item)
    {
        if (!_queue.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _stats.Dropped);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<object>(_options.MaxBatchSize);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                Drain(batch);
                if (batch.Count < _options.MaxBatchSize)
                {
                    // Give a burst a moment to complete so requests stay large.
                    await Task.Delay(_options.BatchDelayMilliseconds, stoppingToken);
                    Drain(batch);
                }

                await SendAsync(batch, retry: true, stoppingToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; StopAsync flushes what is left.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // Final best-effort flush: one attempt per batch, bounded by the host's shutdown timeout.
        var batch = new List<object>(_options.MaxBatchSize);
        try
        {
            while (_queue.Reader.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                Drain(batch);
                await SendAsync(batch, retry: false, cancellationToken);
                batch.Clear();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or AggregateException)
        {
            // Shutdown must not fail; logging providers may already be disposed.
            return;
        }

        var left = _queue.Reader.Count + batch.Count;
        if (left > 0)
        {
            _logger.LogWarning("Shutdown: {Count} telemetry items were not exported", left);
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    private void Drain(List<object> batch)
    {
        while (batch.Count < _options.MaxBatchSize && _queue.Reader.TryRead(out var item))
        {
            batch.Add(item);
        }
    }

    private async Task SendAsync(List<object> batch, bool retry, CancellationToken ct)
    {
        var observed = DateTime.UtcNow;
        var logs = batch.OfType<LogItem>().ToList();
        var spans = batch.OfType<SpanItem>().ToList();
        var metrics = batch.OfType<MetricItem>().ToList();

        if (logs.Count > 0)
        {
            await ExportAsync("logs", _logsUri, logs.Count, w => OtlpJsonSerializer.WriteLogs(w, logs, observed), retry, ct);
        }

        if (spans.Count > 0)
        {
            await ExportAsync("traces", _tracesUri, spans.Count, w => OtlpJsonSerializer.WriteSpans(w, spans), retry, ct);
        }

        if (metrics.Count > 0)
        {
            await ExportAsync("metrics", _metricsUri, metrics.Count, w => OtlpJsonSerializer.WriteMetrics(w, metrics), retry, ct);
        }
    }

    private async Task ExportAsync(string signal, Uri uri, int count, Action<Utf8JsonWriter> write, bool retry, CancellationToken ct)
    {
        var payload = Serialize(write);
        _capture?.CaptureExport(signal, write);
        var deadline = DateTime.UtcNow.AddSeconds(_options.MaxRetrySeconds);
        var delay = TimeSpan.FromSeconds(1);

        for (var attempt = 1; ; attempt++)
        {
            TimeSpan? retryAfter = null;
            string error;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(payload) };
                request.Content.Headers.ContentType = JsonContentType;
                if (_options.Compression)
                {
                    request.Content.Headers.ContentEncoding.Add("gzip");
                }

                using var response = await _http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    var rejected = await HandlePartialSuccessAsync(signal, response, ct);
                    _stats.RecordSuccess(signal, count - (int)Math.Clamp(rejected, 0, count), healthy: rejected == 0);
                    return;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                error = $"HTTP {(int)response.StatusCode}: {Truncate(body)}";
                if (!IsRetryable(response.StatusCode))
                {
                    Fail(signal, count, error, retried: false);
                    return;
                }

                retryAfter = response.Headers.RetryAfter?.Delta;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                error = ex.Message;
            }

            _stats.RecordError($"{signal}: {error}", signal);
            var wait = retryAfter is { } ra && ra < TimeSpan.FromMinutes(1) ? ra : delay;
            if (!retry || DateTime.UtcNow + wait > deadline)
            {
                Fail(signal, count, error, retried: retry);
                return;
            }

            _logger.LogWarning("OTLP {Signal} export attempt {Attempt} failed ({Error}); retrying in {Delay}s",
                signal, attempt, error, wait.TotalSeconds);
            await Task.Delay(wait, ct);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
    }

    private byte[] Serialize(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (Stream target = _options.Compression ? new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true) : buffer)
        using (var writer = new Utf8JsonWriter(target, OtlpJson.WriterOptions))
        {
            write(writer);
        }

        return buffer.ToArray();
    }

    private async Task<long> HandlePartialSuccessAsync(string signal, HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
        {
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            if (!doc.RootElement.TryGetProperty("partialSuccess", out var partial) || partial.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            long rejected = 0;
            foreach (var key in new[] { "rejectedLogRecords", "rejectedSpans", "rejectedDataPoints" })
            {
                if (partial.TryGetProperty(key, out var v))
                {
                    rejected += v.ValueKind == JsonValueKind.String ? long.Parse(v.GetString()!) : v.GetInt64();
                }
            }

            var message = partial.TryGetProperty("errorMessage", out var m) ? m.GetString() : null;
            if (rejected > 0 || !string.IsNullOrEmpty(message))
            {
                Interlocked.Add(ref _stats.Rejected, rejected);
                _stats.RecordError($"{signal}: backend rejected {rejected} item(s): {message}", signal);
                _logger.LogWarning("OTLP {Signal} partial success: {Rejected} rejected ({Message})", signal, rejected, message);
            }

            return rejected > 0 ? rejected : !string.IsNullOrEmpty(message) ? -1 : 0;
        }

        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            // A non-standard success body is not an export failure.
        }
        return 0;
    }

    private void Fail(string signal, int count, string error, bool retried)
    {
        Interlocked.Add(ref _stats.Failed, count);
        _stats.RecordError($"{signal}: {error}", signal);
        _logger.LogError("OTLP {Signal} export failed{Retried}, dropping {Count} item(s): {Error}",
            signal, retried ? " after retries" : string.Empty, count, error);
    }

    private static bool IsRetryable(HttpStatusCode status)
        => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "…";
}

/// <summary>Export counters surfaced on <c>/health</c>.</summary>
public sealed class ExportStatistics
{
    private readonly object _lock = new();
    private readonly HashSet<string> _unhealthySignals = new(StringComparer.Ordinal);
    public long Dropped;
    public long Failed;
    public long Rejected;
    public long ExportedLogs;
    public long ExportedSpans;
    public long ExportedMetrics;

    public string? LastError { get; private set; }

    public DateTime? LastErrorUtc { get; private set; }

    public DateTime? LastSuccessUtc { get; private set; }

    /// <summary>True until every failed signal has subsequently exported without a rejection.</summary>
    public bool IsDegraded { get { lock (_lock) { return _unhealthySignals.Count > 0; } } }

    public void RecordError(string error, string signal)
    {
        lock (_lock)
        {
            _unhealthySignals.Add(signal);
            LastError = error;
            LastErrorUtc = DateTime.UtcNow;
        }
    }

    public void RecordSuccess(string signal, int count, bool healthy = true)
    {
        switch (signal)
        {
            case "logs":
                Interlocked.Add(ref ExportedLogs, count);
                break;
            case "traces":
                Interlocked.Add(ref ExportedSpans, count);
                break;
            default:
                Interlocked.Add(ref ExportedMetrics, count);
                break;
        }

        if (healthy)
        {
            lock (_lock)
            {
                _unhealthySignals.Remove(signal);
                LastSuccessUtc = DateTime.UtcNow;
            }
        }
    }
}

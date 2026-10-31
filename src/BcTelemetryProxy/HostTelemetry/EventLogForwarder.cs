using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using System.Xml.Linq;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Ingestion;
using BcTelemetryProxy.Mapping;
using BcTelemetryProxy.Model;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.HostTelemetry;

/// <summary>
/// Forwards new Windows Event Log entries of the configured channels (by default
/// the BC server's Admin channel, warnings and worse) as OTLP logs. Entries that
/// name a BC server instance (<c>serverInstanceName</c>) land on that instance's
/// resource, so they appear next to its telemetry and metrics.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EventLogForwarder : IHostedService, IDisposable
{
    private const int MaxBodyLength = 16 * 1024;
    private const int MaxValueLength = 4 * 1024;

    private readonly HostTelemetryOptions _options;
    private readonly IOtelEmitter _emitter;
    private readonly SourceResolver _sources;
    private readonly AttributeMapper _attributes;
    private readonly ILogger<EventLogForwarder> _logger;
    private readonly List<EventLogWatcher> _watchers = new();

    public EventLogForwarder(
        IOptions<HostTelemetryOptions> options,
        IOtelEmitter emitter,
        SourceResolver sources,
        AttributeMapper attributes,
        ILogger<EventLogForwarder> logger)
    {
        _options = options.Value;
        _emitter = emitter;
        _sources = sources;
        _attributes = attributes;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !_options.EventLog)
        {
            return Task.CompletedTask;
        }

        var maxLevel = Math.Clamp(_options.EventLogMaxLevel, 1, 5);
        // Configuration binding appends list entries to the defaults; subscribe once per channel.
        foreach (var channel in _options.EventLogChannels.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var query = new EventLogQuery(channel, PathType.LogName, $"*[System[(Level>=1 and Level<={maxLevel})]]");
                var watcher = new EventLogWatcher(query);
                watcher.EventRecordWritten += (_, e) => OnEvent(channel, e);
                watcher.Enabled = true;
                _watchers.Add(watcher);
                _logger.LogInformation("Forwarding Windows Event Log channel {Channel} (level <= {Level})", channel, maxLevel);
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Cannot subscribe to Windows Event Log channel {Channel}", channel);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var watcher in _watchers)
        {
            watcher.Enabled = false;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
    }

    private void OnEvent(string channel, EventRecordWrittenEventArgs e)
    {
        using var record = e.EventRecord;
        if (record is null)
        {
            return;
        }

        try
        {
            var data = EventData(record);
            data.TryGetValue("serverInstanceName", out var instance);
            var message = Describe(record) ?? (data.TryGetValue("message", out var m) ? m : null);

            var attributes = new List<KeyValuePair<string, object?>>
            {
                new("windows.event_log.channel", channel),
                new("windows.event_log.provider", record.ProviderName),
                new("windows.event_log.event_id", (long)record.Id),
                new("windows.event_log.record_id", record.RecordId),
            };

            foreach (var (key, value) in data)
            {
                if (key is not ("message" or "serverInstanceName") && !Envelope.IsPlaceholder(value))
                {
                    attributes.Add(new($"windows.event_log.data.{key}", Truncate(value, MaxValueLength)));
                }
            }

            _emitter.EmitLog(new LogItem(
                _sources.Local(string.IsNullOrWhiteSpace(instance) ? null : instance),
                record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow,
                Level(record.Level),
                Truncate(message?.Trim() ?? $"Event {record.Id}", MaxBodyLength),
                $"windows.event_log.{record.Id}",
                null,
                null,
                _attributes.Filter(attributes)));
        }
        catch (Exception ex) when (ex is EventLogException or System.Xml.XmlException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to forward Windows event from {Channel}", channel);
        }
    }

    private static string? Describe(EventRecord record)
    {
        try
        {
            var text = record.FormatDescription();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (EventLogException)
        {
            return null;
        }
    }

    /// <summary>Named <c>EventData/Data</c> values (BC puts its fields there).</summary>
    private static Dictionary<string, string> EventData(EventRecord record)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var xml = XDocument.Parse(record.ToXml());
        foreach (var data in xml.Descendants().Where(d => d.Name.LocalName == "Data"))
        {
            var name = data.Attribute("Name")?.Value;
            if (!string.IsNullOrEmpty(name))
            {
                result[name] = data.Value;
            }
        }

        return result;
    }

    private static LogLevel Level(byte? level) => level switch
    {
        1 => LogLevel.Critical,
        2 => LogLevel.Error,
        3 => LogLevel.Warning,
        5 => LogLevel.Debug,
        _ => LogLevel.Information,
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Model;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.Support;

/// <summary>One occurrence of an error, as kept for support reports.</summary>
public sealed record ErrorSample(
    DateTime TimeUtc,
    string ServiceInstanceId,
    string? ServiceVersion,
    string? TraceId,
    IReadOnlyDictionary<string, string> Details);

/// <summary>An error kind (same fingerprint) with counts and the latest samples.</summary>
public sealed record ErrorSummary(
    string Fingerprint,
    string Type,
    string? Message,
    string? EventId,
    string ServiceName,
    long Count,
    int Users,
    int Sessions,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    IReadOnlyList<string> Instances,
    IReadOnlyList<string> Versions,
    string? StackTrace,
    IReadOnlyList<ErrorSample> Samples);

/// <summary>
/// Groups errors (failed BC operations, SQL deadlocks) by fingerprint in memory
/// so the proxy can list the important ones and write a ready-to-send support
/// report. Holds only attributes that already passed the privacy filter; bounded
/// by <see cref="SupportOptions.MaxErrorKinds"/> and reset on restart.
/// </summary>
public sealed class ErrorRegistry
{
    private const int MaxTracked = 1000;

    private readonly SupportOptions _options;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public ErrorRegistry(IOptions<SupportOptions>? options = null)
        => _options = options?.Value ?? new SupportOptions();

    /// <summary>
    /// Stable id of an error kind: event, type, AL object, extension and the message
    /// without ids/numbers. Equal errors in different instances share it.
    /// </summary>
    public static string Fingerprint(params string?[] parts)
    {
        var text = string.Join('|', parts.Select(p => p?.Trim() ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 6)).ToLowerInvariant();
    }

    /// <summary>Records an occurrence; returns true when the fingerprint is new.</summary>
    public bool Record(
        TelemetrySource source,
        string fingerprint,
        string type,
        string? message,
        string? eventId,
        DateTime timeUtc,
        ActivityTraceId? traceId,
        IReadOnlyList<KeyValuePair<string, object?>> attributes,
        string? stackTrace = null)
    {
        lock (_lock)
        {
            var isNew = !_entries.TryGetValue(fingerprint, out var entry);
            if (isNew)
            {
                if (_entries.Count >= Math.Max(1, _options.MaxErrorKinds))
                {
                    _entries.Remove(_entries.MinBy(e => e.Value.LastSeenUtc).Key);
                }

                entry = new Entry(fingerprint, type, message, eventId, source.ServiceName, timeUtc);
                _entries[fingerprint] = entry;
            }

            entry!.Add(source, timeUtc, traceId, attributes, stackTrace, Math.Max(1, _options.SamplesPerError));
            return isNew;
        }
    }

    /// <summary>Error kinds seen in the window, most important first (users, then count).</summary>
    public IReadOnlyList<ErrorSummary> List(TimeSpan window, DateTime? nowUtc = null)
    {
        var since = (nowUtc ?? DateTime.UtcNow) - window;
        lock (_lock)
        {
            return _entries.Values
                .Where(e => e.LastSeenUtc >= since)
                .Select(e => e.ToSummary())
                .OrderByDescending(e => e.Users)
                .ThenByDescending(e => e.Count)
                .ThenByDescending(e => e.LastSeenUtc)
                .ToList();
        }
    }

    public ErrorSummary? Find(string fingerprint)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(fingerprint, out var e) ? e.ToSummary() : null;
        }
    }

    private sealed class Entry(string fingerprint, string type, string? message, string? eventId, string serviceName, DateTime firstSeenUtc)
    {
        private readonly HashSet<string> _users = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sessions = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _instances = new(StringComparer.OrdinalIgnoreCase);
        private readonly SortedSet<string> _versions = new(StringComparer.Ordinal);
        private readonly Queue<ErrorSample> _samples = new();
        private long _count;
        private string? _stackTrace;

        private readonly DateTime _firstSeenUtc = firstSeenUtc;

        public DateTime LastSeenUtc { get; private set; } = firstSeenUtc;

        public void Add(TelemetrySource source, DateTime timeUtc, ActivityTraceId? traceId,
            IReadOnlyList<KeyValuePair<string, object?>> attributes, string? stackTrace, int maxSamples)
        {
            _count++;
            LastSeenUtc = timeUtc > LastSeenUtc ? timeUtc : LastSeenUtc;
            _instances.Add(source.ServiceInstanceId);
            if (source.ServiceVersion is { } version)
            {
                _versions.Add(version);
            }

            var details = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in attributes)
            {
                if (value is not null && !details.ContainsKey(key))
                {
                    details[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                }
            }

            Track(_users, details.GetValueOrDefault("enduser.id"));
            Track(_sessions, details.GetValueOrDefault("bc.session.id"));
            _stackTrace = stackTrace ?? _stackTrace;

            _samples.Enqueue(new ErrorSample(timeUtc, source.ServiceInstanceId, source.ServiceVersion, traceId?.ToHexString(), details));
            while (_samples.Count > maxSamples)
            {
                _samples.Dequeue();
            }
        }

        private static void Track(HashSet<string> set, string? value)
        {
            if (value is not null && set.Count < MaxTracked)
            {
                set.Add(value);
            }
        }

        public ErrorSummary ToSummary() => new(
            fingerprint, type, message, eventId, serviceName, _count, _users.Count, _sessions.Count,
            _firstSeenUtc, LastSeenUtc, _instances.ToList(), _versions.ToList(), _stackTrace,
            _samples.Reverse().ToList());
    }
}

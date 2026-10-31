using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Model;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.Mapping;

/// <summary>
/// Builds the attribute set for a telemetry item and applies the configured
/// privacy processing (allowlist / denylist / redaction / hashing) before the
/// attributes leave the proxy.
///
/// Well-known BC dimensions are exported under OpenTelemetry semantic-convention
/// or BC-specific <c>bc.*</c> keys; all other dimensions keep their BC names.
/// </summary>
public sealed class AttributeMapper
{
    private readonly HashSet<string> _allowed;
    private readonly HashSet<string> _denied;
    private readonly HashSet<string> _redacted;
    private readonly HashSet<string> _hashed;
    private readonly string _redactionMarker;

    public AttributeMapper(IOptions<TelemetryPrivacyOptions> options)
    {
        var o = options.Value;
        _allowed = ToSet(o.AllowedAttributes);
        _denied = ToSet(o.DeniedAttributes);
        _redacted = ToSet(o.RedactedAttributes);
        _hashed = ToSet(o.HashedAttributes);
        _redactionMarker = o.RedactionMarker;
    }

    /// <summary>
    /// Adds context attributes derived from the AI envelope tags. Host and server
    /// instance are resource attributes (see <c>SourceResolver</c>) and are not
    /// repeated on every record.
    /// </summary>
    public static void AddContext(List<KeyValuePair<string, object?>> target, Envelope env)
    {
        Add(target, "enduser.id", env.UserId);
        Add(target, "bc.operation.id", env.OperationId);
        Add(target, "bc.operation.name", env.OperationName);
        Add(target, "bc.session.id", env.SessionId);
    }

    /// <summary>
    /// Adds the custom dimensions. Well-known dimensions are renamed to
    /// <c>bc.*</c> / semantic-convention keys (one key per value, no duplicates);
    /// all others keep their original BC names. BC durations ("hh:mm:ss.fffffff")
    /// become numeric <c>&lt;key&gt;InMs</c> attributes, counters become integers and
    /// "True"/"False" become booleans, so backends can aggregate and filter on them.
    /// </summary>
    public static void AddCustomProperties(List<KeyValuePair<string, object?>> target, JsonElement data)
    {
        var properties = data.EnumerateCustomProperties().ToList();
        var keys = new HashSet<string>(properties.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
        var written = new HashSet<string>(target.Select(a => a.Key), StringComparer.Ordinal);

        foreach (var (key, value) in properties)
        {
            if (Envelope.IsPlaceholder(value) || DroppedKeys.Contains(key))
            {
                continue;
            }

            if (value!.Contains(':') &&
                TimeSpan.TryParseExact(value, "c", CultureInfo.InvariantCulture, out var duration))
            {
                // Keep BC's own numeric companion when it sends one.
                if (!keys.Contains(key + "InMs"))
                {
                    AddOnce(target, written, key + "InMs", duration.TotalMilliseconds);
                }

                continue;
            }

            if (key.Equals("httpStatusCode", StringComparison.OrdinalIgnoreCase))
            {
                if (written.Add("http.response.status_code"))
                {
                    AddStatusCode(target, value);
                }

                continue;
            }

            var name = PromotedKeys.TryGetValue(key, out var promoted) ? promoted : key;
            AddOnce(target, written, name, TypedValue(key, value));
        }
    }

    /// <summary>Adds <c>http.response.status_code</c> as an integer (semantic convention type).</summary>
    public static void AddStatusCode(List<KeyValuePair<string, object?>> target, string? value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            target.Add(new("http.response.status_code", code));
        }
        else
        {
            Add(target, "http.response.status_code", value);
        }
    }

    /// <summary>
    /// Known numeric BC dimensions (counters and <c>*InMs</c> durations) are
    /// exported as numbers and "True"/"False" as booleans; everything else stays
    /// a string so a key never changes type between records.
    /// </summary>
    private static object TypedValue(string key, string value)
    {
        if (value is "True" or "False")
        {
            return value == "True";
        }

        var numeric = key.EndsWith("InMs", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("Count", StringComparison.Ordinal)
            || NumericKeys.Contains(key);
        if (!numeric)
        {
            return value;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return l;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : value;
    }

    private static void AddOnce(List<KeyValuePair<string, object?>> target, HashSet<string> written, string key, object value)
    {
        if (written.Add(key))
        {
            target.Add(new(key, value));
        }
    }

    private static readonly HashSet<string> NumericKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "sqlExecutes", "sqlRowsRead", "sqlStatementsExecuted", "totalRows", "rowsAffected",
        "httpStatusCode", "numberOfJoins", "numberOfRecords", "retryCount", "alStackDepth",
        "attemptNumber", "alJobQueueNumberOfAttemptsToRun", "alJobQueueMaxNumberOfAttemptsToRun",
    };

    /// <summary>BC dimension -> exported key. Privacy rules match either name.</summary>
    private static readonly Dictionary<string, string> PromotedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["companyName"] = "bc.company.name",
        ["aadTenantId"] = "bc.tenant.id",
        ["tenantId"] = "bc.tenant.id",
        ["environmentName"] = "bc.environment",
        ["environmentType"] = "bc.environment.type",
        ["componentVersion"] = "bc.version",
        ["serverExecutableVersion"] = "bc.version",
        ["clientType"] = "bc.client_type",
        ["httpStatusCode"] = "http.response.status_code",
    };

    /// <summary>Exported elsewhere: <c>eventId</c> is the record's <c>event.name</c>.</summary>
    private static readonly HashSet<string> DroppedKeys = new(StringComparer.OrdinalIgnoreCase) { "eventId" };

    private static readonly Dictionary<string, string[]> OriginalKeys = PromotedKeys
        .GroupBy(p => p.Value)
        .ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToArray());

    /// <summary>
    /// Applies the configured privacy rules. Returns a new list containing only
    /// the attributes that may be exported, with redacted/hashed values applied.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, object?>> Filter(IReadOnlyList<KeyValuePair<string, object?>> attributes)
    {
        var result = new List<KeyValuePair<string, object?>>(attributes.Count);
        foreach (var attribute in attributes)
        {
            if (Matches(_denied, attribute.Key))
            {
                continue;
            }

            if (_allowed.Count > 0 && !Matches(_allowed, attribute.Key))
            {
                continue;
            }

            if (Matches(_redacted, attribute.Key))
            {
                result.Add(new(attribute.Key, _redactionMarker));
            }
            else if (Matches(_hashed, attribute.Key))
            {
                result.Add(new(attribute.Key, Hash(attribute.Value)));
            }
            else
            {
                result.Add(attribute);
            }
        }

        return result;
    }

    /// <summary>
    /// A rule matches the exported key, the original BC dimension it was renamed
    /// from, or (for <c>&lt;key&gt;InMs</c> durations) the original duration key.
    /// </summary>
    private static bool Matches(HashSet<string> rules, string key)
    {
        if (rules.Count == 0)
        {
            return false;
        }

        if (rules.Contains(key) ||
            (OriginalKeys.TryGetValue(key, out var originals) && originals.Any(rules.Contains)))
        {
            return true;
        }

        return key.EndsWith("InMs", StringComparison.Ordinal) && rules.Contains(key[..^4]);
    }

    private static void Add(List<KeyValuePair<string, object?>> target, string key, string? value)
    {
        if (!Envelope.IsPlaceholder(value))
        {
            target.Add(new(key, value));
        }
    }

    private static string Hash(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static HashSet<string> ToSet(IEnumerable<string> values)
        => new(values, StringComparer.OrdinalIgnoreCase);
}

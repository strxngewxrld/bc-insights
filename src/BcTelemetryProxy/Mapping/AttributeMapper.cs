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
/// Standardised OpenTelemetry semantic-convention attributes and BC-specific
/// <c>bc.*</c> attributes are emitted side by side and complement each other.
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

    /// <summary>Adds context attributes derived from the AI envelope tags.</summary>
    public static void AddContext(List<KeyValuePair<string, object?>> target, Envelope env)
    {
        // Standardised semantic conventions.
        Add(target, "service.instance.id", env.CloudRoleInstance);
        Add(target, "enduser.id", env.UserId);

        // BC-specific namespace (complements, does not replace, the above).
        Add(target, "bc.server_instance", env.CloudRole);
        Add(target, "bc.operation.id", env.OperationId);
        Add(target, "bc.operation.name", env.OperationName);
        Add(target, "bc.session.id", env.SessionId);
    }

    /// <summary>
    /// Adds the custom dimensions under their original keys and promotes a known
    /// subset to <c>bc.*</c> / semantic-convention attributes.
    /// </summary>
    public static void AddCustomProperties(List<KeyValuePair<string, object?>> target, JsonElement data)
    {
        foreach (var (key, value) in data.EnumerateCustomProperties())
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            Add(target, key, value);

            switch (key)
            {
                case "companyName":
                    Add(target, "bc.company.name", value);
                    break;
                case "aadTenantId":
                case "tenantId":
                    Add(target, "bc.tenant.id", value);
                    break;
                case "environmentName":
                    Add(target, "bc.environment", value);
                    break;
                case "environmentType":
                    Add(target, "bc.environment.type", value);
                    break;
                case "componentVersion":
                case "serverExecutableVersion":
                    Add(target, "bc.version", value);
                    break;
                case "clientType":
                    Add(target, "bc.client_type", value);
                    break;
                case "httpStatusCode":
                    Add(target, "http.response.status_code", value);
                    break;
            }
        }
    }

    /// <summary>
    /// Applies the configured privacy rules. Returns a new list containing only
    /// the attributes that may be exported, with redacted/hashed values applied.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, object?>> Filter(IReadOnlyList<KeyValuePair<string, object?>> attributes)
    {
        var result = new List<KeyValuePair<string, object?>>(attributes.Count);
        foreach (var attribute in attributes)
        {
            if (_denied.Contains(attribute.Key))
            {
                continue;
            }

            if (_allowed.Count > 0 && !_allowed.Contains(attribute.Key))
            {
                continue;
            }

            if (_redacted.Contains(attribute.Key))
            {
                result.Add(new(attribute.Key, _redactionMarker));
            }
            else if (_hashed.Contains(attribute.Key))
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

    private static void Add(List<KeyValuePair<string, object?>> target, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
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

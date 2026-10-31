using System.Collections.Concurrent;
using System.Net;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.HostTelemetry;
using BcTelemetryProxy.Mapping;
using BcTelemetryProxy.Model;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.Ingestion;

/// <summary>
/// Determines the <see cref="TelemetrySource"/> (OTLP resource) of an envelope.
///
/// Host:     URL segment &gt; <c>ai.cloud.roleInstance</c> &gt; <c>ai.internal.nodeName</c>
///           (first DNS label) &gt; configured HostName &gt; caller address (the proxy's machine name for loopback callers).
/// Instance: URL segment &gt; configured BcServerInstance.
///
/// The recommended setup encodes both in the BC connection string, e.g.
/// <c>IngestionEndpoint=https://proxy:8443/SRV01/BC290/</c>, which makes BC post to
/// <c>/SRV01/BC290/v2/track</c>.
/// </summary>
public sealed class SourceResolver
{
    private const int MaxSegmentLength = 64;

    private readonly ResourceOptions _options;
    private readonly IReadOnlyDictionary<string, string> _attributes;
    private readonly Dictionary<SourceOverride, IReadOnlyDictionary<string, string>> _overrideAttributes = new();

    // Last BC version seen per "host/instance", so metrics the proxy collects
    // itself land on the same resource (incl. service.version) as the telemetry.
    private readonly ConcurrentDictionary<string, string> _versions = new(StringComparer.OrdinalIgnoreCase);

    // Database attributes of local instances, merged once per base attribute set
    // (shared dictionaries keep TelemetrySource equality cheap).
    private readonly BcInstanceCatalog _catalog;
    private readonly ConcurrentDictionary<(IReadOnlyDictionary<string, string> Base, string Instance), IReadOnlyDictionary<string, string>> _instanceAttributes = new();
    private readonly ConcurrentDictionary<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>> _databaseAttributes = new();

    public SourceResolver(IOptions<ResourceOptions> options, BcInstanceCatalog? catalog = null)
    {
        _options = options.Value;
        _catalog = catalog ?? BcInstanceCatalog.Empty;

        // JSON null in appsettings binds as "" — treat empty values as unset.
        _options.HostName = NullIfEmpty(_options.HostName);
        _options.BcServerInstance = NullIfEmpty(_options.BcServerInstance);
        _options.DeploymentEnvironment = NullIfEmpty(_options.DeploymentEnvironment);
        foreach (var o in _options.Sources.Values)
        {
            o.ServiceName = NullIfEmpty(o.ServiceName);
            o.DeploymentEnvironment = NullIfEmpty(o.DeploymentEnvironment);
        }

        _attributes = new Dictionary<string, string>(_options.Attributes, StringComparer.Ordinal);

        // Merged once per override, so every source of one configuration shares
        // the same dictionary instance (keeps TelemetrySource equality cheap).
        foreach (var o in _options.Sources.Values)
        {
            var merged = new Dictionary<string, string>(_options.Attributes, StringComparer.Ordinal);
            foreach (var (key, value) in o.Attributes)
            {
                merged[key] = value;
            }

            _overrideAttributes[o] = merged;
        }
    }

    /// <summary>Host name of the machine the proxy runs on (configured or actual).</summary>
    public string LocalHostName => _options.HostName ?? Environment.MachineName;

    /// <summary>Source for telemetry the proxy collects itself on this machine.</summary>
    public TelemetrySource Local(string? instance)
    {
        var source = Create(LocalHostName, instance);
        return instance is not null && _versions.TryGetValue(source.ServiceInstanceId, out var version)
            ? source with { ServiceVersion = version }
            : source;
    }

    /// <summary>
    /// Source of the database tier (<c>db.system</c> = mssql), e.g. for SQL Server
    /// monitoring: <c>service.instance.id</c> = <c>host/sqlInstance</c>.
    /// </summary>
    public TelemetrySource Database(string serviceName, string host, string sqlInstance, string? version)
    {
        var match = FindOverride(host, null);
        var baseAttributes = match is null ? _attributes : _overrideAttributes[match];
        return new TelemetrySource(serviceName, _options.ServiceNamespace, host, sqlInstance,
            match?.DeploymentEnvironment ?? _options.DeploymentEnvironment)
        {
            Attributes = _databaseAttributes.GetOrAdd(baseAttributes, a => new Dictionary<string, string>(a, StringComparer.Ordinal) { ["db.system"] = "mssql" }),
            ServiceVersion = version,
        };
    }

    /// <summary>Only letters, digits, '.', '-' and '_' — URL segments end up as resource attributes.</summary>
    public static bool IsValidSegment(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxSegmentLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    public TelemetrySource Resolve(string? routeHost, string? routeInstance, Envelope envelope, IPAddress? caller)
    {
        var host = routeHost
            ?? HostLabel(envelope.CloudRoleInstance)
            ?? HostLabel(envelope.NodeName)
            ?? _options.HostName
            ?? CallerHost(caller);
        var instance = routeInstance ?? _options.BcServerInstance;
        var source = Create(host, instance);

        var version = envelope.Data?.BaseData.GetCustomProperty("componentVersion");
        if (Envelope.IsPlaceholder(version))
        {
            return _versions.TryGetValue(source.ServiceInstanceId, out var known) ? source with { ServiceVersion = known } : source;
        }

        _versions[source.ServiceInstanceId] = version!;
        return source with { ServiceVersion = version };
    }

    private TelemetrySource Create(string? host, string? instance)
    {
        var match = FindOverride(host, instance);
        var attributes = match is null ? _attributes : _overrideAttributes[match];
        return new TelemetrySource(
            match?.ServiceName ?? _options.ServiceName,
            _options.ServiceNamespace,
            host,
            instance,
            match?.DeploymentEnvironment ?? _options.DeploymentEnvironment)
        {
            Attributes = WithDatabase(attributes, host, instance),
        };
    }

    /// <summary>
    /// Instances on this machine carry their database (<c>bc.database.server</c>,
    /// <c>bc.database.name</c>) unless configured explicitly.
    /// </summary>
    private IReadOnlyDictionary<string, string> WithDatabase(IReadOnlyDictionary<string, string> attributes, string? host, string? instance)
    {
        if (instance is null || attributes.ContainsKey("bc.database.server") ||
            !string.Equals(host, LocalHostName, StringComparison.OrdinalIgnoreCase) ||
            _catalog.Find(instance) is not { } db)
        {
            return attributes;
        }

        return _instanceAttributes.GetOrAdd((attributes, instance), key =>
        {
            var merged = new Dictionary<string, string>(key.Base, StringComparer.Ordinal) { ["bc.database.server"] = db.DisplayName };
            if (db.Name is not null)
            {
                merged["bc.database.name"] = db.Name;
            }

            return merged;
        });
    }

    private SourceOverride? FindOverride(string? host, string? instance)
    {
        if (_options.Sources.Count == 0)
        {
            return null;
        }

        if (host is not null && instance is not null && _options.Sources.TryGetValue($"{host}/{instance}", out var exact))
        {
            return exact;
        }

        if (instance is not null && _options.Sources.TryGetValue(instance, out var byInstance))
        {
            return byInstance;
        }

        return host is not null && _options.Sources.TryGetValue(host, out var byHost) ? byHost : null;
    }

    private static string? CallerHost(IPAddress? caller)
    {
        if (caller is { IsIPv4MappedToIPv6: true })
        {
            caller = caller.MapToIPv4();
        }

        return caller is null || IPAddress.IsLoopback(caller) ? Environment.MachineName : caller.ToString();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>"SRV01.corp.local" -> "SRV01", so tag-derived hosts match the URL convention.</summary>
    private static string? HostLabel(string? value)
    {
        var label = value?.Split('.', 2)[0];
        return IsValidSegment(label) ? label : null;
    }
}

using System.Net;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Model;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.Ingestion;

/// <summary>
/// Determines the <see cref="TelemetrySource"/> (OTLP resource) of an envelope.
///
/// Host:     URL segment &gt; <c>ai.cloud.roleInstance</c> &gt; configured HostName
///           &gt; caller address (the proxy's machine name for loopback callers).
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

    public SourceResolver(IOptions<ResourceOptions> options) => _options = options.Value;

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
            ?? NullIfEmpty(envelope.CloudRoleInstance)
            ?? _options.HostName
            ?? CallerHost(caller);
        var instance = routeInstance ?? _options.BcServerInstance;

        var match = FindOverride(host, instance);
        return new TelemetrySource(
            match?.ServiceName ?? _options.ServiceName,
            _options.ServiceNamespace,
            host,
            instance,
            match?.DeploymentEnvironment ?? _options.DeploymentEnvironment);
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
}

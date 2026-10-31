using System.Xml.Linq;
using Microsoft.Win32;

namespace BcTelemetryProxy.HostTelemetry;

/// <summary>Database of a BC server instance, from its <c>CustomSettings.config</c>.</summary>
/// <param name="Server">Server for connections as configured (<c>host[\instance][,port]</c>).</param>
/// <param name="Host">Database host; <c>localhost</c>/<c>.</c> become the machine name.</param>
/// <param name="Name">Database name.</param>
public sealed record BcDatabase(string Server, string Host, string? Name)
{
    /// <summary><c>host\instance</c> for display, e.g. <c>SQL01\BC</c>.</summary>
    public string DisplayName => Server.Contains('\\') ? $"{Host}\\{Server.Split('\\', 2)[1].Split(',')[0]}" : Host;

    public static BcDatabase Create(string server, string? sqlInstance, string? name)
    {
        var full = string.IsNullOrWhiteSpace(sqlInstance) ? server : $"{server}\\{sqlInstance}";
        var host = full.Split('\\', ',')[0].Trim();
        if (host is "." or "(local)" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            host = Environment.MachineName;
        }

        return new BcDatabase(full, host, string.IsNullOrWhiteSpace(name) ? null : name);
    }
}

/// <summary>
/// BC server instances on this machine and the database each one uses, so
/// database spans name the (often remote, 2-tier) SQL server and SQL monitoring
/// finds it without configuration.
/// </summary>
public sealed class BcInstanceCatalog
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private const string ServicePrefix = "MicrosoftDynamicsNavServer$";

    private readonly Dictionary<string, BcDatabase> _databases;

    public BcInstanceCatalog(IReadOnlyDictionary<string, BcDatabase> databases)
        => _databases = new Dictionary<string, BcDatabase>(databases, StringComparer.OrdinalIgnoreCase);

    public static BcInstanceCatalog Empty { get; } = new(new Dictionary<string, BcDatabase>());

    /// <summary>Instance name -> database.</summary>
    public IReadOnlyDictionary<string, BcDatabase> Databases => _databases;

    public BcDatabase? Find(string? instance)
        => instance is not null && _databases.TryGetValue(instance, out var db) ? db : null;

    /// <summary>Reads the BC services of this machine (Windows only; empty elsewhere or on errors).</summary>
    public static BcInstanceCatalog Discover(ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Empty;
        }

        var result = new Dictionary<string, BcDatabase>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(ServicesKey);
            foreach (var name in services?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (!name.StartsWith(ServicePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var service = services!.OpenSubKey(name);
                var instance = name[ServicePrefix.Length..];
                var config = FindSettings(service?.GetValue("ImagePath") as string, instance);
                if (config is not null && ReadDatabase(config) is { } db)
                {
                    result[instance] = db;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or System.Xml.XmlException)
        {
            logger?.LogWarning(ex, "Could not read the BC server instance settings");
        }

        return new BcInstanceCatalog(result);
    }

    /// <summary>
    /// Multi-instance installations keep the settings in <c>Instances\&lt;name&gt;</c>,
    /// the first instance next to the server executable.
    /// </summary>
    private static string? FindSettings(string? imagePath, string instance)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var exe = imagePath.StartsWith('"') ? imagePath[1..imagePath.IndexOf('"', 1)] : imagePath.Split(' ')[0];
        var dir = Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(exe));
        if (dir is null)
        {
            return null;
        }

        return new[] { Path.Combine(dir, "Instances", instance, "CustomSettings.config"), Path.Combine(dir, "CustomSettings.config") }
            .FirstOrDefault(File.Exists);
    }

    public static BcDatabase? ReadDatabase(string customSettingsPath)
    {
        var settings = XDocument.Load(customSettingsPath).Descendants("add")
            .Where(e => e.Attribute("key") is not null)
            .GroupBy(e => (string)e.Attribute("key")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (string?)g.First().Attribute("value"), StringComparer.OrdinalIgnoreCase);

        return settings.GetValueOrDefault("DatabaseServer") is { Length: > 0 } server
            ? BcDatabase.Create(server, settings.GetValueOrDefault("DatabaseInstance"), settings.GetValueOrDefault("DatabaseName"))
            : null;
    }
}

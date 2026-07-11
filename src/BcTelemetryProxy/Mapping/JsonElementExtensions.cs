using System.Text.Json;

namespace BcTelemetryProxy.Mapping;

/// <summary>Small, null-safe accessors over the raw AI <c>baseData</c> JSON.</summary>
public static class JsonElementExtensions
{
    public static string? GetStringOrNull(this JsonElement data, string name)
        => data.ValueKind == JsonValueKind.Object &&
           data.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static int? GetIntOrNull(this JsonElement data, string name)
        => data.ValueKind == JsonValueKind.Object &&
           data.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.Number &&
           v.TryGetInt32(out var i)
            ? i
            : null;

    public static double? GetDoubleOrNull(this JsonElement data, string name)
        => data.ValueKind == JsonValueKind.Object &&
           data.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.Number &&
           v.TryGetDouble(out var d)
            ? d
            : null;

    public static bool? GetBoolOrNull(this JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : null,
            _ => null,
        };
    }

    /// <summary>Reads a value from the nested <c>properties</c> (custom dimensions) object.</summary>
    public static string? GetCustomProperty(this JsonElement data, string propertyName)
    {
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("properties", out var props) &&
            props.ValueKind == JsonValueKind.Object &&
            props.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    /// <summary>Enumerates the custom dimensions (<c>properties</c>) as string pairs.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> EnumerateCustomProperties(this JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("properties", out var props) &&
            props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                yield return new(prop.Name, prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.GetRawText());
            }
        }
    }
}

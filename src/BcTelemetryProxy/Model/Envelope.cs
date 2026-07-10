using System.Text.Json;
using System.Text.Json.Serialization;

namespace BcTelemetryProxy.Model;

/// <summary>
/// An Application Insights ingestion envelope as posted by Business Central to
/// <c>/v2/track</c>. Only the fields we care about are modelled explicitly; the
/// payload-specific part is kept as a raw <see cref="JsonElement"/> and
/// interpreted by the mapper based on <see cref="EnvelopeData.BaseType"/>.
/// </summary>
public sealed class Envelope
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("time")]
    public DateTimeOffset? Time { get; set; }

    [JsonPropertyName("iKey")]
    public string? InstrumentationKey { get; set; }

    /// <summary>Context tags such as <c>ai.cloud.role</c> or <c>ai.operation.id</c>.</summary>
    [JsonPropertyName("tags")]
    public Dictionary<string, string> Tags { get; set; } = new();

    [JsonPropertyName("data")]
    public EnvelopeData? Data { get; set; }

    public DateTime TimestampUtc => (Time ?? DateTimeOffset.UtcNow).UtcDateTime;

    // Common context tag helpers ------------------------------------------------
    public string? CloudRole => Tag("ai.cloud.role");
    public string? CloudRoleInstance => Tag("ai.cloud.roleInstance");
    public string? OperationId => Tag("ai.operation.id");
    public string? OperationParentId => Tag("ai.operation.parentId");
    public string? OperationName => Tag("ai.operation.name");
    public string? UserId => Tag("ai.user.id");
    public string? SessionId => Tag("ai.session.id");

    public string? Tag(string key) => Tags.TryGetValue(key, out var v) ? v : null;
}

public sealed class EnvelopeData
{
    [JsonPropertyName("baseType")]
    public string? BaseType { get; set; }

    [JsonPropertyName("baseData")]
    public JsonElement BaseData { get; set; }
}

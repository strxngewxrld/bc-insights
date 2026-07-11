using System.Text.Json;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Mapping;
using BcTelemetryProxy.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.Tests;

internal static class TestFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static Envelope ParseEnvelope(string json)
        => JsonSerializer.Deserialize<Envelope>(json, JsonOptions)!;

    public static AttributeMapper AttributeMapper(TelemetryPrivacyOptions? privacy = null)
        => new(Options.Create(privacy ?? new TelemetryPrivacyOptions()));

    public static AiToOtelMapper Mapper(IOtelEmitter emitter, TelemetryPrivacyOptions? privacy = null)
        => new(emitter, AttributeMapper(privacy), NullLogger<AiToOtelMapper>.Instance);
}

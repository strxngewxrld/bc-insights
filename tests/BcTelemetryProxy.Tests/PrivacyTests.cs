using BcTelemetryProxy.Configuration;

namespace BcTelemetryProxy.Tests;

public class PrivacyTests
{
    [Fact]
    public void Redacted_attribute_value_is_replaced()
    {
        var emitter = new RecordingEmitter();
        var mapper = TestFactory.Mapper(emitter, new TelemetryPrivacyOptions
        {
            RedactedAttributes = { "password" },
        });

        mapper.Map(TestFactory.ParseEnvelope("""
        {"data":{"baseType":"MessageData","baseData":{"message":"m","properties":{"password":"s3cret"}}}}
        """));

        var log = Assert.Single(emitter.Logs);
        Assert.Equal("[REDACTED]", log.Attributes["password"]);
    }

    [Fact]
    public void Hashed_attribute_value_is_stable_and_not_plaintext()
    {
        var emitter = new RecordingEmitter();
        var mapper = TestFactory.Mapper(emitter, new TelemetryPrivacyOptions
        {
            HashedAttributes = { "enduser.id" },
        });

        mapper.Map(TestFactory.ParseEnvelope("""
        {"tags":{"ai.user.id":"MEGANB"},"data":{"baseType":"MessageData","baseData":{"message":"m"}}}
        """));

        var log = Assert.Single(emitter.Logs);
        var hashed = Assert.IsType<string>(log.Attributes["enduser.id"]);
        Assert.NotEqual("MEGANB", hashed);
        Assert.Equal(64, hashed.Length); // SHA-256 hex
    }

    [Fact]
    public void Denied_attribute_is_removed()
    {
        var emitter = new RecordingEmitter();
        var mapper = TestFactory.Mapper(emitter, new TelemetryPrivacyOptions
        {
            DeniedAttributes = { "companyName" },
        });

        mapper.Map(TestFactory.ParseEnvelope("""
        {"data":{"baseType":"MessageData","baseData":{"message":"m","properties":{"companyName":"CRONUS","sqlExecutes":"3"}}}}
        """));

        // A rule on the BC dimension name also covers its renamed export key.
        var log = Assert.Single(emitter.Logs);
        Assert.False(log.Attributes.ContainsKey("companyName"));
        Assert.False(log.Attributes.ContainsKey("bc.company.name"));
        Assert.True(log.Attributes.ContainsKey("sqlExecutes"));
    }

    [Fact]
    public void Allowlist_drops_everything_else()
    {
        var emitter = new RecordingEmitter();
        var mapper = TestFactory.Mapper(emitter, new TelemetryPrivacyOptions
        {
            AllowedAttributes = { "sqlExecutes", "executionTime" },
        });

        mapper.Map(TestFactory.ParseEnvelope("""
        {"tags":{"ai.session.id":"s1"},"data":{"baseType":"MessageData","baseData":{"message":"m","properties":{
          "companyName":"CRONUS","sqlExecutes":"3","executionTime":"00:00:01"}}}}
        """));

        var log = Assert.Single(emitter.Logs);
        Assert.Equal(new[] { "executionTimeInMs", "sqlExecutes" }, log.Attributes.Keys.OrderBy(k => k));
    }
}

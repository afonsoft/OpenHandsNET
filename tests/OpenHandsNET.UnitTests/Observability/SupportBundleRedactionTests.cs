using Microsoft.Extensions.Configuration;
using OpenHandsNET.Infrastructure.Observability;

namespace OpenHandsNET.UnitTests.Observability;

/// <summary>
/// Support-bundle redaction (SPEC-14 RF-004): every secret-shaped config
/// value must be masked; log lines lose sk-* and placeholder secrets.
/// </summary>
public sealed class SupportBundleRedactionTests
{
    private static Dictionary<string, string?> Flatten(
        Dictionary<string, string?> pairs) =>
        SupportBundleService.EffectiveConfigOf(
            new ConfigurationBuilder().AddInMemoryCollection(pairs).Build());

    [Theory]
    [InlineData("Agent:ApiKey")]
    [InlineData("Auth:ClientSecret")]
    [InlineData("ConnectionStrings:Default")]
    [InlineData("Git:Token")]
    [InlineData("Observability:OtlpHeaders:authorization")]
    [InlineData("LLM_PASSWORD")]
    public void Sensitive_Paths_Are_Masked(string key)
    {
        var flat = Flatten(new() { [key] = "super-secret-value" });
        flat[key].ShouldBe("***");
    }

    [Theory]
    [InlineData("Sandbox:Profile")]
    [InlineData("Sandbox:EgressMode")]
    [InlineData("AgentLlm:Model")]
    [InlineData("Database:Provider")]
    public void NonSensitive_Paths_Pass_Through(string key)
    {
        var flat = Flatten(new() { [key] = "plain-value" });
        flat[key].ShouldBe("plain-value");
    }

    [Fact]
    public void Sk_Shaped_Values_Are_Masked_Even_On_Innocent_Keys()
    {
        var flat = Flatten(new()
        {
            ["Custom:Label"] = "sk-live-abcdef1234567890",
        });
        flat["Custom:Label"].ShouldBe("***");
    }

    [Fact]
    public void ConnectionString_With_Embedded_Password_Is_Masked()
    {
        var flat = Flatten(new()
        {
            ["Custom:Upstream"] =
                "Host=db.internal;Password=hunter2-hunter2-hunter2;Port=5432",
        });
        flat["Custom:Upstream"].ShouldBe("***");
    }

    [Theory]
    [InlineData("calling api with key sk-abc123XYZ_ok", "calling api with key ***")]
    [InlineData("prompt had {{secret:GH_TOKEN}} here", "prompt had *** here")]
    [InlineData("plain info line", "plain info line")]
    public void Log_Lines_Are_Redacted(string input, string expected) =>
        SupportBundleService.RedactLine(input).ShouldBe(expected);
}

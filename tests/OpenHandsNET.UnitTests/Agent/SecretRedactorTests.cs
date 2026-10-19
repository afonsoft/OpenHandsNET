using OpenHandsNET.Infrastructure.Agent;

using Shouldly;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>SPEC-09 AC: secrets nunca entram no prompt/logs.</summary>
public sealed class SecretRedactorTests
{
    [Fact]
    public void Placeholder_Pattern_Is_Masked() =>
        SecretRedactor.Redact("key={{secret:AWS_KEY}} done")
            .ShouldBe("key=*** done");

    [Fact]
    public void Known_Values_Are_Masked() =>
        SecretRedactor.Redact("token sk-abc123 leaked", ["sk-abc123"])
            .ShouldBe("token *** leaked");

    [Fact]
    public void Null_And_Empty_Are_Safe()
    {
        SecretRedactor.Redact(null).ShouldBe("");
        SecretRedactor.Redact("", ["x"]).ShouldBe("");
    }

    [Fact]
    public void Non_Secret_Text_Is_Untouched() =>
        SecretRedactor.Redact("normal output", ["sk-x"]).ShouldBe("normal output");
}
using Sandcastle.Infrastructure.ChatOps;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.ChatOps;

/// <summary>
/// SPEC-147 RF-001 — <see cref="SlackTextSanitizer.NeutralizePings"/>:
/// todo ping de grupo (@channel/@here/@everyone, &lt;!…&gt; e
/// &lt;!subteam^…&gt;) sai neutralizado; menções a usuários
/// (&lt;@U…&gt;) e texto comum ficam intactos.
/// </summary>
public sealed class SlackTextSanitizerTests
{
    [Theory]
    [InlineData("@channel")]
    [InlineData("@here")]
    [InlineData("@everyone")]
    [InlineData("<!channel>")]
    [InlineData("<!here>")]
    [InlineData("<!everyone>")]
    [InlineData("<!channel|@channel>")]
    [InlineData("<!subteam^S0123ABC>")]
    [InlineData("<!subteam^S0123ABC|@devs>")]
    public void NeutralizePings_GroupTokens_Neutralized(string ping)
    {
        var result = SlackTextSanitizer.NeutralizePings(
            $"summary done {ping} now");

        // A forma visível continua legível mas o token não casa mais.
        result.ShouldNotContain(ping);
        result.ShouldNotContain("<!");
        result.ShouldContain('\u200B');
    }

    [Fact]
    public void NeutralizePings_PreservesUserMentions()
    {
        const string text = "cc <@U123> and <@W999|bot> — <@U123> done";

        SlackTextSanitizer.NeutralizePings(text).ShouldBe(text);
    }

    [Fact]
    public void NeutralizePings_MixedText_OnlyPingsTouched()
    {
        var result = SlackTextSanitizer.NeutralizePings(
            "Build ok <@U1>. @channel check logs at <!here>.");

        result.ShouldContain("<@U1>");
        result.ShouldNotContain("@channel ");
        result.ShouldNotContain("<!here>");
        result.ShouldContain("@\u200Bchannel");
        result.ShouldContain("@\u200Bhere");
    }

    [Fact]
    public void NeutralizePings_EmailAddress_NotBroken()
    {
        const string text = "contact ops@channel.example or dev@here.io";

        // O @ de e-mail é precedido de letra — não é ping.
        SlackTextSanitizer.NeutralizePings(text).ShouldBe(text);
    }

    [Fact]
    public void NeutralizePings_SubteamLabel_BecomesReadableHandle()
    {
        var result = SlackTextSanitizer.NeutralizePings(
            "ask <!subteam^S0ABC|@devops> for access");

        result.ShouldContain("@\u200Bdevops");
        result.ShouldNotContain("subteam");
    }

    [Fact]
    public void NeutralizePings_NullAndEmpty_PassThrough()
    {
        SlackTextSanitizer.NeutralizePings(null).ShouldBe("");
        SlackTextSanitizer.NeutralizePings("").ShouldBe("");
        SlackTextSanitizer.NeutralizePings("plain text").ShouldBe("plain text");
    }

    [Fact]
    public void NeutralizePings_AlreadyNeutralized_Idempotent()
    {
        var once = SlackTextSanitizer.NeutralizePings("hi @channel");
        SlackTextSanitizer.NeutralizePings(once).ShouldBe(once);
    }

    [Fact]
    public void NeutralizePings_CaseVariants_Neutralized()
    {
        foreach (var ping in new[] { "@Channel", "@HERE", "<!CHANNEL>", "<!Everyone>" })
        {
            SlackTextSanitizer.NeutralizePings(ping).ShouldContain('\u200B');
        }
    }
}
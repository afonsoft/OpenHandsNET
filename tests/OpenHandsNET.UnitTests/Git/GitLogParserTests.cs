using OpenHandsNET.Infrastructure.Git;
using Shouldly;

namespace OpenHandsNET.UnitTests.Git;

/// <summary><c>git log --format=%H%x1f%an%x1f%aI%x1f%s%x1e</c> fixtures.</summary>
public sealed class GitLogParserTests
{
    [Fact]
    public void Parse_Entries()
    {
        var output =
            "deadbeef1234openhandsnet-agent2026-10-13T14:00:00+00:00commit message" +
            "cafe0000abcduser2026-10-13T13:00:00+00:00init repo";

        var entries = GitLogParser.Parse(output);
        entries.Count.ShouldBe(2);
        entries[0].Hash.ShouldBe("deadbeef1234");
        entries[0].Author.ShouldBe("openhandsnet-agent");
        entries[0].Message.ShouldBe("commit message");
        entries[1].Message.ShouldBe("init repo");
    }

    [Fact]
    public void Parse_Empty()
    {
        GitLogParser.Parse("").ShouldBeEmpty();
    }
}

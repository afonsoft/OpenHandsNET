using OpenHandsNET.Infrastructure.Git;
using Shouldly;

namespace OpenHandsNET.UnitTests.Git;

/// <summary>Porcelain v2 fixtures (SPEC-12 RF-002).</summary>
public sealed class GitStatusParserTests
{
    [Fact]
    public void Parse_Branch_And_Buckets()
    {
        const string output = """
            # branch.oid abc123
            # branch.head openhands/abc12345
            # branch.upstream origin/main
            1 M. N... 100644 100644 100644 h1 h2 staged.txt
            1 .M N... 100644 100644 100644 h1 h2 work/modified file.txt
            1 AM N... 100644 100644 100644 h1 h2 both.txt
            u UU N... 1 2 3 4 5 6 7 clash.txt
            ? new file.txt
            ? dir/
            ! ignored.tmp
            """;

        var c = GitStatusParser.Parse(output);
        c.Branch.ShouldBe("openhands/abc12345");
        c.Staged.ShouldContain(f => f.Path == "staged.txt" && f.Status == "modified");
        c.Staged.ShouldContain(f => f.Path == "both.txt" && f.Status == "added");
        c.Modified.ShouldContain(f => f.Path == "work/modified file.txt");
        c.Modified.ShouldContain(f => f.Path == "both.txt");
        c.Untracked.ShouldContain(f => f.Path == "new file.txt");
        c.Conflicts.ShouldContain(f => f.Path == "clash.txt");
    }

    [Fact]
    public void Parse_Rename_Maps_OrigPath()
    {
        const string output = """
            # branch.head main
            2 R. N... 100644 100644 100644 h1 h2 R100 new.txt	old.txt
            """;

        var c = GitStatusParser.Parse(output);
        var f = c.Staged.ShouldHaveSingleItem();
        f.Path.ShouldBe("new.txt");
        f.Status.ShouldBe("renamed");
        f.OrigPath.ShouldBe("old.txt");
    }

    [Fact]
    public void Parse_Empty()
    {
        var c = GitStatusParser.Parse("# branch.head main\n");
        c.Staged.ShouldBeEmpty();
        c.Modified.ShouldBeEmpty();
        c.Untracked.ShouldBeEmpty();
    }
}

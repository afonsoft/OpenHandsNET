using OpenHandsNET.Infrastructure.Git;

using Shouldly;

namespace OpenHandsNET.UnitTests.Git;

/// <summary>Unified diff fixtures (SPEC-12 RF-002).</summary>
public sealed class GitDiffParserTests
{
    [Fact]
    public void Parse_Modify_And_Add()
    {
        const string diff = """
            diff --git a/README.md b/README.md
            index 111..222 100644
            --- a/README.md
            +++ b/README.md
            @@ -1,3 +1,4 @@
             line1
            -old line
            +new line
            +extra
             tail
            diff --git a/src/new.cs b/src/new.cs
            new file mode 100644
            index 000..333
            --- /dev/null
            +++ b/src/new.cs
            @@ -0,0 +1,2 @@
            +class A {}
            +class B {}
            """;

        var r = GitDiffParser.Parse(diff);
        r.Files.Count.ShouldBe(2);
        r.Files[0].Path.ShouldBe("README.md");
        r.Files[0].Status.ShouldBe("modified");
        r.Files[0].Hunks.ShouldHaveSingleItem().OldStart.ShouldBe(1);
        r.Files[0].Hunks[0].NewLines.ShouldBe(4);
        r.Files[1].Path.ShouldBe("src/new.cs");
        r.Files[1].Status.ShouldBe("added");
        r.Additions.ShouldBe(4);
        r.Deletions.ShouldBe(1);
    }

    [Fact]
    public void Parse_Deleted_File()
    {
        const string diff = """
            diff --git a/gone.txt b/gone.txt
            deleted file mode 100644
            index 111..000
            --- a/gone.txt
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -bye
            -gone
            """;

        var r = GitDiffParser.Parse(diff);
        var f = r.Files.ShouldHaveSingleItem();
        f.Path.ShouldBe("gone.txt");
        f.Status.ShouldBe("deleted");
        r.Deletions.ShouldBe(2);
    }

    [Fact]
    public void Parse_EmptyDiff()
    {
        var r = GitDiffParser.Parse("");
        r.Files.ShouldBeEmpty();
        (r.Additions, r.Deletions).ShouldBe((0, 0));
    }
}
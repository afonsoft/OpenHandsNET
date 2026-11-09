using OpenHandsNET.Infrastructure.Conversations;

using Shouldly;

namespace OpenHandsNET.UnitTests.Git;

/// <summary>
/// SPEC-135 RF-005: <see cref="HunkPatch"/> — split de unified diff em
/// header+hunks e remontagem de patch single-hunk aplicável.
/// </summary>
public sealed class HunkPatchTests
{
    private const string Diff = """
        diff --git a/a.cs b/a.cs
        index 111..222 100644
        --- a/a.cs
        +++ b/a.cs
        @@ -1,3 +1,3 @@
         a
        -b
        +B
         c
        @@ -10,2 +10,2 @@
         x
        -y
        +Y
        """;

    [Fact]
    public void Split_Returns_Header_And_Each_Hunk()
    {
        var (header, hunks) = HunkPatch.Split(Diff + '\n');
        header.ShouldContain("diff --git");
        header.ShouldContain("+++ b/a.cs");
        header.ShouldNotContain("@@");
        hunks.Count.ShouldBe(2);
        hunks[0].ShouldStartWith("@@ -1,3");
        hunks[0].ShouldContain("+B");
        hunks[1].ShouldStartWith("@@ -10,2");
    }

    [Fact]
    public void Build_Joins_Header_And_One_Hunk()
    {
        var (header, hunks) = HunkPatch.Split(Diff + '\n');
        var patch = HunkPatch.Build(header, hunks[1]);
        patch.ShouldContain("+++ b/a.cs");
        patch.ShouldContain("@@ -10,2");
        patch.ShouldNotContain("@@ -1,3");
    }

    [Fact]
    public void Split_No_Hunks_Returns_Header_Only()
    {
        var (_, hunks) = HunkPatch.Split("diff --git a/a b/a\n");
        hunks.ShouldBeEmpty();
    }
}
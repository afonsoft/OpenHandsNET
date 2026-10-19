using OpenHandsNET.Application.Indexing;
using OpenHandsNET.Infrastructure.Indexing;
using Shouldly;
using Xunit;

namespace OpenHandsNET.UnitTests.Indexing;

/// <summary>SPEC-60: chunker, indexignore globs, citações.</summary>
public sealed class Spec60IndexTests
{
    // ---- CodeChunker -------------------------------------------------

    [Fact]
    public void Chunk_CsFile_SplitsAtTypeDeclarations()
    {
        var lines = new[]
        {
            "using System;", "",
            "public class Foo", "{", "    void A() { }", "}", "",
            "public class Bar", "{", "    void B() { }", "}",
        };
        var units = CodeChunker.Chunk("src/Foo.cs", lines);
        units.ShouldContain(u => u.Kind == "symbol");
        units.Select(u => u.Text).ShouldContain(t => t.Contains("class Foo"));
        units.Select(u => u.Text).ShouldContain(t => t.Contains("class Bar"));
    }

    [Fact]
    public void Chunk_PythonFile_SplitsAtDefClass()
    {
        var lines = new[]
        {
            "def alpha():", "    return 1", "",
            "def beta():", "    return 2",
        };
        var units = CodeChunker.Chunk("mod/a.py", lines);
        units.Count(u => u.Kind == "symbol").ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Chunk_LongFile_FallsBackToSlidingWindow()
    {
        var lines = Enumerable.Range(1, 200)
            .Select(i => $"line {i}").ToArray();
        var units = CodeChunker.Chunk("notes.txt", lines);
        units.ShouldAllBe(u => u.Kind == "chunk");
        units.Count.ShouldBeGreaterThan(1);
        // overlap: unidades consecutivas compartilham linhas
        units[0].End.ShouldBeGreaterThan(units[1].Start);
    }

    [Fact]
    public void Chunk_UnknownExtension_ProducesChunks()
    {
        var lines = Enumerable.Range(1, 10)
            .Select(i => $"x{i}").ToArray();
        var units = CodeChunker.Chunk("data.csv", lines);
        units.ShouldNotBeEmpty();
    }

    // ---- GlobToRegex (indexignore) ------------------------------------

    [Theory]
    [InlineData("*.gen.cs", "a/b.gen.cs", true)]
    [InlineData("*.gen.cs", "a/Foo.cs", false)]
    [InlineData("docs/**", "docs/a/b/c.md", true)]
    [InlineData("docs/**", "src/a.md", false)]
    [InlineData("vendor/*.js", "vendor/x.js", true)]
    [InlineData("vendor/*.js", "vendor/sub/x.js", false)]
    public void GlobToRegex_Matches(
        string glob, string path, bool expected) =>
        CodeChunker.GlobToRegex(glob)!.IsMatch(path).ShouldBe(expected);

    [Fact]
    public void IndexableExtensions_CoversCommonCode()
    {
        CodeChunker.IndexableExtensions.ShouldContain(".cs");
        CodeChunker.IndexableExtensions.ShouldContain(".py");
        CodeChunker.IndexableExtensions.ShouldContain(".ts");
        CodeChunker.IndexableExtensions.ShouldNotContain(".png");
    }

    // ---- citacao regex (AskService) -----------------------------------

    [Fact]
    public void AskCitation_RecordShape()
    {
        var c = new AskCitation("src/Foo.cs", 10, 42);
        c.Path.ShouldBe("src/Foo.cs");
        (c.StartLine, c.EndLine).ShouldBe((10, 42));
    }

    // ---- contracts ----------------------------------------------------

    [Fact]
    public void RepoIndexStatus_HasExpectedValues()
    {
        Enum.GetValues<RepoIndexStatus>().ShouldBe(
            [RepoIndexStatus.Indexing, RepoIndexStatus.Ready,
             RepoIndexStatus.Stale, RepoIndexStatus.Failed]);
    }
}

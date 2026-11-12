using Sandcastle.Application.Scans;
using Sandcastle.Infrastructure.Scans;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Scans;

/// <summary>
/// SPEC-143 RF-002: <see cref="ScanDeduplicator"/> — exact-key dedup on
/// (repo,path,start_line,kind) + near-dup merge (close lines × similar
/// text), keeping max severity / best description / max confidence.
/// </summary>
public sealed class Spec143ScanDeduplicatorTests
{
    private static ScanFindingInput F(
        string path = "src/A.cs", int? line = 10,
        string kind = "security", string severity = "medium",
        string repo = "org/repo", string? title = "sql injection",
        string desc = "user input reaches a sql query unsanitized",
        double? conf = 0.5, int? end = null) =>
        new(repo, path, line, end, kind, severity, conf, title, desc,
            null, null);

    [Fact]
    public void Exact_Duplicates_Merge_Keeping_Max_Severity()
    {
        var (kept, merged) = ScanDeduplicator.Deduplicate(
        [
            F(severity: "low", conf: 0.9),
            F(severity: "high", conf: 0.4),
        ]);

        merged.ShouldBe(1);
        kept.Count.ShouldBe(1);
        kept[0].Severity.ShouldBe("high");
        kept[0].Confidence.ShouldBe(0.9); // max conf entre os dois
    }

    [Fact]
    public void Exact_Key_Includes_Repo_Path_Line_Kind()
    {
        var (kept, merged) = ScanDeduplicator.Deduplicate(
        [
            F(line: 10),
            F(line: 50),                // linha diferente (fora near-window)
            F(kind: "performance"),     // kind diferente
            F(path: "src/B.cs"),        // path diferente
            F(repo: "org/other"),       // repo diferente
        ]);

        merged.ShouldBe(0);
        kept.Count.ShouldBe(5);
    }

    [Fact]
    public void Path_Normalization_Merges_Slashes_And_Case()
    {
        var (kept, merged) = ScanDeduplicator.Deduplicate(
        [
            F(path: "SRC\\A.cs"),
            F(path: "src/a.cs"),
        ]);

        merged.ShouldBe(1);
        kept.Count.ShouldBe(1);
    }

    [Fact]
    public void NearDup_Close_Lines_Same_Title_Merges()
    {
        var (kept, merged) = ScanDeduplicator.Deduplicate(
        [
            F(line: 10, title: "unsafe deser"),
            F(line: 15, title: "Unsafe Deser"), // ±10 window
        ]);

        merged.ShouldBe(1);
        kept.Count.ShouldBe(1);
    }

    [Fact]
    public void NearDup_Requires_Text_Similarity()
    {
        var (kept, merged) = ScanDeduplicator.Deduplicate(
        [
            F(line: 10, title: "first issue",
                desc: "completely unrelated alpha beta gamma delta"),
            F(line: 12, title: "second issue",
                desc: "other words entirely different epsilon zeta"),
        ]);

        merged.ShouldBe(0);
        kept.Count.ShouldBe(2);
    }

    [Fact]
    public void NearDup_Far_Lines_Dont_Merge()
    {
        var (kept, merged) = ScanDeduplicator.Deduplicate(
        [
            F(line: 10),
            F(line: 100), // fora da janela de 10
        ]);

        merged.ShouldBe(0);
        kept.Count.ShouldBe(2);
    }

    [Fact]
    public void NearDup_Jaccard_Description_Match_Merges()
    {
        // títulos distintos mas descrições quase idênticas.
        var (kept, merged) = ScanDeduplicator.Deduplicate(
        [
            F(line: 20, title: "sqli risk",
                desc: "the user input flows directly into sql query without sanitization"),
            F(line: 25, title: "sql injection",
                desc: "the user input flows directly into sql queries without sanitization"),
        ]);

        merged.ShouldBe(1);
        kept.Count.ShouldBe(1);
    }

    [Fact]
    public void Merge_Prefers_Longer_Description_And_Fills_Nulls()
    {
        var (kept, _) = ScanDeduplicator.Deduplicate(
        [
            F(title: null, desc: "short",
                end: null, conf: null),
            F(title: "t", desc: "a much longer description of the bug",
                end: 42, conf: 0.7),
        ]);

        kept.Count.ShouldBe(1);
        kept[0].Description
            .ShouldBe("a much longer description of the bug");
        kept[0].Title.ShouldBe("t");
        kept[0].EndLine.ShouldBe(42);
        kept[0].Confidence.ShouldBe(0.7);
    }
}
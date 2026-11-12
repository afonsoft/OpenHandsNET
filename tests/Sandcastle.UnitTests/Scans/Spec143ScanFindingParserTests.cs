using Sandcastle.Infrastructure.Scans;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Scans;

/// <summary>
/// SPEC-143 RF-002: <see cref="ScanFindingParser"/> — fenced
/// <c>scan_findings</c> block / trailing JSON, schema check, invalid rows
/// skipped and field normalization/capping.
/// </summary>
public sealed class Spec143ScanFindingParserTests
{
    private static string Payload(object findings) =>
        "```scan_findings\n"
        + System.Text.Json.JsonSerializer.Serialize(
            new { schema = "scan_finding_v1", findings })
        + "\n```";

    private static object Row(object? extra = null) => new
    {
        path = "src/Auth.cs",
        start_line = 42,
        kind = "security",
        severity = "high",
        title = "sql injection",
        description = "concatenated query",
        confidence = 0.8,
        remediation_hint = "parameterize",
        payoff = "high",
    };

    [Fact]
    public void Parses_Fenced_Block()
    {
        var (findings, error) = ScanFindingParser.Parse(
            "some prose first\n" + Payload(new[] { Row() }));

        error.ShouldBeNull();
        findings.Count.ShouldBe(1);
        findings[0].Path.ShouldBe("src/Auth.cs");
        findings[0].StartLine.ShouldBe(42);
        findings[0].Kind.ShouldBe("security");
        findings[0].Severity.ShouldBe("high");
        findings[0].Confidence.ShouldBe(0.8);
        findings[0].Payoff.ShouldBe("high");
    }

    [Fact]
    public void Last_Fenced_Block_Wins()
    {
        var stale = Payload(new[]
        {
            new { path = "stale.cs", description = "stale" },
        });
        var fresh = Payload(new[] { Row() });

        var (findings, _) = ScanFindingParser.Parse(
            stale + "\n\n" + fresh);

        findings.Count.ShouldBe(1);
        findings[0].Path.ShouldBe("src/Auth.cs");
    }

    [Fact]
    public void Trailing_Raw_Json_Fallback()
    {
        var json = "{\"schema\":\"scan_finding_v1\",\"findings\":["
            + "{\"path\":\"a.cs\",\"description\":\"x\"}]}";
        var (findings, error) = ScanFindingParser.Parse(
            "analysis…\n" + json);

        error.ShouldBeNull();
        findings.Count.ShouldBe(1);
    }

    [Fact]
    public void Missing_Block_Reports_Error()
    {
        var (_, error) = ScanFindingParser.Parse("no findings here");
        error.ShouldBe("no_scan_findings_block");
    }

    [Fact]
    public void Empty_Output_Reports_Error()
    {
        var (_, error) = ScanFindingParser.Parse("   ");
        error.ShouldBe("empty_output");
    }

    [Fact]
    public void Wrong_Schema_Rejected()
    {
        var payload = "```scan_findings\n"
            + "{\"schema\":\"other_v9\",\"findings\":[]}\n```";
        var (_, error) = ScanFindingParser.Parse(payload);
        error.ShouldBe("unexpected_schema");
    }

    [Fact]
    public void Invalid_Rows_Are_Skipped()
    {
        var (findings, error) = ScanFindingParser.Parse(Payload(new object[]
        {
            new { path = "", description = "no path" },
            new { path = "b.cs" },                       // sem description
            new { path = "c.cs", description = "ok" },   // válido
            "not-an-object",
        }));

        error.ShouldBeNull();
        findings.Count.ShouldBe(1);
        findings[0].Path.ShouldBe("c.cs");
    }

    [Fact]
    public void Unknown_Kind_And_Severity_Normalize()
    {
        var (findings, _) = ScanFindingParser.Parse(Payload(new[]
        {
            new
            {
                path = "a.cs", description = "d",
                kind = "weird", severity = "WEIRD",
            },
        }));

        findings[0].Kind.ShouldBe("custom");
        findings[0].Severity.ShouldBe("medium");
    }

    [Fact]
    public void Text_Fields_Are_Capped()
    {
        var longDesc = new string('x', 10_000);
        var (findings, _) = ScanFindingParser.Parse(Payload(new[]
        {
            new { path = "a.cs", description = longDesc },
        }));

        findings[0].Description.Length
            .ShouldBeLessThanOrEqualTo(4013); // cap + truncation mark
        findings[0].Description.ShouldEndWith("[…truncated]");
    }
}
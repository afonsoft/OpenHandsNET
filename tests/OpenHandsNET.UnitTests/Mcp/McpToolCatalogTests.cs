using System.Text.Json;

using OpenHandsNET.Application.Mcp;

namespace OpenHandsNET.UnitTests.Mcp;

/// <summary>McpToolCatalog: naming mcp__s__t + truncate 1024 (SPEC-07 RF-004).</summary>
public sealed class McpToolCatalogTests
{
    [Fact]
    public void FullName_Joins_With_Double_Underscores() =>
        McpToolCatalog.FullName("fetch", "get_url")
            .ShouldBe("mcp__fetch__get_url");

    [Theory]
    [InlineData("mcp__fetch__get_url", "fetch", "get_url")]
    [InlineData("mcp__db__run-query", "db", "run-query")]
    public void TryParse_Round_Trip(
        string full, string server, string tool)
    {
        McpToolCatalog.TryParse(full, out var s, out var t).ShouldBeTrue();
        s.ShouldBe(server);
        t.ShouldBe(tool);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("mcp__")]
    [InlineData("mcp__only")]
    [InlineData("mcp____tool")]
    [InlineData("mcp__a__")]
    public void TryParse_Rejects_Invalid(string full) =>
        McpToolCatalog.TryParse(full, out _, out _).ShouldBeFalse();

    [Fact]
    public void TryParse_Tool_Name_May_Contain_Double_Underscores()
    {
        // Primeiro __ separa; o resto é da tool (round-trip com FullName).
        McpToolCatalog.TryParse("mcp__a__b__c", out var s, out var t)
            .ShouldBeTrue();
        s.ShouldBe("a");
        t.ShouldBe("b__c");
    }

    [Fact]
    public void ToSchema_Prefixes_And_Truncates_Description()
    {
        var d = new McpToolDescriptor(
            "srv", "tool", "mcp__srv__tool",
            new string('x', McpToolCatalog.MaxDescriptionLength + 50),
            JsonDocument.Parse("{}").RootElement);
        var schema = McpToolCatalog.ToSchema(d);
        schema.Name.ShouldBe("mcp__srv__tool");
        schema.Description.Length.ShouldBe(McpToolCatalog.MaxDescriptionLength);
    }

    [Fact]
    public void ToSchema_Null_Description_Becomes_Empty()
    {
        var d = new McpToolDescriptor(
            "s", "t", "mcp__s__t", null,
            JsonDocument.Parse("{}").RootElement);
        McpToolCatalog.ToSchema(d).Description.ShouldBe("");
    }
}
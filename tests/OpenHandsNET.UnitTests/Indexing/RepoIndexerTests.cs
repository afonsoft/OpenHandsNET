using OpenHandsNET.Infrastructure.Indexing;

using Shouldly;

using Xunit;

namespace OpenHandsNET.UnitTests.Indexing;

public class RepoIndexerTests
{
    [Fact]
    public void ChunkFile_Produces_CodeUnits_With_Correct_Lines()
    {
        var content = string.Join("\n", Enumerable.Range(1, 120).Select(i => $"public void Method{i}() {{}}"));
        var indexId = Guid.NewGuid();

        var units = RepoIndexer.ChunkFile(indexId, "src/Service.cs", content);

        units.ShouldNotBeEmpty();
        units[0].Path.ShouldBe("src/Service.cs");
        units[0].StartLine.ShouldBe(1);
        units[0].Kind.ShouldBe("symbol");
        units[0].SymbolName.ShouldNotBeNull();
        units[0].RepoIndexId.ShouldBe(indexId);
    }

    [Fact]
    public void ChunkFile_Handles_Empty_Content()
    {
        var units = RepoIndexer.ChunkFile(Guid.NewGuid(), "empty.txt", "");
        units.ShouldBeEmpty();
    }
}
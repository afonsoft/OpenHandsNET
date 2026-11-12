using Sandcastle.Infrastructure.Refactors;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Refactors;

/// <summary>
/// SPEC-144 RF-002: <c>refactor_batch_v1</c> output parser (fenced +
/// bare JSON), and <see cref="RefactorRepoAnalyzer"/> — C#
/// using/project-reference edges, directory fallback, verify-command
/// inference.
/// </summary>
public sealed class Spec144RefactorParserTests
{
    [Fact]
    public void Parses_Fenced_Verify_Passed()
    {
        var (output, error) = RefactorBatchOutputParser.Parse("""
            Work summary here.

            ```refactor_batch
            {"schema":"refactor_batch_v1","verify_passed":true,
             "verify_attempts":2,"log_tail":"build ok",
             "summary":"all green"}
            ```
            """);

        error.ShouldBeNull();
        output.ShouldNotBeNull();
        output.VerifyPassed.ShouldBeTrue();
        output.VerifyAttempts.ShouldBe(2);
        output.LogTail.ShouldBe("build ok");
    }

    [Fact]
    public void Parses_Bare_Json_Verify_Failed()
    {
        var (output, error) = RefactorBatchOutputParser.Parse(
            """{"schema":"refactor_batch_v1","verify_passed":false,"log_tail":"CS0246"}""");

        error.ShouldBeNull();
        output!.VerifyPassed.ShouldBeFalse();
        output.LogTail.ShouldBe("CS0246");
    }

    [Fact]
    public void Rejects_Missing_Schema()
    {
        var (output, error) = RefactorBatchOutputParser.Parse(
            """{"verify_passed":true}""");
        output.ShouldBeNull();
        error!.ShouldContain("refactor_batch_v1");
    }

    [Fact]
    public void Rejects_Empty_And_NonJson()
    {
        RefactorBatchOutputParser.Parse("").Error.ShouldNotBeNull();
        RefactorBatchOutputParser.Parse("no json at all")
            .Error.ShouldNotBeNull();
    }

    [Fact]
    public void Truncates_Huge_Log_Tails()
    {
        var tail = new string('x', 9000);
        var (output, error) = RefactorBatchOutputParser.Parse(
            $$"""{"schema":"refactor_batch_v1","verify_passed":true,"log_tail":"{{tail}}"}""");
        error.ShouldBeNull();
        output!.LogTail!.Length.ShouldBe(4000);
    }

    [Fact]
    public void Analyzer_Builds_CSharp_Using_Edges()
    {
        var root = NewRepo();
        try
        {
            Write(root, "src/Core/Thing.cs",
                "namespace App.Core;\npublic class Thing {}\n");
            Write(root, "src/App/Use.cs",
                "using App.Core;\nnamespace App;\npublic class Use { Thing? t; }\n");

            var files = RefactorRepoAnalyzer.EnumerateFiles(root);
            var edges = RefactorRepoAnalyzer.BuildImportGraph(root, files);

            edges["src/App/Use.cs"].ShouldContain("src/Core/Thing.cs");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Analyzer_Builds_ProjectReference_Edges()
    {
        var root = NewRepo();
        try
        {
            Write(root, "Lib/Lib.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            Write(root, "Lib/Helper.cs",
                "namespace Lib;\npublic class Helper {}\n");
            Write(root, "App/App.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
                + "<ProjectReference Include=\"..\\Lib\\Lib.csproj\" />"
                + "</ItemGroup></Project>");
            Write(root, "App/Main.cs",
                "namespace App;\npublic class Main {}\n");

            var files = RefactorRepoAnalyzer.EnumerateFiles(root);
            var edges = RefactorRepoAnalyzer.BuildImportGraph(root, files);

            // App/Main.cs + App.csproj depend on files under Lib/.
            edges["App/Main.cs"].ShouldContain("Lib/Helper.cs");
            edges["App/App.csproj"].ShouldContain("Lib/Helper.cs");
            // Lib does not depend on App.
            edges.ContainsKey("Lib/Helper.cs").ShouldBeFalse();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Analyzer_InferVerifyCommand_DotNet()
    {
        var root = NewRepo();
        try
        {
            Write(root, "A.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            RefactorRepoAnalyzer.InferVerifyCommand(root)
                .ShouldBe("dotnet build && dotnet test");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Analyzer_InferVerifyCommand_Node_And_Python()
    {
        var node = NewRepo();
        var py = NewRepo();
        try
        {
            Write(node, "package.json", "{}");
            RefactorRepoAnalyzer.InferVerifyCommand(node)
                .ShouldBe("npm test");
            Write(py, "requirements.txt", "pytest\n");
            RefactorRepoAnalyzer.InferVerifyCommand(py)
                .ShouldBe("pytest");
        }
        finally
        {
            Directory.Delete(node, true);
            Directory.Delete(py, true);
        }
    }

    [Fact]
    public void Planner_Directory_Strategy_No_Edges_Packs_By_Size()
    {
        // directory strategy feeds no edges → all level 0 → size-packed
        // independent batches.
        var files = Enumerable.Range(1, 6)
            .Select(i => $"src/d{i % 2}/F{i}.cs").ToList();
        var plan = RefactorBatchPlanner.Plan(
            "org/repo", files,
            new Dictionary<string, IReadOnlyCollection<string>>(),
            "independent", 2, 200, 64, "dotnet test");

        plan.Batches.Count.ShouldBe(3);
        plan.Batches.ShouldAllBe(b => b.DependsOn.Count == 0);
    }

    private static string NewRepo()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), $"ref-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string root, string rel, string text)
    {
        var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }
}
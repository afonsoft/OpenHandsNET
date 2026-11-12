using Sandcastle.Infrastructure.Scans;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Scans;

/// <summary>
/// SPEC-143 RF-002: <see cref="ScanPartitioner"/> — top-level directory
/// counting, ignored dirs, greedy bin-pack into ≤ target partitions and
/// the flat-repo collapse to a single root partition.
/// </summary>
public sealed class Spec143ScanPartitionerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"scan-part-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Write(string rel)
    {
        var path = Path.Combine(_root,
            rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "// x\n");
        return path;
    }

    [Fact]
    public void FlatRepo_Collapses_To_Root_Partition()
    {
        Write("Program.cs");
        Write("README.md");
        Write("single/One.cs");

        var parts = ScanPartitioner.Partition(_root, "org/repo", 4);

        parts.Count.ShouldBe(1);
        parts[0].Repository.ShouldBe("org/repo");
        parts[0].Directories.ShouldBeEmpty(); // "" = repo inteiro
        parts[0].Label.ShouldBe("/");
    }

    [Fact]
    public void EmptyRepo_Collapses_To_Root_Partition()
    {
        Directory.CreateDirectory(_root);

        var parts = ScanPartitioner.Partition(_root, "org/repo", 4);

        parts.Count.ShouldBe(1);
        parts[0].Directories.ShouldBeEmpty();
    }

    [Fact]
    public void Dirs_BinPack_Within_Target()
    {
        // 4 dirs com contagens distintas → target 2 = 2 buckets.
        for (var i = 0; i < 10; i++) Write($"src/F{i}.cs");
        for (var i = 0; i < 6; i++) Write($"tests/F{i}.cs");
        for (var i = 0; i < 3; i++) Write($"docs/F{i}.md");
        for (var i = 0; i < 1; i++) Write($"tools/F{i}.sh");

        var parts = ScanPartitioner.Partition(_root, "org/repo", 2);

        parts.Count.ShouldBe(2);
        // largest-first greedy: src(10) num bucket; tests(6) no outro;
        // docs(3)+tools(1) vão pro menor → tests+docs+tools.
        var all = parts.SelectMany(p => p.Directories)
            .OrderBy(d => d).ToList();
        all.ShouldBe(["docs", "src", "tests", "tools"]);
        parts.Select(p => p.Directories).ShouldContain(
            dirs => dirs.Contains("src"));
        parts.Select(p => p.Directories).ShouldContain(
            dirs => dirs.Contains("tests"));
    }

    [Fact]
    public void Ignored_Dirs_Never_Get_Partitions()
    {
        Write("src/Real.cs");
        Write("node_modules/dep/index.js");
        Write("bin/Debug/app.cs");
        Write("obj/Debug/app.cs");
        Write("dist/bundle.js");
        Write("build/out.cs");
        Write(".git/hooks/x.sh");
        Write("lib/Util.cs");

        var parts = ScanPartitioner.Partition(_root, "org/repo", 4);

        var all = parts.SelectMany(p => p.Directories).ToList();
        all.ShouldNotContain("node_modules");
        all.ShouldNotContain("bin");
        all.ShouldNotContain("obj");
        all.ShouldNotContain("dist");
        all.ShouldNotContain("build");
        all.ShouldContain("src");
        all.ShouldContain("lib");
    }

    [Fact]
    public void NonIndexable_Files_Are_Ignored()
    {
        // só extensões não-indexáveis → colapso root.
        Write("data/blob.bin");
        Write("assets/logo.png");
        Write("src/Real.cs");

        var parts = ScanPartitioner.Partition(_root, "org/repo", 4);

        var all = parts.SelectMany(p => p.Directories).ToList();
        all.ShouldContain("src");
        all.ShouldNotContain("data");
        all.ShouldNotContain("assets");
    }

    [Fact]
    public void Root_Files_Join_Smallest_Bucket_As_Dot()
    {
        for (var i = 0; i < 5; i++) Write($"a/F{i}.cs");
        for (var i = 0; i < 5; i++) Write($"b/F{i}.cs");
        Write("README.md"); // root file

        var parts = ScanPartitioner.Partition(_root, "org/repo", 2,
            maxDirsPerPartition: 1);

        parts.Count.ShouldBe(2);
        parts.SelectMany(p => p.Directories).ShouldContain(".");
    }
}
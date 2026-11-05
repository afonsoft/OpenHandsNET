namespace OpenHandsNET.UnitTests;

/// <summary>
/// SPEC-74 (S8970 causa-raiz): SonarCloud AutoScan é build-less e lê
/// <c>&lt;Nullable&gt;</c> apenas dos .csproj — ignora
/// <c>Directory.Build.props</c>. Todo csproj declara
/// <c>&lt;Nullable&gt;enable&lt;/Nullable&gt;</c> explicitamente para que
/// o analisador veja o contexto nullable correto (evita a classe de
/// falsos-positivos "null-forgiving em contexto disabled").
/// </summary>
public sealed class ProjectHygieneTests
{
    [Fact]
    public void Todo_Csproj_Declara_Nullable_Enable()
    {
        var root = RepoRoot();
        var missing = Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin")
                && !p.Contains($"{Path.DirectorySeparatorChar}obj"))
            .Where(p => !File.ReadAllText(p).Contains("<Nullable>enable</Nullable>"))
            .Select(p => Path.GetRelativePath(root, p))
            .ToList();

        missing.ShouldBeEmpty(
            "csproj sem <Nullable>enable</Nullable> explícito: "
            + string.Join(", ", missing));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 5; i++)
        {
            dir = dir.Parent!;
        }

        return dir.FullName;
    }
}
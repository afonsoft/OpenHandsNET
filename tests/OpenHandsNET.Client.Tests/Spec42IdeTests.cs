using Shouldly;
using OpenHandsNET.Client.Services;

namespace OpenHandsNET.Client.Tests;

/// <summary>SPEC-42 RF-007: fuzzy do quick-open — score e ranking.</summary>
public sealed class Spec42QuickOpenTests
{
    [Fact]
    public void Score_Prefere_Match_No_Nome_Do_Arquivo()
    {
        IdeQuickOpenFuzzy.Score("main",
            "/workspace/repo/src/main.cs")
            .ShouldBeGreaterThan(
                IdeQuickOpenFuzzy.Score("main",
                    "/workspace/repo/src/domain/remaining.cs"));
    }

    [Fact]
    public void Score_Subsequencia_Ordem_Importa()
    {
        IdeQuickOpenFuzzy.Score("abc", "aXbXc").ShouldBeGreaterThan(0);
        IdeQuickOpenFuzzy.Score("abc", "cXbXa").ShouldBe(0);
        // Query vazia lista tudo (Ctrl+P sem filtro).
        IdeQuickOpenFuzzy.Score("", "qualquer").ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Top_Ordena_Por_Score_E_Limita()
    {
        var paths = Enumerable.Range(0, 50)
            .Select(i => $"/workspace/repo/f{i}.cs")
            .Append("/workspace/repo/target.cs");
        var top = IdeQuickOpenFuzzy.Top("target", paths, take: 5);
        top.First().ShouldBe("/workspace/repo/target.cs");
        top.Count.ShouldBeLessThanOrEqualTo(5);
    }
}

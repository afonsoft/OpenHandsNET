using System.Reflection;

using NetArchTest.Rules;

using OpenHandsNET.Domain.Common;

namespace OpenHandsNET.UnitTests.Architecture;

/// <summary>
/// Enforces the Clean Architecture dependency rules from SPEC-01 §3:
/// Domain (no refs) &lt;- Application &lt;- Infrastructure &lt;- Api;
/// Client → Application; Cli → HTTP contracts only.
/// </summary>
public class DependencyRulesTests
{
    private static readonly Assembly Domain = typeof(Entity).Assembly;
    private static readonly Assembly Application = typeof(Application.Contracts.IEventService).Assembly;
    private static readonly Assembly Infrastructure = typeof(Infrastructure.Persistence.AppDbContext).Assembly;
    private static readonly Assembly Client = typeof(Client.Pages.Home).Assembly;

    // Covers AC-03: a reverse dependency must fail the build.
    [Fact]
    public void Domain_Should_Not_Depend_On_Higher_Layers()
    {
        var result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny(
                "OpenHandsNET.Application", "OpenHandsNET.Infrastructure",
                "OpenHandsNET.Api", "OpenHandsNET.Client")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(FormatFailures(result));
    }

    [Fact]
    public void Application_Should_Not_Depend_On_Infrastructure_Or_Api()
    {
        var result = Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOnAny("OpenHandsNET.Infrastructure", "OpenHandsNET.Api")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(FormatFailures(result));
    }

    [Fact]
    public void Infrastructure_Should_Not_Depend_On_Api()
    {
        var result = Types.InAssembly(Infrastructure)
            .ShouldNot()
            .HaveDependencyOn("OpenHandsNET.Api")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(FormatFailures(result));
    }

    [Fact]
    public void Client_Should_Not_Depend_On_Infrastructure()
    {
        var result = Types.InAssembly(Client)
            .ShouldNot()
            .HaveDependencyOn("OpenHandsNET.Infrastructure")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(FormatFailures(result));
    }

    [Fact]
    public void Cli_Should_Not_ProjectReference_Api_Or_Infrastructure()
    {
        var csproj = Path.Combine(FindRepoRoot(), "cli", "OpenHandsNET.Cli", "OpenHandsNET.Cli.csproj");
        File.Exists(csproj).ShouldBeTrue($"CLI csproj not found at {csproj}");
        var content = File.ReadAllText(csproj);
        content.ShouldNotContain("OpenHandsNET.Api");
        content.ShouldNotContain("OpenHandsNET.Infrastructure");
        content.ShouldNotContain("OpenHandsNET.Application");
        content.ShouldNotContain("OpenHandsNET.Domain");
    }

    private static string FormatFailures(TestResult result) =>
        result.IsSuccessful ? string.Empty
            : string.Join(", ", result.FailingTypeNames ?? []);

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "OpenHandsNET.slnx")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        return dir ?? throw new DirectoryNotFoundException("Repository root (OpenHandsNET.slnx) not found");
    }
}
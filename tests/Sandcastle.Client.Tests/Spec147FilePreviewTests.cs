using Sandcastle.Client.Components.Conversation;
using Sandcastle.Client.Services;

using Shouldly;

namespace Sandcastle.Client.Tests;

/// <summary>
/// SPEC-147 RF-003 — mapeamento de linguagem do viewer:
/// .md/.markdown → markdown (toggle Rendered/Source) e
/// .patch/.diff → diff (Monarch mínimo + fallback .diff-line).
/// </summary>
public sealed class Spec147FilePreviewTests
{
    [Theory]
    [InlineData("README.md")]
    [InlineData("docs/guide.markdown")]
    public void IsMarkdown_MdExtensions_True(string path) =>
        FilesTab.IsMarkdown(path).ShouldBeTrue();

    [Theory]
    [InlineData("file.cs")]
    [InlineData("changes.patch")]
    public void IsMarkdown_OtherExtensions_False(string path) =>
        FilesTab.IsMarkdown(path).ShouldBeFalse();

    [Theory]
    [InlineData("changes.patch")]
    [InlineData("out.diff")]
    public void IsPatch_DiffExtensions_True(string path) =>
        FilesTab.IsPatch(path).ShouldBeTrue();

    [Theory]
    [InlineData("file.md")]
    [InlineData("file.cs")]
    public void IsPatch_OtherExtensions_False(string path) =>
        FilesTab.IsPatch(path).ShouldBeFalse();

    [Theory]
    [InlineData("a.md", "markdown")]
    [InlineData("a.markdown", "markdown")]
    [InlineData("a.mdx", "markdown")]
    [InlineData("a.patch", "diff")]
    [InlineData("a.diff", "diff")]
    [InlineData("a.cs", "csharp")]
    public void LanguageFor_MapsPreviewLanguages(string path, string lang) =>
        MonacoInterop.LanguageFor(path).ShouldBe(lang);
}
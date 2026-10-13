using Bunit;
using OpenHandsNET.Client.Pages;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// Smoke tests for the Blazor WASM client shell (SPEC-01 RF-001).
/// </summary>
public class HomePageTests : TestContext
{
    [Fact]
    public void Home_Renders_Page_Title()
    {
        var cut = RenderComponent<Home>();
        cut.Markup.ShouldContain("Hello, world!");
    }
}

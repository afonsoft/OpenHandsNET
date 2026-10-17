using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using OpenHandsNET.Client.Components;
using OpenHandsNET.Client.Services;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// SPEC-26 AC: markdown sanitizado, fuzzy da palette, toasts, ciclo de
/// tema, ícones SVG, host de toasts, banner offline.
/// </summary>
public class Spec26UiTests : TestContext
{
    public Spec26UiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
    }

    // ---------- MarkdownService ----------

    [Fact]
    public void Markdown_Renders_Code_And_Headings()
    {
        var md = new MarkdownService();
        var html = md.Render("# title\n\n```cs\nvar x = 1;\n```\n\n- a\n- b")
            .ToString();
        html.ShouldContain("<h1");
        html.ShouldContain("<pre");
        html.ShouldContain("<li");
    }

    [Fact]
    public void Markdown_Strips_Script_And_OnClick()
    {
        var md = new MarkdownService();
        // DisableHtml já remove HTML cru — injeção via link javascript: e
        // spans escapados também precisam sair.
        var html = md.Render(
            "[x](javascript:alert(1))\n\n<script>alert(2)</script>").ToString();
        html.ShouldNotContain("<script");
        html.ShouldNotContain("javascript:");
    }

    [Fact]
    public void Markdown_Empty_Returns_Empty()
    {
        new MarkdownService().Render("").ToString().ShouldBe("");
        new MarkdownService().Render(null).ToString().ShouldBe("");
    }

    // ---------- PaletteService ----------

    [Fact]
    public async Task Palette_Fuzzy_Filters_And_Invokes()
    {
        var pal = new PaletteService();
        var invoked = "";
        pal.Provider = () => Task.FromResult<IEnumerable<PaletteService.Item>>(
        [
            new("a", "Nova conversa", null, "plus",
                () => { invoked = "a"; return Task.CompletedTask; }),
            new("b", "Settings", null, "settings",
                () => { invoked = "b"; return Task.CompletedTask; }),
            new("c", "Administration", null, "shield",
                () => { invoked = "c"; return Task.CompletedTask; }),
        ]);

        await pal.OpenAsync();
        pal.SetQuery("adm");
        pal.Filtered.Count.ShouldBe(1);
        pal.Filtered[0].Label.ShouldBe("Administration");
        await pal.InvokeSelectedAsync();
        invoked.ShouldBe("c");
        pal.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public void Palette_Matches_Subsequence()
    {
        PaletteService.Matches("nc", "Nova conversa").ShouldBeTrue();
        PaletteService.Matches("xyz", "Nova conversa").ShouldBeFalse();
        PaletteService.Matches("", "qualquer").ShouldBeTrue();
    }

    // ---------- ToastService ----------

    [Fact]
    public void Toasts_Push_And_Dismiss()
    {
        var t = new ToastService();
        var fired = 0;
        t.Changed += () => fired++;
        t.Error("boom");
        t.Toasts.Count.ShouldBe(1);
        t.Toasts[0].Severity.ShouldBe(ToastService.Level.Error);
        t.Dismiss(t.Toasts[0].Id);
        t.Toasts.Count.ShouldBe(0);
        fired.ShouldBe(2);
    }

    [Fact]
    public void Toasts_Cap_At_Five()
    {
        var t = new ToastService();
        for (var i = 0; i < 8; i++)
        {
            t.Info($"m{i}");
        }
        t.Toasts.Count.ShouldBe(5);
        t.Toasts[^1].Message.ShouldBe("m7");
    }

    // ---------- ThemeService ----------

    [Fact]
    public async Task Theme_Cycles_And_Persists_Local()
    {
        var api = new OpenHandsNetApiClient(new HttpClient
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        var theme = new ThemeService(JSInterop.JSRuntime, api);
        await theme.InitAsync();
        theme.Theme.ShouldBe(ThemeService.Dark);
        await theme.SetAsync(ThemeService.Light);
        theme.Theme.ShouldBe(ThemeService.Light);
        theme.EffectiveTheme.ShouldBe(ThemeService.Light);
        JSInterop.VerifyInvoke("ohnTheme.set");
    }

    [Fact]
    public async Task Theme_Invalid_Value_Falls_Back_To_Dark()
    {
        var api = new OpenHandsNetApiClient(new HttpClient
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        var theme = new ThemeService(JSInterop.JSRuntime, api);
        await theme.SetAsync("rainbow");
        theme.Theme.ShouldBe(ThemeService.Dark);
    }

    // ---------- Icon ----------

    [Fact]
    public void Icon_Renders_Svg_With_Known_Path()
    {
        var cut = RenderComponent<Icon>(p => p.Add(x => x.Name, "terminal"));
        var svg = cut.Find("svg.ohn-icon");
        svg.GetAttribute("viewBox").ShouldBe("0 0 24 24");
        cut.Markup.ShouldContain("<path");
    }

    [Fact]
    public void Icon_Unknown_Name_Falls_Back_To_Info()
    {
        var cut = RenderComponent<Icon>(p => p.Add(x => x.Name, "inexistente"));
        cut.Markup.ShouldContain("<circle"); // ícone info
    }

    // ---------- ToastHost ----------

    [Fact]
    public void ToastHost_Renders_And_Dismiss_Button_Works()
    {
        var toasts = new ToastService();
        Services.AddSingleton(toasts);
        var cut = RenderComponent<ToastHost>();
        toasts.Warn("atenção");
        cut.Find(".toast.warn .msg").TextContent.ShouldContain("atenção");
        cut.Find(".toast .x").Click();
        toasts.Toasts.ShouldBeEmpty();
    }
}

using System.Net;
using System.Text;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Client.Components;
using OpenHandsNET.Client.Services;

using Shouldly;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// SPEC-45: composer — slash expansion, send text, chips de mention,
/// popup @ com árvore do workspace (stub <c>/exec find</c>).
/// </summary>
public class Spec45ComposerTests : TestContext
{
    public Spec45ComposerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
    }
    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(fn(request));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8,
                "application/json"),
        };

    private static OpenHandsNetApiClient Api(
        Func<HttpRequestMessage, HttpResponseMessage> fn) =>
        new(new HttpClient(new StubHandler(fn))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });

    private void RegisterDeps(OpenHandsNetApiClient api)
    {
        Services.AddSingleton(api);
        Services.AddSingleton(new ToastService());
    }

    // ---- ComposerSlash helpers -----------------------------------------

    [Fact]
    public void Expand_Substitutes_Arguments_And_Positionals()
    {
        var cmd = new ComposerSlash.Command(
            "review", "rev", ComposerSlash.CommandKind.Skill,
            "Review $1 against $2 — notes: $ARGUMENTS");
        ComposerSlash.Expand(cmd, "main strict")
            .ShouldBe("Review main against strict — notes: main strict");
    }

    [Fact]
    public void Expand_Without_Template_Returns_Literal()
    {
        var cmd = new ComposerSlash.Command(
            "x", "x", ComposerSlash.CommandKind.Skill, null);
        ComposerSlash.Expand(cmd, "a b").ShouldBe("/x a b");
        ComposerSlash.Expand(cmd, "").ShouldBe("/x");
    }

    // ---- Render + send --------------------------------------------------

    [Fact]
    public void Composer_Renders_And_Sends_Text()
    {
        RegisterDeps(Api(_ => Json("[]")));
        string? sent = null;
        var cut = RenderComponent<ChatComposer>(p => p
            .Add(x => x.ConversationId, "conv-1")
            .Add(x => x.OnSend, (string t) => sent = t));

        cut.Find("textarea").Input("hello world");
        cut.Find(".send-btn").Click();
        sent.ShouldBe("hello world");
        var v = cut.Find("textarea").GetAttribute("value");
        (v is null || v == "").ShouldBeTrue();
    }

    // ---- @mention popup --------------------------------------------------

    [Fact]
    public void Mention_Opens_Popup_From_Find_Output()
    {
        var api = Api(req => req.RequestUri!.AbsolutePath
            .EndsWith("/exec")
            ? Json("""{"exit_code":0,"stdout":"/workspace/repo/a.cs\n/workspace/repo/b.ts\n","stderr":"","timed_out":false}""")
            : Json("[]"));
        RegisterDeps(api);
        var cut = RenderComponent<ChatComposer>(p => p
            .Add(x => x.ConversationId, "conv-1")
            .Add(x => x.OnSend, _ => { }));

        cut.Find("textarea").Input("check @a.");
        cut.WaitForAssertion(() =>
            cut.FindAll(".popup-item").Count.ShouldBeGreaterThan(0),
            TimeSpan.FromSeconds(3));
        // Seleciona → o token vira @path completo.
        cut.Find(".popup-item").Click();
        cut.Find("textarea").GetAttribute("value")!
            .ShouldContain("@/workspace/repo/a.cs");
    }

    // ---- /slash popup ----------------------------------------------------

    [Fact]
    public void Slash_Opens_Popup_With_Builtins()
    {
        var api = Api(_ => Json("[]"));
        RegisterDeps(api);
        var cut = RenderComponent<ChatComposer>(p => p
            .Add(x => x.ConversationId, "conv-1")
            .Add(x => x.OnSend, _ => { }));

        cut.Find("textarea").Input("/p");
        cut.WaitForAssertion(() =>
            cut.FindAll(".popup-item").Count.ShouldBeGreaterThan(0),
            TimeSpan.FromSeconds(3));
        cut.Markup.ShouldContain("/plan");
    }

    // ---- skill expansion no send ----------------------------------------

    [Fact]
    public void Slash_Skill_Expands_On_Send()
    {
        var skillsJson = """
            [{"id":"s1","name":"review","description":"d",
              "content_md":"Review $ARGUMENTS now","source":"repo"}]
            """;
        var api = Api(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/skills")
                ? Json(skillsJson) : Json("[]"));
        RegisterDeps(api);
        string? sent = null;
        var cut = RenderComponent<ChatComposer>(p => p
            .Add(x => x.ConversationId, "conv-1")
            .Add(x => x.OnSend, (string t) => sent = t));

        // Carrega o cache de skills via popup.
        cut.Find("textarea").Input("/r");
        cut.WaitForAssertion(() =>
            cut.FindAll(".popup-item").Count.ShouldBeGreaterThan(0),
            TimeSpan.FromSeconds(3));
        // Digita args (popup fecha) e envia — expande o template.
        cut.Find("textarea").Input("/review src/foo.cs");
        cut.Find(".send-btn").Click();
        sent.ShouldBe("Review src/foo.cs now");
    }
}
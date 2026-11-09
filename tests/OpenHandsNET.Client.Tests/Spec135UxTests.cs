using System.Net;
using System.Text;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Client.Components;
using OpenHandsNET.Client.Components.Conversation;
using OpenHandsNET.Client.Services;

using Shouldly;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// SPEC-135: composer `/undo`, strip de hunks do DiffTab (revert +
/// slot desabilitado "comentar → agente") e menu de conversa com Fork.
/// </summary>
public class Spec135UxTests : BunitContext
{
    public Spec135UxTests()
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

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8,
                "text/plain"),
        };

    private static OpenHandsNetApiClient Api(
        Func<HttpRequestMessage, HttpResponseMessage> fn) =>
        new(new HttpClient(new StubHandler(fn))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });

    // RF-004: /undo builtin existe no popup e chama POST .../undo.
    [Fact]
    public void Slash_Undo_Builtin_Posts_Undo()
    {
        var undoCalls = 0;
        var api = Api(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/undo"))
            {
                req.Method.ShouldBe(HttpMethod.Post);
                undoCalls++;
                return Json(
                    """{"truncated_events":3,"files_reverted":[]}""");
            }

            return Json("[]");
        });
        Services.AddSingleton(api);
        Services.AddSingleton(new ToastService());

        var cut = Render<ChatComposer>(p => p
            .Add(x => x.ConversationId, "conv-1")
            .Add(x => x.OnSend, _ => { }));

        ComposerSlash.Builtins.Any(c => c.Name == "undo"
            && c.Kind == ComposerSlash.CommandKind.Undo)
            .ShouldBeTrue();

        cut.Find("textarea").Input("/undo");
        cut.Find("textarea").KeyDown("Enter");
        cut.WaitForAssertion(() => undoCalls.ShouldBe(1),
            TimeSpan.FromSeconds(3));
    }

    private const string SampleDiff = """
        diff --git a/a.cs b/a.cs
        index 111..222 100644
        --- a/a.cs
        +++ b/a.cs
        @@ -1,3 +1,3 @@
         a
        -b
        +B
         c
        @@ -10,2 +10,2 @@
         x
        -y
        +Y
        """;

    private static ConversationStateService StateWithDiff(
        out OpenHandsNetApiClient api, out Func<string, int> revertHits,
        out List<string> revertPaths)
    {
        var paths = new List<string>();
        revertPaths = paths;
        var hits = new System.Collections.Concurrent
            .ConcurrentDictionary<string, int>();
        revertHits = p => hits.TryGetValue(p, out var n) ? n : 0;
        api = new OpenHandsNetApiClient(new HttpClient(
            new StubHandler(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                var q = req.RequestUri.Query;
                if (path.EndsWith("/git/changes"))
                {
                    return Json("""{"files":["a.cs"]}""");
                }

                if (path.EndsWith("/git/diff"))
                {
                    return Text(SampleDiff);
                }

                if (path.EndsWith("/git/file")
                    || path.EndsWith("/file"))
                {
                    return Json("\"x\"");
                }

                if (path.EndsWith("/files/revert-hunks"))
                {
                    var body = req.Content!
                        .ReadAsStringAsync().Result;
                    body.ShouldContain("\"path\":\"a.cs\"");
                    paths.Add(body);
                    return Json("""{"applied":[1]}""");
                }

                return Json("[]");
            }))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        var state = new ConversationStateService(api,
            new ConversationHubClient(
                new Uri("http://localhost/hubs/conversations")));
        typeof(ConversationStateService)
            .GetProperty(nameof(ConversationStateService.Conversation))!
            .SetValue(state, new ConversationItem
            {
                Id = "conv-1",
                ExecutionStatus = "READY",
            });
        return state;
    }

    // RF-006: strip mostra 1 botão por hunk + slot desabilitado.
    [Fact]
    public void DiffTab_Hunk_Strip_Renders_Per_Hunk()
    {
        var state = StateWithDiff(out var api,
            out _, out _);
        Services.AddSingleton(api);
        Services.AddSingleton(new ToastService());
        Services.AddSingleton(new MonacoInterop(JSInterop.JSRuntime));

        var cut = Render<DiffTab>(p => p.Add(x => x.State, state));
        cut.WaitForAssertion(() =>
            cut.FindAll(".hunk-strip .hunk-btn").Count.ShouldBe(3),
            TimeSpan.FromSeconds(5));

        var btns = cut.FindAll(".hunk-strip .hunk-btn");
        btns[^1].HasAttribute("disabled").ShouldBeTrue(); // comentar slot
    }

    // RF-006: clicar num hunk → POST revert-hunks com o índice.
    [Fact]
    public void DiffTab_Revert_Button_Posts_Index()
    {
        var state = StateWithDiff(out var api,
            out _, out var paths);
        Services.AddSingleton(api);
        Services.AddSingleton(new ToastService());
        Services.AddSingleton(new MonacoInterop(JSInterop.JSRuntime));

        var cut = Render<DiffTab>(p => p.Add(x => x.State, state));
        cut.WaitForAssertion(() =>
            cut.FindAll(".hunk-strip .hunk-btn").Count.ShouldBe(3),
            TimeSpan.FromSeconds(5));

        cut.FindAll(".hunk-strip .hunk-btn")[1].Click();
        cut.WaitForAssertion(() => paths.Count.ShouldBe(1),
            TimeSpan.FromSeconds(3));
        paths[0].ShouldContain("\"hunk_indexes\":[1]");
    }
}
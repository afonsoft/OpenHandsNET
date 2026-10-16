using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenHandsNET.Client.Components.Conversation;
using OpenHandsNET.Client.Services;
using Shouldly;

namespace OpenHandsNET.Client.Tests;

/// <summary>SPEC-43 RF-004: CheckpointList — render, expand, revert+409.</summary>
public class Spec43CheckpointListTests : TestContext
{
    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(fn(request));
    }

    private static OpenHandsNetApiClient Api(
        Func<HttpRequestMessage, HttpResponseMessage> fn)
    {
        var api = new OpenHandsNetApiClient(new HttpClient(
            new StubHandler(fn))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        return api;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Conflict(string body) =>
        new(HttpStatusCode.Conflict)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private const string ListJson = """
        [{"hash":"abc1234def","ts":1700000002,"label":"turn 2",
          "files_changed":1,"ins":3,"del":0},
         {"hash":"bbb2222ccc","ts":1700000001,"label":"turn 1",
          "files_changed":4,"ins":9,"del":2}]
        """;

    private IRenderedComponent<CheckpointList> Render(
        Func<HttpRequestMessage, HttpResponseMessage> fn)
    {
        Services.AddSingleton(Api(fn));
        return RenderComponent<CheckpointList>(p =>
            p.Add(x => x.ConversationId, "conv-1"));
    }

    [Fact]
    public void Lista_Renderiza_Stats_E_Labels()
    {
        var cut = Render(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/checkpoints")
                ? Json(ListJson) : Json("null"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("turn 2");
            cut.Markup.ShouldContain("turn 1");
            cut.Markup.ShouldContain("abc1234");
        });
    }

    [Fact]
    public void Empty_State_Sem_Checkpoints()
    {
        var cut = Render(_ => Json("[]"));
        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("no checkpoints yet"));
    }

    [Fact]
    public void Revert_409_Mostra_Conflictos_E_Force()
    {
        var reverted = false;
        var cut = Render(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/checkpoints") && req.Method == HttpMethod.Get)
            {
                return Json(ListJson);
            }

            if (path.Contains("/revert") && req.Method == HttpMethod.Post)
            {
                return reverted
                    ? Json("""{"reverted":true,"conflicts":[],"untracked":[]}""")
                    : Conflict("""
                        {"error":"drift","conflicts":["src/a.cs"],
                         "untracked":["notes/n.txt"]}
                        """);
            }

            return Json("null");
        });

        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("abc1234"));

        // Click revert → modal abre (sem conflitos ainda).
        cut.Find(".ckpt-revert").Click();
        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("Revert to abc1234"));

        // Confirma → 409 drift → modal lista conflitos e oferece force.
        cut.Find(".modal-actions .primary").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("src/a.cs");
            cut.Markup.ShouldContain("revert (force)");
        });

        // Segunda confirmação (force) → revertido, modal fecha.
        // ("Revert to" sempre existe nos aria-labels dos botões —
        // assere o backdrop do modal.)
        reverted = true;
        cut.Find(".modal-actions .primary").Click();
        cut.WaitForAssertion(() =>
            cut.FindAll(".modal-backdrop").ShouldBeEmpty());
    }
}

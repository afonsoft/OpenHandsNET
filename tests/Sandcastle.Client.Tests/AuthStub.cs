using System.Net;
using System.Text;

using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Services;

namespace Sandcastle.Client.Tests;

/// <summary>
/// SPEC-132: stub de <see cref="AuthStateService"/> para fixtures bUnit —
/// sessão <c>provider=none</c> (modo local), já inicializada. Registrado
/// via factory lazy para não congelar o DI antes das outras chamadas
/// de <c>AddSingleton</c> do fixture.
/// </summary>
public static class AuthStub
{
    private sealed class SessionHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8,
                    "application/json"),
            });
    }

    /// <summary>Registra o serviço (sessão none/anônima por default).</summary>
    public static void Register(BunitContext ctx,
        string sessionJson =
            """{"provider":"none","authenticated":false,"user":null}""") =>
        ctx.Services.AddSingleton(sp =>
        {
            var nav = sp.GetRequiredService<NavigationManager>();
            var http = new HttpClient(new SessionHandler(sessionJson))
            {
                BaseAddress = new Uri("http://localhost:8080"),
            };
            var svc = new AuthStateService(http, nav);
            svc.InitializeAsync().GetAwaiter().GetResult();
            return svc;
        });
}
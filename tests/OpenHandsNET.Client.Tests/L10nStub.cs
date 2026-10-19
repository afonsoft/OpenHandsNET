using System.Net;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Client.Services;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// SPEC-46: stub de <see cref="LocalizationService"/> para fixtures bUnit —
/// serve os dicionários reais de <c>wwwroot/i18n</c> via handler em
/// memória, já inicializado (pt-BR ativo).
/// </summary>
public static class L10nStub
{
    private sealed class DiskHandler(string i18nDir) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (path.StartsWith("i18n/"))
            {
                var file = Path.Combine(i18nDir, path["i18n/".Length..]);
                if (File.Exists(file))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            File.ReadAllText(file), System.Text.Encoding.UTF8,
                            "application/json"),
                    });
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static string I18nDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var c = Path.Combine(dir.FullName,
                "src", "OpenHandsNET.Client", "wwwroot", "i18n");
            if (Directory.Exists(c)) { return c; }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("wwwroot/i18n não encontrado");
    }

    /// <summary>Cria serviço inicializado com os JSONs reais.</summary>
    public static LocalizationService Create(TestContext ctx)
    {
        var http = new HttpClient(new DiskHandler(I18nDir()))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        };
        var svc = new LocalizationService(http, ctx.JSInterop.JSRuntime);
        svc.InitializeAsync().GetAwaiter().GetResult();
        return svc;
    }

    /// <summary>Registra o stub no DI do <see cref="TestContext"/>.</summary>
    public static LocalizationService Register(TestContext ctx)
    {
        var svc = Create(ctx);
        ctx.Services.AddSingleton(svc);
        return svc;
    }
}
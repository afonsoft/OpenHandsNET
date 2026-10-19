using System.Net;
using System.Text;

using Microsoft.JSInterop;

using OpenHandsNET.Client.Services;

using Shouldly;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// SPEC-46 RF-001: <see cref="LocalizationService"/> — cadeia de
/// fallback <c>active → en-US → pt-BR → key</c>, interpolação
/// <c>{{name}}</c>, evento <c>Changed</c> e init com default pt-BR.
/// </summary>
public sealed class I18nServiceTests
{
    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(fn(request));
    }

    private sealed class StubJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(
            string identifier, object?[]? args) => new(default(T)!);
        public ValueTask<T> InvokeAsync<T>(
            string identifier, CancellationToken cancellationToken,
            object?[]? args) => new(default(T)!);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8,
                "application/json"),
        };

    /// <summary>HttpClient cujos i18n/{lang}.json vêm do dicionário.</summary>
    private static LocalizationService NewService(
        Dictionary<string, string> dicts)
    {
        var http = new HttpClient(new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath.TrimStart('/');
            if (path == "i18n/locales.json")
            {
                return Json("""{"locales":[{"code":"pt-BR","name":"PT"},{"code":"en-US","name":"EN"},{"code":"ja-JP","name":"JA"}]}""");
            }
            if (path.StartsWith("i18n/") && path.EndsWith(".json")
                && dicts.TryGetValue(path["i18n/".Length..^5], out var d))
            {
                return Json(d);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        };
        return new LocalizationService(http, new StubJs());
    }

    private static Dictionary<string, string> BaseDicts() => new()
    {
        ["pt-BR"] = """{"a":"pt-A","pt_only":"só-pt","int":"Olá {{name}}"}""",
        ["en-US"] = """{"a":"en-A","en_only":"en-only","int":"Hi {{name}}"}""",
        ["ja-JP"] = """{"a":"ja-A"}""",
    };

    [Fact]
    public async Task Default_Language_Is_PtBr()
    {
        var l = NewService(BaseDicts());
        await l.InitializeAsync();
        l.Language.ShouldBe("pt-BR");
        l.T("a").ShouldBe("pt-A");
    }

    [Fact]
    public async Task Fallback_Chain_Active_En_Pt_Key()
    {
        var l = NewService(BaseDicts());
        await l.InitializeAsync();
        await l.SetLanguageAsync("ja-JP");
        l.T("a").ShouldBe("ja-A");            // active
        l.T("en_only").ShouldBe("en-only");   // en-US fallback
        l.T("pt_only").ShouldBe("só-pt");     // pt-BR fallback
        l.T("missing.key").ShouldBe("missing.key"); // própria chave
    }

    [Fact]
    public async Task Interpolation_Replaces_Named_Placeholders()
    {
        var l = NewService(BaseDicts());
        await l.InitializeAsync();
        l.T("int", ("name", "Dev")).ShouldBe("Olá Dev");
    }

    [Fact]
    public async Task SetLanguage_Raises_Changed()
    {
        var l = NewService(BaseDicts());
        await l.InitializeAsync();
        var fired = 0;
        l.Changed += () => fired++;
        await l.SetLanguageAsync("en-US");
        fired.ShouldBe(1);
        l.T("a").ShouldBe("en-A");
    }

    [Fact]
    public async Task Unknown_Language_Falls_Back_To_Default()
    {
        var l = NewService(BaseDicts());
        await l.InitializeAsync();
        await l.SetLanguageAsync("xx-XX");
        l.Language.ShouldBe("pt-BR");
    }

    [Fact]
    public async Task Culture_Matches_Active_Language()
    {
        var l = NewService(BaseDicts());
        await l.InitializeAsync();
        l.Culture.Name.ShouldBe("pt-BR");
        await l.SetLanguageAsync("en-US");
        l.Culture.Name.ShouldBe("en-US");
    }
}
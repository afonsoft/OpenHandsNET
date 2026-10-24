using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenHandsNET.IntegrationTests;

/// <summary>
/// SPEC-97: com a FallbackPolicy global, endpoints sem AllowAnonymous
/// exigem sessão. Helper central: dev-login (provider=none) num client
/// com cookies — substitui o antigo "client anônimo" dos testes que
/// dependiam do acesso sem auth (o próprio bug que a SPEC corrige).
/// </summary>
public static class TestAuth
{
    /// <summary>Cria client autenticado via dev-login (cookie).</summary>
    public static async Task<HttpClient> LoginAsync(
        WebApplicationFactory<Program> factory, string email = "test@local",
        WebApplicationFactoryClientOptions? options = null)
    {
        options ??= new WebApplicationFactoryClientOptions();
        options.HandleCookies = true;
        var http = factory.CreateClient(options);
        using var res = await http.PostAsJsonAsync(
            "/api/v1/auth/dev-login", new { email, displayName = email });
        res.EnsureSuccessStatusCode();
        return http;
    }

    /// <summary>Versão síncrona p/ ctors de fixture (TestServer não
    /// tem SynchronizationContext — GetAwaiter().GetResult() é seguro
    /// aqui).</summary>
    public static HttpClient Login(
        WebApplicationFactory<Program> factory, string email = "test@local",
        WebApplicationFactoryClientOptions? options = null) =>
        LoginAsync(factory, email, options).GetAwaiter().GetResult();
}
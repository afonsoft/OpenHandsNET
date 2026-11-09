namespace OpenHandsNET.Client.Services;

/// <summary>
/// SPEC-132 RF-005: handler do HttpClient principal — resposta 401 numa
/// chamada autenticada (fora <c>/api/v1/auth/*</c>) dispara
/// <see cref="AuthStateService.HandleUnauthorizedAsync"/>: a sessão
/// expirou e o app sai do estado zombie (API rejeitando tudo).
/// </summary>
public sealed class AuthRedirectHandler(Func<Task> onUnauthorized)
    : DelegatingHandler
{
    public AuthRedirectHandler(Func<Task> onUnauthorized,
        HttpMessageHandler inner)
        : this(onUnauthorized) => InnerHandler = inner;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var resp = await base.SendAsync(request, cancellationToken);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && !IsAuthPath(request.RequestUri))
        {
            await onUnauthorized();
        }

        return resp;
    }

    private static bool IsAuthPath(Uri? uri) =>
        uri?.AbsolutePath.StartsWith("/api/v1/auth",
            StringComparison.OrdinalIgnoreCase) == true;
}
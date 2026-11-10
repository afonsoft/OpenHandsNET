using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Components;

namespace Sandcastle.Client.Services;

/// <summary>Perfil público da sessão (payload de <c>/api/v1/auth/session</c>).</summary>
public sealed record SessionUser(
    string Email, string? DisplayName, bool IsSuperAdmin);

/// <summary>
/// SPEC-132 RF-001: estado de auth do client. O boot
/// (<see cref="InitializeAsync"/>) chama o probe anônimo
/// <c>GET /api/v1/auth/session</c> uma única vez e resolve
/// <see cref="Provider"/>/<see cref="User"/>. Com
/// <c>provider=none</c> o app se comporta como antes (sem login).
/// </summary>
public sealed class AuthStateService(HttpClient http, NavigationManager nav)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private Task? _init;
    private bool _forceSignedOut;

    /// <summary>Notifica a shell (menu/avatar) para re-render.</summary>
    public event Action? Changed;

    /// <summary>Probe concluído — MainLayout pode montar a shell.</summary>
    public bool Initialized { get; private set; }

    /// <summary><c>Auth:Provider</c> do servidor (none|local|oidc…).</summary>
    public string Provider { get; private set; } = "none";

    /// <summary>Usuário da sessão atual (null = anônimo).</summary>
    public SessionUser? User { get; private set; }

    /// <summary>Sessão autenticada no momento.</summary>
    public bool IsAuthenticated => User is not null;

    /// <summary>RF-006: UI deve exigir sessão (provider != none).</summary>
    public bool RequiresAuth =>
        !Provider.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>Boot: probe único (idempotente — chamadas concorrentes
    /// compartilham a mesma task).</summary>
    public Task InitializeAsync() => _init ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            var doc = await http.GetFromJsonAsync<JsonElement>(
                "/api/v1/auth/session", Json);
            Provider = doc.TryGetProperty("provider", out var p)
                && p.ValueKind == JsonValueKind.String
                    ? p.GetString() ?? "none"
                    : "none";
            User = ParseUser(doc);
            _forceSignedOut = false;
        }
        catch (Exception)
        {
            // API indisponível no boot: assume none (comportamento atual).
            Provider = "none";
        }

        Initialized = true;
        Changed?.Invoke();
    }

    private static SessionUser? ParseUser(JsonElement doc)
    {
        if (!doc.TryGetProperty("user", out var u)
            || u.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var email = u.TryGetProperty("email", out var e)
            ? e.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(email))
        {
            return null;
        }

        var display = u.TryGetProperty("display_name", out var d)
            && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        var admin = u.TryGetProperty("is_super_admin", out var a)
            && a.ValueKind == JsonValueKind.True;
        return new SessionUser(email, display, admin);
    }

    /// <summary>RF-002: sanitiza returnUrl — só paths internos
    /// (<c>/x/y?z</c>), nunca absolute/scheme-relative (open-redirect).</summary>
    public static string SanitizeReturnUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048)
        {
            return "/";
        }

        url = url.Trim();
        // Deve começar com '/', não pode ser '//' (scheme-relative)
        // nem conter scheme/netloc ou controles.
        if (!url.StartsWith('/') || url.StartsWith("//")
            || url.StartsWith("/\\") || url.Contains("://")
            || url.Contains('\\')
            || url.Any(char.IsControl))
        {
            return "/";
        }

        return url;
    }

    /// <summary>Rotas que não exigem sessão (path relativo à base).</summary>
    public static bool IsPublicPath(string relativePath)
    {
        var path = relativePath.Split(new[] { '?', '#' })[0];
        return path.Equals("auth", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("auth/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("share/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("shared/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>RF-005: chamado pelo handler de 401 — marca a sessão como
    /// perdida e navega para /auth preservando a rota atual.</summary>
    public Task HandleUnauthorizedAsync()
    {
        if (!RequiresAuth || _forceSignedOut)
        {
            return Task.CompletedTask;
        }

        _forceSignedOut = true;
        User = null;
        Changed?.Invoke();
        var current = ToRelative(nav.Uri);
        if (!IsPublicPath(current))
        {
            nav.NavigateTo(
                $"/auth?returnUrl={Uri.EscapeDataString("/" + current)}");
        }

        return Task.CompletedTask;
    }

    /// <summary>Login e-mail (providers none/local) — dev-login.</summary>
    public async Task<bool> DevLoginAsync(string email, string? displayName,
        CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email, displayName = displayName ?? email }, Json, ct);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            return false;
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        User = new SessionUser(
            doc.TryGetProperty("email", out var e)
                ? e.GetString() ?? email : email,
            displayName, false);
        _forceSignedOut = false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>RF-004: sign-out — cookie morto no servidor; o reload leva
    /// à tela /auth (re-probe).</summary>
    public async Task LogoutAsync()
    {
        try
        {
            await http.PostAsync("/api/v1/auth/logout", null);
        }
        catch (Exception) { /* mesmo offline, força o re-probe */ }

        nav.NavigateTo("/auth", forceLoad: true);
    }

    private string ToRelative(string uri)
    {
        var rel = nav.ToBaseRelativePath(uri);
        return rel.TrimStart('/');
    }
}
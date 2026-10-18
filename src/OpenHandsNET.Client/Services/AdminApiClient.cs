using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenHandsNET.Client.Services;

/// <summary>
/// SPEC-21 RF-003: wrappers tipados dos endpoints admin (`/api/v1/admin/*`,
/// usage e support-bundle) sobre o <see cref="HttpClient"/> autenticado
/// (cookie same-origin). 401/403 propagam como <see cref="AdminDeniedException"/>
/// para as páginas mostrarem mensagem amigável.
/// </summary>
public sealed class AdminApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    // ---- gate ----------------------------------------------------------

    /// <summary>GET /admin/super-admins/me → flag (false em 401/403/404).</summary>
    public async Task<bool> IsSuperAdminAsync(CancellationToken ct = default)
    {
        try
        {
            var doc = await http.GetFromJsonAsync<JsonElement>(
                "/api/v1/admin/super-admins/me", Json, ct);
            return doc.TryGetProperty("is_super_admin", out var v)
                && v.GetBoolean();
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    // ---- super admins --------------------------------------------------

    public async Task<IReadOnlyList<JsonElement>> ListSuperAdminsAsync(
        CancellationToken ct = default) =>
        await GetAsync<List<JsonElement>>("/api/v1/admin/super-admins", ct)
            ?? [];

    /// <summary>Grant por email. Retorna null quando o email não existe.</summary>
    public async Task<bool> GrantSuperAdminAsync(
        string email, CancellationToken ct = default)
    {
        var resp = Guard(await http.PostAsJsonAsync(
            "/api/v1/admin/super-admins", new { email }, Json, ct));
        return resp.StatusCode != HttpStatusCode.NotFound;
    }

    /// <summary>Revoke; <paramref name="conflict"/> = detalhe do 409 (last admin).</summary>
    public async Task<bool> RevokeSuperAdminAsync(
        string userId, Action<string>? conflict, CancellationToken ct = default)
    {
        var resp = Guard(await http.DeleteAsync(
            $"/api/v1/admin/super-admins/{userId}", ct));
        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            conflict?.Invoke(await DetailAsync(resp, ct));
            return false;
        }

        return resp.IsSuccessStatusCode;
    }

    // ---- users ---------------------------------------------------------

    public async Task<IReadOnlyList<JsonElement>> ListUsersAsync(
        string? query, int page, CancellationToken ct = default) =>
        await GetAsync<List<JsonElement>>(
            $"/api/v1/admin/users?page={page}"
            + (string.IsNullOrWhiteSpace(query)
                ? "" : $"&q={Uri.EscapeDataString(query)}"), ct) ?? [];

    public async Task<int> CountUsersAsync(CancellationToken ct = default)
    {
        var doc = await GetAsync<JsonElement>("/api/v1/admin/users/count", ct);
        return doc.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
    }

    /// <summary>Delete user; 409 (last super admin) → <paramref name="conflict"/>.</summary>
    public async Task<bool> DeleteUserAsync(
        string userId, Action<string>? conflict, CancellationToken ct = default)
    {
        var resp = Guard(await http.DeleteAsync(
            $"/api/v1/admin/users/{userId}", ct));
        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            conflict?.Invoke(await DetailAsync(resp, ct));
            return false;
        }

        return resp.IsSuccessStatusCode;
    }

    // ---- audit ---------------------------------------------------------

    public async Task<(IReadOnlyList<JsonElement> Items, int Total)>
        QueryAuditAsync(string? user, string? action,
            DateTimeOffset? from, DateTimeOffset? to, int page,
            CancellationToken ct = default)
    {
        var url = "/api/v1/admin/audit?page=" + page
            + (string.IsNullOrWhiteSpace(user)
                ? "" : $"&user={Uri.EscapeDataString(user)}")
            + (string.IsNullOrWhiteSpace(action)
                ? "" : $"&action={Uri.EscapeDataString(action)}")
            + (from is null ? "" : $"&from={from.Value:O}")
            + (to is null ? "" : $"&to={to.Value:O}");
        var doc = await GetAsync<JsonElement>(url, ct);
        var items = doc.TryGetProperty("items", out var arr)
            ? arr.EnumerateArray().Select(e => e.Clone()).ToList()
            : [];
        var total = doc.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
        return (items, total);
    }

    /// <summary>Mesma query como URL de export CSV (download same-origin).</summary>
    public static string AuditCsvUrl(
        string? user, string? action, DateTimeOffset? from,
        DateTimeOffset? to) =>
        "/api/v1/admin/audit?format=csv"
            + (string.IsNullOrWhiteSpace(user)
                ? "" : $"&user={Uri.EscapeDataString(user)}")
            + (string.IsNullOrWhiteSpace(action)
                ? "" : $"&action={Uri.EscapeDataString(action)}")
            + (from is null ? "" : $"&from={from.Value:O}")
            + (to is null ? "" : $"&to={to.Value:O}");

    /// <summary>Download do support-bundle (zip autenticado por cookie).</summary>
    public const string SupportBundleUrl = "/api/v1/support-bundle";

    // ---- feature flags -------------------------------------------------

    public async Task<IReadOnlyList<JsonElement>> ListFlagsAsync(
        CancellationToken ct = default) =>
        await GetAsync<List<JsonElement>>("/api/v1/admin/feature-flags", ct)
            ?? [];

    public async Task SetFlagAsync(
        string name, bool enabled, string? description,
        CancellationToken ct = default) =>
        Guard(await http.PutAsJsonAsync(
            $"/api/v1/admin/feature-flags/{Uri.EscapeDataString(name)}",
            new { enabled, description }, Json, ct));

    public async Task DeleteFlagAsync(string name, CancellationToken ct = default) =>
        Guard(await http.DeleteAsync(
            $"/api/v1/admin/feature-flags/{Uri.EscapeDataString(name)}", ct));

    // ---- plans (SPEC-20) ------------------------------------------------

    public async Task<IReadOnlyList<JsonElement>> ListPlansAsync(
        CancellationToken ct = default) =>
        await GetAsync<List<JsonElement>>("/api/v1/admin/plans", ct) ?? [];

    public async Task<JsonElement> CreatePlanAsync(
        string name, object? limits, decimal? monthlyPriceUsd,
        CancellationToken ct = default)
    {
        var resp = Guard(await http.PostAsJsonAsync(
            "/api/v1/admin/plans",
            new { name, limits, monthly_price_usd = monthlyPriceUsd },
            Json, ct));
        return await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
    }

    public async Task<JsonElement> PatchPlanAsync(
        string id, string? name, object? limits, decimal? monthlyPriceUsd,
        CancellationToken ct = default)
    {
        var resp = Guard(await http.PatchAsJsonAsync(
            $"/api/v1/admin/plans/{id}",
            new { name, limits, monthly_price_usd = monthlyPriceUsd },
            Json, ct));
        return await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
    }

    /// <summary>Delete plan; 409 (em uso) → <paramref name="conflict"/>.</summary>
    public async Task<bool> DeletePlanAsync(
        string id, Action<string>? conflict, CancellationToken ct = default)
    {
        var resp = Guard(await http.DeleteAsync(
            $"/api/v1/admin/plans/{id}", ct));
        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            conflict?.Invoke(await DetailAsync(resp, ct));
            return false;
        }

        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> AssignOrgPlanAsync(
        string orgId, string planId, CancellationToken ct = default)
    {
        var resp = Guard(await http.PutAsJsonAsync(
            $"/api/v1/admin/organizations/{orgId}/plan",
            new { plan_id = planId }, Json, ct));
        return resp.IsSuccessStatusCode;
    }

    // ---- orgs / usage ---------------------------------------------------

    /// <summary>Orgs do usuário (picker de usage/assign).</summary>
    public async Task<IReadOnlyList<JsonElement>> ListOrgsAsync(
        CancellationToken ct = default) =>
        await GetAsync<List<JsonElement>>("/api/v1/organizations", ct) ?? [];

    public async Task<JsonElement> GetOrgUsageAsync(
        string orgId, CancellationToken ct = default) =>
        await GetAsync<JsonElement>(
            $"/api/v1/organizations/{orgId}/usage", ct);

    public async Task<JsonElement> GetOrgPlanAsync(
        string orgId, CancellationToken ct = default) =>
        await GetAsync<JsonElement>(
            $"/api/v1/organizations/{orgId}/plan", ct);

    /// <summary>SPEC-48: política de segurança efetiva da org.</summary>
    public async Task<JsonElement> GetOrgSecurityPolicyAsync(
        string orgId, CancellationToken ct = default) =>
        await GetAsync<JsonElement>(
            $"/api/v1/organizations/{orgId}/security-policy", ct);

    /// <summary>
    /// SPEC-48: PUT da política — <paramref name="update"/> null reseta
    /// para <c>balanced</c>. Retorna (ok, policy?, error?).
    /// </summary>
    public async Task<(bool Ok, JsonElement? Policy, string? Error)>
        PutOrgSecurityPolicyAsync(
            string orgId, object? update, CancellationToken ct = default)
    {
        var resp = Guard(await http.PutAsJsonAsync(
            $"/api/v1/organizations/{orgId}/security-policy",
            update, Json, ct));
        if (!resp.IsSuccessStatusCode)
        {
            return (false, null, await DetailAsync(resp, ct));
        }

        return (true,
            await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct),
            null);
    }

    // ---- scm (SPEC-52) -----------------------------------------------------

    /// <summary>Conexões SCM da org.</summary>
    public async Task<IReadOnlyList<JsonElement>> ListScmConnectionsAsync(
        string orgId, CancellationToken ct = default) =>
        await GetAsync<List<JsonElement>>(
            $"/api/v1/orgs/{orgId}/scm-connections", ct) ?? [];

    /// <summary>Remove uma conexão SCM.</summary>
    public async Task<bool> DeleteScmConnectionAsync(
        string orgId, string id, CancellationToken ct = default)
    {
        var resp = Guard(await http.DeleteAsync(
            $"/api/v1/orgs/{orgId}/scm-connections/{id}", ct));
        return resp.IsSuccessStatusCode;
    }

    // ---- plumbing --------------------------------------------------------

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct) =>
        await Guard(await http.GetAsync(url, ct))
            .Content.ReadFromJsonAsync<T>(Json, ct);

    /// <summary>401/403 → exceção amigável; demais status passam.</summary>
    private static HttpResponseMessage Guard(HttpResponseMessage resp)
    {
        if (resp.StatusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden)
        {
            throw new AdminDeniedException((int)resp.StatusCode);
        }

        return resp;
    }

    private static async Task<string> DetailAsync(
        HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var doc = await resp.Content
                .ReadFromJsonAsync<JsonElement>(Json, ct);
            return doc.TryGetProperty("detail", out var d)
                ? d.GetString() ?? "conflict" : "conflict";
        }
        catch
        {
            return "conflict";
        }
    }
}

/// <summary>401/403 nos endpoints admin — a página mostra aviso e esconde.</summary>
public sealed class AdminDeniedException(int status)
    : HttpRequestException($"admin access denied ({status})")
{
    /// <summary>HTTP status observado.</summary>
    public int Status { get; } = status;
}

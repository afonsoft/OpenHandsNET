using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Contracts;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Application.Scm;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Scm;

/// <summary>
/// <see cref="IScmConnectionService"/> — CRUD de <c>scm_connections</c> +
/// GitHub App manifest flow (SPEC-52 RF-002). Segredos sempre via
/// <see cref="ISecretStore"/>; a resposta do manifest callback grava
/// PEM/webhook_secret/client_secret como secrets por-referência.
/// </summary>
public sealed class ScmConnectionService(
    AppDbContext db,
    ISecretStore secrets,
    IAuditService audit,
    IHttpClientFactory httpFactory,
    IScmProviderRegistry providers,
    ILogger<ScmConnectionService> logger) : IScmConnectionService
{
    private const string GitHubManifestConversionUrl =
        "https://api.github.com/app-manifests/{0}/conversions";

    /// <inheritdoc />
    public object BuildGitHubAppManifest(string redirectUri, string appName) => new
    {
        name = appName,
        url = "https://github.com/afonsoft/OpenHandsNET",
        hook_attributes = new { url = redirectUri.Replace("/callback", "/../../webhooks/scm/github"), active = true },
        redirect_url = redirectUri,
        @public = false,
        default_permissions = new
        {
            contents = "write",
            pull_requests = "write",
            issues = "write",
            statuses = "write",
            metadata = "read",
        },
        default_events = new[]
        {
            "issue_comment", "pull_request", "pull_request_review",
            "pull_request_review_comment",
        },
    };

    /// <inheritdoc />
    public async Task<ScmConnection?> CompleteGitHubManifestFlowAsync(
        Guid orgId, string code, string? createdByUserId, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("github");
        using var res = await http.PostAsync(
            string.Format(GitHubManifestConversionUrl, code),
            content: null, ct);
        if (!res.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "github manifest conversion failed: {Status}", res.StatusCode);
            return null;
        }

        using var doc = JsonDocument.Parse(
            await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var appId = root.GetProperty("id").GetRawText();
        var slug = root.GetProperty("slug").GetString();
        var pem = root.GetProperty("pem").GetString()!;
        var webhookSecret = root.TryGetProperty("webhook_secret", out var ws)
            ? ws.GetString() : null;
        var clientSecret = root.TryGetProperty("client_secret", out var cs)
            ? cs.GetString() : null;

        var owner = orgId.ToString();
        var pemRef = $"scm_github_pem_{slug}";
        var hookRef = $"scm_github_hook_{slug}";
        var clientRef = $"scm_github_client_{slug}";
        await secrets.SetAsync("org", owner, pemRef, pem, ct);
        if (webhookSecret is not null)
            await secrets.SetAsync("org", owner, hookRef, webhookSecret, ct);
        if (clientSecret is not null)
            await secrets.SetAsync("org", owner, clientRef, clientSecret, ct);

        var record = new ScmConnectionRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Kind = ScmKind.GitHub.ToWire(),
            AppId = appId,
            AppSlug = slug,
            PemSecretRef = pemRef,
            WebhookSecretRef = webhookSecret is null ? null : hookRef,
            ClientSecretRef = clientSecret is null ? null : clientRef,
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ScmConnections.Add(record);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("scm.connection_added",
            actorUserId: createdByUserId, orgId: orgId,
            targetType: "scm_connection", targetId: record.Id.ToString(),
            metadata: new { kind = "github", app_slug = slug },
            cancellationToken: ct);
        return ToModel(record);
    }

    /// <inheritdoc />
    public async Task<ScmConnection> CreateAsync(
        Guid orgId, ScmKind kind, string? installationId,
        string? pemSecretRef, string? webhookSecretRef,
        IReadOnlyList<string>? repos, string? appId, string? appSlug,
        string? createdByUserId, CancellationToken ct,
        string? apiUrl = null, string? authKind = null,
        string? metaJson = null)
    {
        var record = new ScmConnectionRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Kind = kind.ToWire(),
            InstallationId = installationId,
            PemSecretRef = pemSecretRef,
            WebhookSecretRef = webhookSecretRef,
            ReposJson = repos is null ? null : JsonSerializer.Serialize(repos),
            AppId = appId,
            AppSlug = appSlug,
            ApiUrl = apiUrl,
            AuthKind = authKind,
            MetaJson = metaJson,
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ScmConnections.Add(record);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("scm.connection_added",
            actorUserId: createdByUserId, orgId: orgId,
            targetType: "scm_connection", targetId: record.Id.ToString(),
            metadata: new { kind = kind.ToWire() }, cancellationToken: ct);
        return ToModel(record);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScmConnection>> ListAsync(
        Guid orgId, CancellationToken ct) =>
        (await db.ScmConnections.IgnoreQueryFilters()
            .Where(c => c.OrgId == orgId)
            .ToListAsync(ct))
            .OrderByDescending(c => c.CreatedAt)
            .Select(ToModel).ToList();

    /// <inheritdoc />
    public async Task<ScmConnection?> GetAsync(Guid id, CancellationToken ct)
    {
        var record = await db.ScmConnections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        return record is null ? null : ToModel(record);
    }

    /// <inheritdoc />
    public async Task<ScmConnection?> FindForRepositoryAsync(
        Guid orgId, ScmKind kind, string repository, CancellationToken ct)
    {
        var rows = await db.ScmConnections.IgnoreQueryFilters()
            .Where(c => c.OrgId == orgId && c.Kind == kind.ToWire())
            .ToListAsync(ct);
        var model = rows.Select(ToModel).FirstOrDefault(c =>
            c.Repos.Count == 0 || c.Repos.Contains(repository,
                StringComparer.OrdinalIgnoreCase));
        return model;
    }

    /// <inheritdoc />
    public async Task<ScmConnectionDiagnostic> TestAsync(
        Guid id, CancellationToken ct)
    {
        var conn = await GetAsync(id, ct);
        if (conn is null)
        {
            return new ScmConnectionDiagnostic(
                false, null, null, null, "connection not found");
        }
        var provider = providers.Get(conn.Kind);
        if (provider is null)
        {
            return new ScmConnectionDiagnostic(
                false, null, null, null,
                $"provider '{conn.Kind.ToWire()}' not registered");
        }
        try
        {
            return await provider.TestConnectionAsync(conn, ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "scm test failed (conn {Id})", id);
            return new ScmConnectionDiagnostic(
                false, null, null, null, e.Message);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var record = await db.ScmConnections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (record is null)
            return false;
        db.ScmConnections.Remove(record);
        await db.SaveChangesAsync(ct);
        return true;
    }

    internal static ScmConnection ToModel(ScmConnectionRecord r) =>
        new ScmConnection(
            r.Id, r.OrgId, ScmKindExtensions.Parse(r.Kind) ?? ScmKind.GitHub,
            r.InstallationId, r.PemSecretRef, r.WebhookSecretRef,
            r.ReposJson is null
                ? []
                : JsonSerializer.Deserialize<List<string>>(r.ReposJson) ?? [],
            r.AppId, r.AppSlug, r.CreatedByUserId, r.CreatedAt,
            r.ApiUrl, r.AuthKind, r.MetaJson);
}
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.BrowserProfiles;
using OpenHandsNET.Application.Contracts;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Infrastructure.Agent.Browser;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// SPEC-64: browser auth profiles (storage state por domínio) —
/// meta apenas; o state nunca sai pela API.
/// </summary>
public static class BrowserProfileEndpoints
{
    private sealed record CaptureRequest(
        string Domain, string? Name = null,
        DateTimeOffset? ExpiresAt = null, bool Shared = false);

    private sealed record AttachRequest(Guid ProfileId);

    public static IEndpointRouteBuilder MapBrowserProfileEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1")
            .WithTags("BrowserProfiles").RequireAuthorization();

        group.MapGet("/browser-profiles", async (
            HttpContext http, ICurrentUserAccessor current,
            IBrowserProfileService profiles,
            IOptions<BrowserOptions> browserOpt, CancellationToken ct) =>
        {
            if (!browserOpt.Value.ProfilesEnabled)
            {
                return FeatureOff(http);
            }
            var user = current.RequireUserId();
            var items = await profiles.ListAsync(
                user.ToString(), includeShared: true, ct);
            return Results.Ok(items);
        });

        group.MapGet("/browser-profiles/{id:guid}", async (
            Guid id, HttpContext http, ICurrentUserAccessor current,
            IBrowserProfileService profiles,
            IOptions<BrowserOptions> browserOpt, CancellationToken ct) =>
        {
            if (!browserOpt.Value.ProfilesEnabled)
            {
                return FeatureOff(http);
            }
            var user = current.RequireUserId();
            var admin = current.User?.IsSuperAdmin == true;
            var info = await profiles.GetAsync(
                id, user.ToString(), admin, ct);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        group.MapDelete("/browser-profiles/{id:guid}", async (
            Guid id, HttpContext http, ICurrentUserAccessor current,
            IBrowserProfileService profiles, IAuditService audit,
            IOptions<BrowserOptions> browserOpt, CancellationToken ct) =>
        {
            if (!browserOpt.Value.ProfilesEnabled)
            {
                return FeatureOff(http);
            }
            var user = current.RequireUserId();
            var admin = current.User?.IsSuperAdmin == true;
            if (!await profiles.DeleteAsync(id, user.ToString(), admin, ct))
            {
                return Results.NotFound();
            }
            await audit.RecordAsync("browser.profile_deleted",
                actorUserId: user.ToString(),
                targetType: "browser_profile", targetId: id.ToString(),
                cancellationToken: ct);
            return Results.NoContent();
        });

        // RF-002 — captura o storage state da sessão, filtrado ao
        // eTLD+1 informado (a UI/agente pede confirmação antes).
        group.MapPost("/app-conversations/{id:guid}/browser/capture",
            async ([AsParameters] BrowserCaptureParams p) =>
        {
            if (!p.BrowserOpt.Value.ProfilesEnabled)
            {
                return FeatureOff(p.Http);
            }
            var user = p.Current.RequireUserId();
            var conv = await p.Db.Conversations.FindAsync([p.Id], p.Ct);
            if (conv is null)
            {
                return Results.NotFound();
            }
            var raw = await p.Browser.CaptureStorageStateAsync(p.Id, p.Ct);
            if (raw is null)
            {
                return OpenHandsNetProblemFactory.Create(
                    p.Http, 409, "no browser context for this conversation");
            }
            var domain = IBrowserProfileService.NormalizeDomain(p.Req.Domain);
            var filtered = FilterStateToDomain(raw, domain);
            var profile = await p.Profiles.SaveAsync(
                user.ToString(), domain,
                p.Req.Name ?? domain, filtered, p.Req.ExpiresAt, p.Ct);
            await p.Audit.RecordAsync("browser.profile_saved",
                actorUserId: user.ToString(), orgId: conv.OrgId,
                targetType: "browser_profile",
                targetId: profile.Id.ToString(),
                metadata: new { domain }, cancellationToken: p.Ct);
            return Results.Ok(profile);
        });

        // RF-003 — attach: storage state + site_cookie secrets do
        // domínio entram no contexto da conversa.
        group.MapPost("/app-conversations/{id:guid}/browser/attach",
            async ([AsParameters] BrowserAttachParams p) =>
        {
            if (!p.BrowserOpt.Value.ProfilesEnabled)
            {
                return FeatureOff(p.Http);
            }
            var user = p.Current.RequireUserId();
            var admin = p.Current.User?.IsSuperAdmin == true;
            var conv = await p.Db.Conversations.FindAsync([p.Id], p.Ct);
            if (conv is null)
            {
                return Results.NotFound();
            }
            string state;
            try
            {
                state = await p.Profiles.ResolveStateAsync(
                    p.Req.ProfileId, user.ToString(), admin, p.Ct);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (BrowserProfileExpiredException)
            {
                return OpenHandsNetProblemFactory.Create(
                    p.Http, 410, "browser_profile_expired");
            }
            var meta = await p.Profiles.GetAsync(
                p.Req.ProfileId, user.ToString(), admin, p.Ct);
            if (meta is not null)
            {
                state = await MergeSiteCookiesAsync(
                    p.Secrets, user.ToString(), meta.Domain, state, p.Ct);
            }
            await p.Browser.SetStorageStateAsync(p.Id, state, p.Ct);
            await p.Audit.RecordAsync("browser.profile_attached",
                actorUserId: user.ToString(), orgId: conv.OrgId,
                targetType: "browser_profile",
                targetId: p.Req.ProfileId.ToString(),
                metadata: new { meta?.Domain }, cancellationToken: p.Ct);
            return Results.Ok(new { attached = true });
        });

        return app;
    }

    private static IResult FeatureOff(HttpContext http) =>
        OpenHandsNetProblemFactory.Create(
            http, 403, "browser profiles disabled");

    /// <summary>
    /// Filtra cookies/origins do storage state Playwright ao eTLD+1 —
    /// state cru pode conter domínios de terceiros que não devem
    /// vazar para outro profile.
    /// </summary>
    internal static string FilterStateToDomain(
        string stateJson, string domain)
    {
        try
        {
            using var doc = JsonDocument.Parse(stateJson);
            var cookies = doc.RootElement
                .TryGetProperty("cookies", out var c)
                ? c.EnumerateArray()
                    .Where(x => MatchesDomain(x, domain))
                    .Select(x => x.GetRawText()).ToList()
                : [];
            var origins = doc.RootElement
                .TryGetProperty("origins", out var o)
                ? o.EnumerateArray()
                    .Where(x => MatchesOrigin(x, domain))
                    .Select(x => x.GetRawText()).ToList()
                : [];
            return JsonSerializer.Serialize(new
            {
                cookies = cookies.Select(
                    x => JsonSerializer.Deserialize<JsonElement>(x)),
                origins = origins.Select(
                    x => JsonSerializer.Deserialize<JsonElement>(x)),
            });
        }
        catch (JsonException)
        {
            return stateJson;
        }
    }

    private static bool MatchesDomain(JsonElement cookie, string domain)
    {
        if (!cookie.TryGetProperty("domain", out var d))
        {
            return false;
        }
        var cd = d.GetString()?.TrimStart('.') ?? "";
        return IBrowserProfileService.NormalizeDomain(cd) == domain;
    }

    private static bool MatchesOrigin(JsonElement origin, string domain)
    {
        if (!origin.TryGetProperty("origin", out var o)
            || !Uri.TryCreate(o.GetString(), UriKind.Absolute, out var uri))
        {
            return false;
        }
        return IBrowserProfileService.NormalizeDomain(uri.Host) == domain;
    }

    /// <summary>
    /// RF-004: mescla cookies de secrets `site_cookie:{domain}:*`
    /// (valor JSON {value,domain,path,expires,secure,http_only})
    /// no storage state antes do attach.
    /// </summary>
    private static async Task<string> MergeSiteCookiesAsync(
        ISecretStore secrets, string ownerId, string domain,
        string stateJson, CancellationToken ct)
    {
        var names = await secrets.ListNamesAsync("user", ownerId, ct);
        var prefix = $"site_cookie:{domain}:";
        var matching = names.Where(
            n => n.StartsWith(prefix, StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(stateJson);
        var cookies = doc.RootElement
            .TryGetProperty("cookies", out var c)
            ? c.EnumerateArray().Select(x => x.GetRawText()).ToList()
            : [];
        var origins = doc.RootElement
            .TryGetProperty("origins", out var o)
            ? o.EnumerateArray().Select(x => x.GetRawText()).ToList()
            : [];
        foreach (var name in matching)
        {
            var payload = await secrets.ResolveAsync(
                "user", ownerId, name, ct);
            if (payload is null)
            {
                continue;
            }
            try
            {
                using var cd = JsonDocument.Parse(payload);
                var cookie = cd.RootElement.Clone();
                cookies.Add(cookie.GetRawText());
            }
            catch (JsonException) { /* payload malformado: ignora */ }
        }
        return JsonSerializer.Serialize(new
        {
            cookies = cookies.Select(
                x => JsonSerializer.Deserialize<JsonElement>(x)),
            origins = origins.Select(
                x => JsonSerializer.Deserialize<JsonElement>(x)),
        });
    }

    /// <summary>Binding do browser capture (SPEC-104 — AsParameters).</summary>
    private sealed class BrowserCaptureParams
    {
        [FromRoute] public Guid Id { get; init; }
        [FromBody] public CaptureRequest Req { get; init; } = null!;
        public HttpContext Http { get; init; } = null!;
        [FromServices] public ICurrentUserAccessor Current { get; init; } = null!;
        [FromServices] public AppDbContext Db { get; init; } = null!;
        [FromServices] public IBrowserService Browser { get; init; } = null!;
        [FromServices] public IBrowserProfileService Profiles { get; init; } = null!;
        [FromServices] public IAuditService Audit { get; init; } = null!;
        [FromServices] public IOptions<BrowserOptions> BrowserOpt { get; init; } = null!;
        public CancellationToken Ct { get; init; }
    }

    /// <summary>Binding do browser attach (SPEC-104 — AsParameters).</summary>
    private sealed class BrowserAttachParams
    {
        [FromRoute] public Guid Id { get; init; }
        [FromBody] public AttachRequest Req { get; init; } = null!;
        public HttpContext Http { get; init; } = null!;
        [FromServices] public ICurrentUserAccessor Current { get; init; } = null!;
        [FromServices] public AppDbContext Db { get; init; } = null!;
        [FromServices] public IBrowserService Browser { get; init; } = null!;
        [FromServices] public IBrowserProfileService Profiles { get; init; } = null!;
        [FromServices] public ISecretStore Secrets { get; init; } = null!;
        [FromServices] public IAuditService Audit { get; init; } = null!;
        [FromServices] public IOptions<BrowserOptions> BrowserOpt { get; init; } = null!;
        public CancellationToken Ct { get; init; }
    }
}
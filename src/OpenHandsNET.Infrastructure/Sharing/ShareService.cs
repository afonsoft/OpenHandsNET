using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using OpenHandsNET.Application.Sharing;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Sharing;

/// <summary>
/// EF-backed <see cref="IShareService"/> (SPEC-56). Tokens are 256-bit
/// random; only the SHA-256 hash is persisted — invalid, expired and
/// revoked tokens all resolve to <see langword="null"/> (no oracle).
/// </summary>
public sealed class ShareService(
    AppDbContext db,
    IPublicEventFilter filter,
    OpenHandsNET.Application.Contracts.ISecretStore secrets,
    IConfiguration config) : IShareService
{
    /// <inheritdoc />
    public async Task<ShareLinkResult> CreateOrGetAsync(
        Guid conversationId, Guid actorUserId, CancellationToken ct)
    {
        var conv = await RequireOwnedAsync(conversationId, actorUserId, ct);
        EnsureSharesEnabled(conv);

        var now = DateTimeOffset.UtcNow;
        var active = await db.ConversationShares
            .Where(s => s.ConversationId == conversationId
                && s.RevokedAt == null
                && (s.ExpiresAtTicks == null
                    || s.ExpiresAtTicks > now.UtcTicks))
            .OrderByDescending(s => s.CreatedAtTicks)
            .FirstOrDefaultAsync(ct);
        if (active is not null)
        {
            // O token bruto não é recuperável — retorna URL estável por
            // record. Novo token só após revoke+create.
            return new ShareLinkResult(
                ShareUrl(null), Token: "", ExpiresAt: active.ExpiresAt);
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            .ToLowerInvariant();
        var record = new ConversationShareRecord
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            TokenHash = Hash(token),
            CreatedByUserId = actorUserId.ToString(),
            CreatedAt = now,
        };
        db.ConversationShares.Add(record);
        await db.SaveChangesAsync(ct);
        return new ShareLinkResult(ShareUrl(token), token, record.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(
        Guid conversationId, Guid actorUserId, CancellationToken ct)
    {
        await RequireOwnedAsync(conversationId, actorUserId, ct);
        var now = DateTimeOffset.UtcNow;
        var shares = await db.ConversationShares
            .Where(s => s.ConversationId == conversationId
                && s.RevokedAt == null)
            .ToListAsync(ct);
        if (shares.Count == 0)
        {
            return false;
        }

        foreach (var s in shares)
        {
            s.RevokedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<ShareStatusResult> GetStatusAsync(
        Guid conversationId, Guid actorUserId, CancellationToken ct)
    {
        await RequireOwnedAsync(conversationId, actorUserId, ct);
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var active = await db.ConversationShares
            .Where(s => s.ConversationId == conversationId
                && s.RevokedAt == null
                && (s.ExpiresAtTicks == null || s.ExpiresAtTicks > nowTicks))
            .OrderByDescending(s => s.CreatedAtTicks)
            .FirstOrDefaultAsync(ct);
        return new ShareStatusResult(
            Shared: active is not null,
            Url: active is null ? null : "/share",
            ExpiresAt: active?.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task<SharedConversationPayload?> GetSharedAsync(
        string token, int limit, CancellationToken ct)
    {
        var conv = await ResolveAsync(token, ct);
        if (conv is null)
        {
            return null;
        }

        var secrets = await ResolveSecretValuesAsync(conv, ct);
        var page = await PageEventsAsync(conv.Id, null, limit, secrets, ct);
        return new SharedConversationPayload(
            Title: conv.Title ?? "Untitled conversation",
            SelectedRepository: conv.SelectedRepository,
            AgentType: conv.AgentType,
            ExecutionStatus: conv.ExecutionStatus,
            CreatedAt: conv.CreatedAt,
            Items: page.Items,
            NextPageId: page.NextPageId);
    }

    /// <inheritdoc />
    public async Task<SharedEventPage?> GetSharedEventsAsync(
        string token, string? cursor, int limit, CancellationToken ct)
    {
        var conv = await ResolveAsync(token, ct);
        if (conv is null)
        {
            return null;
        }

        var secrets = await ResolveSecretValuesAsync(conv, ct);
        return await PageEventsAsync(conv.Id, cursor, limit, secrets, ct);
    }

    // ------------------------------------------------------------------

    private async Task<ConversationRecord?> ResolveAsync(
        string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256)
        {
            return null;
        }

        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var share = await db.ConversationShares
            .Where(s => s.TokenHash == Hash(token)
                && s.RevokedAt == null
                && (s.ExpiresAtTicks == null || s.ExpiresAtTicks > nowTicks))
            .FirstOrDefaultAsync(ct);
        if (share is null)
        {
            return null;
        }

        return await db.Conversations
            .Where(c => c.Id == share.ConversationId)
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(ct);
    }

    private async Task<SharedEventPage> PageEventsAsync(
        Guid conversationId, string? cursor, int limit,
        IReadOnlyList<string> secretValues, CancellationToken ct)
    {
        var take = Math.Clamp(limit, 1, 100);
        var items = new List<JsonElement>();
        var (afterTicks, afterId) = DecodeCursor(cursor);
        string? next = null;

        // Pós-filtro pode descartar itens — avança por janelas de 100 até
        // encher a página ou esgotar o stream.
        while (items.Count < take)
        {
            var batch = await db.Events
                .Where(e => e.ConversationId == conversationId)
                .Where(e => afterTicks == null
                    || e.TimestampTicks > afterTicks
                    || (e.TimestampTicks == afterTicks
                        && string.Compare(e.Id, afterId) > 0))
                .OrderBy(e => e.TimestampTicks).ThenBy(e => e.Id)
                .Take(100)
                .ToListAsync(ct);
            if (batch.Count == 0)
            {
                next = null;
                break;
            }

            foreach (var e in batch)
            {
                var filtered = filter.Filter(e.Kind, e.Payload, secretValues);
                if (filtered is { } el && items.Count < take)
                {
                    items.Add(el);
                }

                (afterTicks, afterId) = (e.TimestampTicks, e.Id);
            }

            next = batch.Count == 100
                ? EncodeCursor(afterTicks!.Value, afterId!)
                : null;
            if (batch.Count < 100)
            {
                break;
            }
        }

        return new SharedEventPage(items, next);
    }

    private async Task<ConversationRecord> RequireOwnedAsync(
        Guid conversationId, Guid actorUserId, CancellationToken ct)
    {
        var conv = await db.Conversations
            .Where(c => c.Id == conversationId)
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(ct)
            ?? throw new ShareForbiddenException("conversation not found");
        if (!await CanReadAsync(conv, actorUserId, ct))
        {
            throw new ShareForbiddenException("not allowed");
        }

        return conv;
    }

    private async Task<bool> CanReadAsync(
        ConversationRecord conv, Guid actorUserId, CancellationToken ct)
    {
        if (conv.CreatedByUserId == actorUserId.ToString())
        {
            return true;
        }

        return conv.OrgId is { } orgId
            && await db.OrgMembers.AnyAsync(
                m => m.OrgId == orgId && m.UserId == actorUserId, ct);
    }

    private void EnsureSharesEnabled(ConversationRecord conv)
    {
        if (string.Equals(config["Shares:Enabled"], "false",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ShareForbiddenException("sharing disabled");
        }

        if (conv.OrgId is not { } orgId)
        {
            return;
        }

        var settings = db.Organizations
            .Where(o => o.Id == orgId)
            .Select(o => o.SettingsJson)
            .FirstOrDefault();
        if (settings is null)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(settings);
            if (doc.RootElement.TryGetProperty("shares_enabled", out var v)
                && v.ValueKind == JsonValueKind.False)
            {
                throw new ShareForbiddenException(
                    "sharing disabled for this organization");
            }
        }
        catch (JsonException)
        {
        }
    }

    private async Task<IReadOnlyList<string>> ResolveSecretValuesAsync(
        ConversationRecord conv, CancellationToken ct)
    {
        if (conv.SecretNamesJson is null)
        {
            return [];
        }

        var names = JsonSerializer
            .Deserialize<List<string>>(conv.SecretNamesJson) ?? [];
        var values = new List<string>();
        foreach (var name in names)
        {
            try
            {
                if (conv.CreatedByUserId is { } uid
                    && await secrets.ResolveAsync(
                        "user", uid, name, ct) is { } uv)
                {
                    values.Add(uv);
                }

                if (conv.OrgId is { } orgId
                    && await secrets.ResolveAsync(
                        "org", orgId.ToString(), name, ct) is { } ov)
                {
                    values.Add(ov);
                }
            }
            catch
            {
                // Secret store indisponível — o filtro ainda remove
                // placeholders {{secret:*}} e propriedades sensíveis.
            }
        }

        return values;
    }

    private static string Hash(string token) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();

    private static string ShareUrl(string? token) =>
        token is null ? "/share" : $"/share/{token}";

    private static string EncodeCursor(long ticks, string id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ticks}:{id}"));

    private static (long? Ticks, string? Id) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (null, null);
        }

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var idx = raw.IndexOf(':', StringComparison.Ordinal);
            return idx < 0 || !long.TryParse(raw[..idx], out var t)
                ? (null, null)
                : (t, raw[(idx + 1)..]);
        }
        catch
        {
            return (null, null);
        }
    }
}
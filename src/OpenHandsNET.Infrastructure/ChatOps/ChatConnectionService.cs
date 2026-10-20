using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Application.ChatOps;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.ChatOps;

/// <summary>
/// CRUD de <see cref="ChatConnectionRecord"/> + configs de canal
/// (SPEC-54 RF-001/RF-004).
/// </summary>
public sealed class ChatConnectionService(
    AppDbContext db, IAuditService audit) : IChatConnectionService
{
    /// <inheritdoc />
    public async Task<ChatConnection> CreateAsync(
        Guid orgId, string teamId, string botTokenSecretRef,
        string signingSecretRef, string? botUserId,
        string? defaultChannelId, string? createdByUserId,
        CancellationToken ct)
    {
        var record = new ChatConnectionRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Kind = "slack",
            TeamId = teamId,
            BotTokenSecretRef = botTokenSecretRef,
            SigningSecretRef = signingSecretRef,
            BotUserId = botUserId,
            DefaultChannelId = defaultChannelId,
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ChatConnections.Add(record);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("chatops.connection_added",
            actorUserId: createdByUserId, orgId: orgId,
            targetType: "chat_connection", targetId: record.Id.ToString(),
            metadata: new { teamId }, cancellationToken: ct);
        return ToModel(record);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatConnection>> ListAsync(
        Guid orgId, CancellationToken ct) =>
        (await db.ChatConnections.IgnoreQueryFilters()
            .Where(c => c.OrgId == orgId).ToListAsync(ct))
            .OrderByDescending(c => c.CreatedAt).Select(ToModel).ToList();

    /// <inheritdoc />
    public async Task<ChatConnection?> GetAsync(
        Guid id, CancellationToken ct)
    {
        var r = await db.ChatConnections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        return r is null ? null : ToModel(r);
    }

    /// <inheritdoc />
    public async Task<ChatConnection?> FindByTeamAsync(
        string teamId, CancellationToken ct)
    {
        var r = await db.ChatConnections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TeamId == teamId, ct);
        return r is null ? null : ToModel(r);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        Guid id, Guid orgId, CancellationToken ct)
    {
        var r = await db.ChatConnections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct);
        if (r is null)
        {
            return false;
        }
        db.ChatConnections.Remove(r);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<ChatChannelConfig> SetChannelConfigAsync(
        Guid connectionId, string channelId, string? selectedRepository,
        string? selectedBranch, bool autoTriage, CancellationToken ct)
    {
        var r = await db.ChatChannelConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.ConnectionId == connectionId
                && c.ChannelId == channelId, ct);
        if (r is null)
        {
            r = new ChatChannelConfigRecord
            {
                Id = Guid.NewGuid(),
                ConnectionId = connectionId,
                ChannelId = channelId,
            };
            db.ChatChannelConfigs.Add(r);
        }
        r.SelectedRepository = selectedRepository;
        r.SelectedBranch = selectedBranch;
        r.AutoTriage = autoTriage;
        r.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToConfig(r);
    }

    /// <inheritdoc />
    public async Task<ChatChannelConfig?> GetChannelConfigAsync(
        Guid connectionId, string channelId, CancellationToken ct)
    {
        var r = await db.ChatChannelConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.ConnectionId == connectionId
                && c.ChannelId == channelId, ct);
        return r is null ? null : ToConfig(r);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatChannelConfig>?>
        ListChannelConfigsAsync(
            Guid connectionId, Guid orgId, CancellationToken ct)
    {
        var owns = await db.ChatConnections.IgnoreQueryFilters()
            .AnyAsync(c => c.Id == connectionId && c.OrgId == orgId, ct);
        return owns
            ? (await db.ChatChannelConfigs.IgnoreQueryFilters()
                .Where(c => c.ConnectionId == connectionId)
                .ToListAsync(ct))
                .OrderBy(c => c.ChannelId).Select(ToConfig).ToList()
            : null;
    }

    internal static ChatConnection ToModel(ChatConnectionRecord r) =>
        new(r.Id, r.OrgId, ChatKindExtensions.Parse(r.Kind) ?? ChatKind.Slack,
            r.TeamId, r.BotTokenSecretRef, r.SigningSecretRef,
            r.BotUserId, r.DefaultChannelId, r.CreatedByUserId, r.CreatedAt);

    private static ChatChannelConfig ToConfig(ChatChannelConfigRecord r) =>
        new(r.Id, r.ConnectionId, r.ChannelId, r.SelectedRepository,
            r.SelectedBranch, r.AutoTriage, r.UpdatedAt);
}
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Application.Mcp;
using OpenHandsNET.Domain.Mcp;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Mcp;

/// <summary>
/// CRUD do flat map MCP em <c>mcp_servers</c> (SPEC-07 RF-001).
/// ConfigJson = <see cref="McpServerConfig"/> serializado; override por
/// conversa = JSON do mesmo shape armazenado na conversa.
/// </summary>
public sealed class McpSettingsService(AppDbContext db) : IMcpSettingsService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<McpServerConfig>> ListAsync(
        string ownerId, CancellationToken ct = default) =>
        (await db.McpServers.Where(r => r.OwnerId == ownerId).ToListAsync(ct))
            .Select(r => Parse(r.ConfigJson))
            .OfType<McpServerConfig>().ToList();

    /// <inheritdoc />
    public async Task<McpServerConfig?> GetAsync(
        string ownerId, string name, CancellationToken ct = default) =>
        await db.McpServers.FindAsync([ownerId, name], ct) is { } row
            ? Parse(row.ConfigJson) : null;

    /// <inheritdoc />
    public async Task SaveAsync(
        string ownerId, McpServerConfig config, CancellationToken ct = default)
    {
        var row = await db.McpServers.FindAsync([ownerId, config.Name], ct);
        var json = JsonSerializer.Serialize(config, Json);
        if (row is null)
        {
            db.McpServers.Add(new McpServerRecord
            {
                OwnerId = ownerId,
                Name = config.Name,
                ConfigJson = json,
                Enabled = config.Enabled,
            });
        }
        else
        {
            row.ConfigJson = json;
            row.Enabled = config.Enabled;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string ownerId, string name, CancellationToken ct = default)
    {
        if (await db.McpServers.FindAsync([ownerId, name], ct) is not { } row)
        {
            return false;
        }

        db.McpServers.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<McpServerConfig>> EffectiveForAsync(
        string ownerId, string? conversationOverrideJson,
        CancellationToken ct = default)
    {
        if (conversationOverrideJson is not null)
        {
            var parsed = JsonSerializer.Deserialize<List<McpServerConfig>>(
                conversationOverrideJson, Json);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return await ListAsync(ownerId, ct);
    }

    /// <summary>Serializa um mapa de configs (uso do override por conversa).</summary>
    public static string SerializeAll(IReadOnlyList<McpServerConfig> configs) =>
        JsonSerializer.Serialize(configs, Json);

    private static McpServerConfig? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<McpServerConfig>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
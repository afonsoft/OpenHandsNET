using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Mcp;
using Sandcastle.Domain.Mcp;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Mcp;

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

    /// <summary>
    /// SPEC-131 RF-003: servidores builtin — sempre disponíveis quando a
    /// imagem de runtime os suporta. Um servidor configurado com o mesmo
    /// nome vence (override/desligamento honrado).
    /// </summary>
    internal static readonly IReadOnlyList<McpServerConfig> BuiltinServers =
    [
        new McpServerConfig
        {
            Name = "chrome-devtools",
            Transport = McpTransport.Stdio,
            Command = "chrome-devtools-mcp",
            Args =
            [
                "--headless", "--isolated",
                "--chromeArg=--no-sandbox",
                "--chromeArg=--disable-dev-shm-usage",
            ],
        },
    ];

    /// <summary>Merge builtin + configurados — nome configurado vence.</summary>
    internal static IReadOnlyList<McpServerConfig> MergeBuiltin(
        IReadOnlyList<McpServerConfig> configured)
    {
        var names = configured.Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. BuiltinServers.Where(b => !names.Contains(b.Name)),
            .. configured];
    }

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
        CancellationToken ct = default) =>
        await EffectiveForOwnersAsync(
            [ownerId], conversationOverrideJson, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<McpServerConfig>> EffectiveForOwnersAsync(
        IReadOnlyList<string> ownerIds, string? conversationOverrideJson,
        CancellationToken ct = default)
    {
        if (conversationOverrideJson is not null)
        {
            var parsed = JsonSerializer.Deserialize<List<McpServerConfig>>(
                conversationOverrideJson, Json);
            if (parsed is not null)
            {
                return MergeBuiltin(parsed);
            }
        }

        // Cadeia de escopo: owners na ordem crescente de precedência —
        // o último vence conflitos de nome (SPEC-148 user > org > system).
        var merged = new Dictionary<string, McpServerConfig>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var owner in ownerIds)
        {
            foreach (var cfg in await ListAsync(owner, ct))
            {
                merged[cfg.Name] = cfg;
            }
        }

        return MergeBuiltin([.. merged.Values]);
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
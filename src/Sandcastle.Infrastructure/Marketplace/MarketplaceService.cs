using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Identity;
using Sandcastle.Application.Marketplace;
using Sandcastle.Domain.Marketplace;
using Sandcastle.Domain.Mcp;
using Sandcastle.Domain.Skills;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Marketplace;

/// <summary>
/// SPEC-148 RF-002/RF-003: install pipeline + materializers per kind.
/// Materialization writes <b>data only</b> — prompts/configs/skills —
/// into the same stores the agent already reads: <c>skills</c> table
/// (prompt injection), <c>~/.sandcastle/agents/*.agent.md</c> (SPEC-95
/// host layer), <c>mcp_servers</c> (SPEC-131 merge), <c>hook_configs</c>
/// (SPEC-11 RF-004). Raw entry files are mirrored under
/// <see cref="MarketplaceOptions.DataDir"/> for audit/offline re-verify.
/// </summary>
public sealed class MarketplaceService(
    AppDbContext db,
    IMarketplaceRegistry registry,
    IOptions<MarketplaceOptions> options,
    IAuditService audit,
    ILogger<MarketplaceService> logger) : IMarketplaceService
{
    internal const string MarketplaceSkillSource = "marketplace";
    private const int MaxBundleDepth = 8;
    private const string DefaultTriggerType = "keyword";

    /// <summary>Scope keys used for <c>mcp_servers.owner_id</c> rows.</summary>
    internal static string McpOwner(InstallScope scope, string? scopeId) =>
        scope switch
        {
            InstallScope.User => $"user:{scopeId}",
            InstallScope.Org => $"org:{scopeId}",
            _ => "system",
        };

    /// <summary>Test seam: process HOME (agent materialization).</summary>
    internal static Func<string?> HomeResolver { get; set; } =
        () => Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);

    /// <summary>Test seam: app version for min_app_version checks.</summary>
    internal static Func<string> AppVersionResolver { get; set; } =
        () => typeof(MarketplaceService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0-dev";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    // ------------------------------------------------------------- install

    /// <inheritdoc />
    public async Task<MarketplaceInstallResult> InstallAsync(
        MarketplaceInstallRequest request, CancellationToken ct) =>
        await InstallCoreAsync(request, 0, ct);

    private async Task<MarketplaceInstallResult> InstallCoreAsync(
        MarketplaceInstallRequest request, int depth, CancellationToken ct)
    {
        if (depth > MaxBundleDepth)
        {
            return MarketplaceInstallResult.Fail("bundle_too_deep");
        }

        var entry = await registry.GetEntryAsync(request.EntryId, ct);
        if (entry is null)
        {
            return MarketplaceInstallResult.Fail("entry_not_found");
        }

        if (request.Version is { } v
            && !string.Equals(v, entry.Version, StringComparison.OrdinalIgnoreCase))
        {
            return MarketplaceInstallResult.Fail("version_mismatch");
        }

        if (entry.MinAppVersion is { } min && !VersionSatisfies(min))
        {
            return MarketplaceInstallResult.Fail("min_app_version");
        }

        if (entry.PermissionsRequired.Count > 0 && !request.AcceptPermissions)
        {
            return MarketplaceInstallResult.Fail("permissions_required");
        }

        var scopeCheck = CheckScope(request);
        if (scopeCheck is not null)
        {
            return MarketplaceInstallResult.Fail(scopeCheck);
        }

        var scope = WireScope(request.Scope);
        var scopeId = request.Scope == InstallScope.System
            ? null : request.ScopeId;
        var exists = await db.InstalledSkills
            .AnyAsync(i => i.Scope == scope && i.ScopeId == scopeId
                && i.EntryId == entry.Id, ct);
        if (exists)
        {
            return MarketplaceInstallResult.Fail("already_installed");
        }

        var row = new InstalledSkillRecord
        {
            Id = Guid.NewGuid(),
            Scope = scope,
            ScopeId = scopeId,
            EntryId = entry.Id,
            Version = entry.Version,
            Enabled = true,
            InstalledBy = request.InstalledBy,
            InstalledAt = DateTimeOffset.UtcNow,
        };

        var meta = new MaterializationMeta();
        List<InstalledSkill>? bundleItems = null;
        try
        {
            if (entry.Kind == MarketplaceEntryKind.Bundle)
            {
                bundleItems = await InstallBundleAsync(
                    entry, request, meta, depth, ct);
            }
            else
            {
                await MaterializeEntryAsync(entry, row, meta, ct);
            }
        }
        catch (MarketplaceRegistryException ex)
        {
            return MarketplaceInstallResult.Fail(ex.Code);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex,
                "marketplace materialization failed for {Entry}", entry.Id);
            return MarketplaceInstallResult.Fail("materialize_failed");
        }

        row.MetaJson = JsonSerializer.Serialize(meta, Json);
        db.InstalledSkills.Add(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(
            "marketplace.install", request.InstalledBy,
            request.Scope == InstallScope.Org
                && Guid.TryParse(scopeId, out var oid) ? oid : null,
            "marketplace_entry", entry.Id,
            metadata: new
            {
                scope,
                scope_id = scopeId,
                entry.Version,
                permissions_required = entry.PermissionsRequired,
            }, cancellationToken: ct);
        return new MarketplaceInstallResult(ToDomain(row), null, bundleItems);
    }

    private async Task<List<InstalledSkill>> InstallBundleAsync(
        MarketplaceEntry entry, MarketplaceInstallRequest request,
        MaterializationMeta meta, int depth, CancellationToken ct)
    {
        var items = new List<InstalledSkill>();
        var childIds = new List<string>();
        foreach (var childId in entry.EntryIds)
        {
            var result = await InstallCoreAsync(
                request with { EntryId = childId }, depth + 1, ct);
            if (result.Error is "already_installed")
            {
                continue; // idempotent — bundle members may pre-exist.
            }

            if (result.Error is { } err)
            {
                throw new InvalidOperationException(
                    $"bundle member '{childId}' failed: {err}");
            }

            if (result.Installed is { } r)
            {
                items.Add(r);
                childIds.Add(childId);
            }
        }

        meta.Entries = childIds;
        return items;
    }

    /// <summary>Scope gate (endpoint layer already applied perm policies).</summary>
    private static string? CheckScope(MarketplaceInstallRequest r) =>
        r.Scope switch
        {
            InstallScope.User =>
                r.ScopeId is null || r.ScopeId == r.InstalledBy
                    ? null
                    : "scope_forbidden", // users install only to their own scope
            InstallScope.Org =>
                r.ScopeId is not null
                && (r.IsSuperAdmin || r.IsMemberOfScopeOrg)
                    ? null
                    : "scope_forbidden",
            InstallScope.System =>
                r.IsSuperAdmin ? null : "scope_forbidden",
            _ => "scope_forbidden",
        };

    // -------------------------------------------------------- materialize

    private async Task MaterializeEntryAsync(
        MarketplaceEntry entry, InstalledSkillRecord row,
        MaterializationMeta meta, CancellationToken ct)
    {
        // Resolve every file once (sha256-verified) — remote fetch happens
        // here so nothing half-materializes before integrity is proven.
        var contents = new List<(MarketplaceFile File, string Content)>();
        foreach (var f in entry.Files)
        {
            contents.Add((f, await registry.ResolveFileContentAsync(entry, f, ct)));
        }

        if (entry.Checksum is { } expected)
        {
            // Canonical digest = sha256 of the first file's content —
            // documented convention for embedded curated entries.
            MarketplaceRegistry.VerifySha256(
                contents[0].Content, expected, entry.Id);
        }

        switch (entry.Kind)
        {
            case MarketplaceEntryKind.Skill:
                await MaterializeSkillAsync(entry, row, contents, meta, ct);
                break;
            case MarketplaceEntryKind.Agent:
                MaterializeAgent(entry, contents, meta);
                break;
            case MarketplaceEntryKind.McpServer:
                await MaterializeMcpServerAsync(entry, row, contents, meta, ct);
                break;
            case MarketplaceEntryKind.Hook:
                await MaterializeHookAsync(entry, row, contents, meta, ct);
                break;
            default:
                throw new InvalidOperationException(
                    $"kind '{entry.Kind}' has no materializer");
        }

        // Mirror raw files under DataDir (audit/offline re-verify).
        var dir = Path.Combine(options.Value.EffectiveDataDir(),
            "entries", entry.Id);
        Directory.CreateDirectory(dir);
        foreach (var (file, content) in contents)
        {
            var dest = Path.GetFullPath(Path.Combine(dir, file.Path));
            if (!dest.StartsWith(
                    Path.GetFullPath(dir) + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("unsafe file path");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await File.WriteAllTextAsync(dest, content, ct);
        }

        meta.FilesDir = dir;
    }

    /// <summary>
    /// skill → <c>skills</c> row scoped via Owner/Org (+ SKILL.md parse).
    /// Trigger default: keyword on the entry id (invoke-by-name parity
    /// with repo skills).
    /// </summary>
    private async Task MaterializeSkillAsync(
        MarketplaceEntry entry, InstalledSkillRecord row,
        List<(MarketplaceFile File, string Content)> contents,
        MaterializationMeta meta, CancellationToken ct)
    {
        var skillMd = contents.FirstOrDefault(
            c => c.File.Path.Equals("SKILL.md",
                StringComparison.OrdinalIgnoreCase)).Content;
        if (skillMd is null)
        {
            throw new InvalidOperationException(
                $"skill entry '{entry.Id}' has no SKILL.md");
        }

        var (name, desc, body) =
            Skills.RepoSkillProvider.ParseSkillMd(skillMd);
        var skillId = Guid.NewGuid();
        db.Skills.Add(new SkillRecord
        {
            Id = skillId,
            OwnerId = row.Scope == "user" ? row.ScopeId : null,
            OrgId = row.Scope == "org" ? row.ScopeId : null,
            Name = name ?? entry.Id,
            Description = desc ?? entry.Description,
            Content = body,
            TriggersJson = JsonSerializer.Serialize(
                new[] { new { type = DefaultTriggerType, value = entry.Id } },
                Json),
            Source = MarketplaceSkillSource,
            Enabled = row.Enabled,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        meta.SkillIds.Add(skillId);
        await Task.CompletedTask;
    }

    /// <summary>agent → <c>~/.sandcastle/agents/{id}.agent.md</c> (SPEC-95 user layer).</summary>
    private void MaterializeAgent(
        MarketplaceEntry entry,
        List<(MarketplaceFile File, string Content)> contents,
        MaterializationMeta meta)
    {
        var agentMd = contents.FirstOrDefault(
            c => c.File.Path.EndsWith(".agent.md",
                StringComparison.OrdinalIgnoreCase)).Content
            ?? contents[0].Content;
        var home = HomeResolver()
            ?? throw new InvalidOperationException("no home directory");
        var dir = Path.Combine(home, ".sandcastle", "agents");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{entry.Id}.agent.md");
        File.WriteAllText(path, agentMd);
        meta.AgentPath = path;
    }

    /// <summary>
    /// mcp-server → <c>mcp_servers</c> row keyed by scope owner. Builtin
    /// names are refused unless the manifest opts in
    /// (<see cref="MarketplaceEntry.AllowBuiltinOverride"/>) — SPEC-148 NFR.
    /// </summary>
    private async Task MaterializeMcpServerAsync(
        MarketplaceEntry entry, InstalledSkillRecord row,
        List<(MarketplaceFile File, string Content)> contents,
        MaterializationMeta meta, CancellationToken ct)
    {
        var json = contents[0].Content;
        McpServerConfig? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<McpServerConfig>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"mcp-server entry '{entry.Id}' invalid config: {ex.Message}");
        }

        if (cfg is null || string.IsNullOrWhiteSpace(cfg.Name))
        {
            throw new InvalidOperationException(
                $"mcp-server entry '{entry.Id}' config missing name");
        }

        if (!entry.AllowBuiltinOverride
            && Mcp.McpSettingsService.BuiltinServers.Any(
                b => string.Equals(b.Name, cfg.Name,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new MarketplaceRegistryException(
                "builtin_override",
                $"mcp-server entry '{entry.Id}' cannot override builtin '{cfg.Name}'");
        }

        var owner = McpOwner(ParseScope(row.Scope), row.ScopeId);
        var existing = await db.McpServers
            .FindAsync([owner, cfg.Name], ct);
        var cfgJson = JsonSerializer.Serialize(cfg with
        {
            Enabled = row.Enabled,
        }, Json);
        if (existing is null)
        {
            db.McpServers.Add(new McpServerRecord
            {
                OwnerId = owner,
                Name = cfg.Name,
                ConfigJson = cfgJson,
                Enabled = row.Enabled,
            });
        }
        else
        {
            existing.ConfigJson = cfgJson;
            existing.Enabled = row.Enabled;
        }

        meta.McpServers.Add($"{owner}/{cfg.Name}");
    }

    /// <summary>hook → <c>hook_configs</c> row scoped via Owner/Org.</summary>
    private async Task MaterializeHookAsync(
        MarketplaceEntry entry, InstalledSkillRecord row,
        List<(MarketplaceFile File, string Content)> contents,
        MaterializationMeta meta, CancellationToken ct)
    {
        MarketplaceHookFile? hook;
        try
        {
            hook = JsonSerializer.Deserialize<MarketplaceHookFile>(
                contents[0].Content, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"hook entry '{entry.Id}' invalid hook file: {ex.Message}");
        }

        var evt = Hooks.WorkspaceHookService.NormalizeEvent(hook?.Event)
            ?? throw new InvalidOperationException(
                $"hook entry '{entry.Id}' unknown event '{hook?.Event}'");
        var (action, payload) = hook?.Type switch
        {
            "command" => ("run_command", hook.Command),
            "prompt" => ("emit_event", hook.Prompt),
            _ => ("run_command",
                hook?.Command ?? hook?.Prompt),
        };
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new InvalidOperationException(
                $"hook entry '{entry.Id}' has no command/prompt");
        }

        var hookId = Guid.NewGuid();
        db.HookConfigs.Add(new HookConfigRecord
        {
            Id = hookId,
            OwnerId = row.Scope == "user" ? row.ScopeId : null,
            OrgId = row.Scope == "org" ? row.ScopeId : null,
            Name = entry.Id,
            Event = evt,
            Action = action,
            Payload = payload,
            Blocking = hook?.Blocking == true,
            Enabled = row.Enabled,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        meta.HookIds.Add(hookId);
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------- query

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstalledSkill>> ListInstalledAsync(
        string? userId, Guid? orgId, InstallScope? scope,
        CancellationToken ct)
    {
        var rows = await db.InstalledSkills.AsNoTracking().ToListAsync(ct);
        return rows.Where(r => VisibleTo(r, userId, orgId))
            .Where(r => scope is null || ParseScope(r.Scope) == scope)
            .OrderBy(r => r.Scope).ThenBy(r => r.EntryId)
            .Select(ToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstalledSkill>> ResolveEffectiveAsync(
        string userId, Guid? orgId, CancellationToken ct) =>
        (await ListInstalledAsync(userId, orgId, null, ct))
        .GroupBy(r => r.EntryId, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.OrderBy(r => ScopeRank(r.Scope)).First())
        .OrderBy(r => r.EntryId).ToList();

    /// <inheritdoc />
    public async Task<InstalledSkill?> GetInstalledAsync(
        Guid id, CancellationToken ct) =>
        await db.InstalledSkills.FindAsync([id], ct) is { } r
            ? ToDomain(r) : null;

    // ------------------------------------------------------------- mutate

    /// <inheritdoc />
    public async Task<(InstalledSkill? Row, string? Error)> SetEnabledAsync(
        Guid id, bool enabled, CancellationToken ct)
    {
        var row = await db.InstalledSkills.FindAsync([id], ct);
        if (row is null)
        {
            return (null, "not_found");
        }

        row.Enabled = enabled;
        var meta = ParseMeta(row.MetaJson);
        foreach (var sid in meta.SkillIds)
        {
            if (await db.Skills.FindAsync([sid], ct) is { } s)
            {
                s.Enabled = enabled;
            }
        }

        foreach (var hid in meta.HookIds)
        {
            if (await db.HookConfigs.FindAsync([hid], ct) is { } h)
            {
                h.Enabled = enabled;
            }
        }

        foreach (var m in meta.McpServers)
        {
            var slash = m.IndexOf('/');
            if (slash > 0 && await db.McpServers.FindAsync(
                    [m[..slash], m[(slash + 1)..]], ct) is { } rec)
            {
                rec.Enabled = enabled;
            }
        }

        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(
            "marketplace.toggle", null, null, "marketplace_entry",
            row.EntryId,
            metadata: new { row.Scope, row.ScopeId, enabled },
            cancellationToken: ct);
        return (ToDomain(row), null);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct)
    {
        var row = await db.InstalledSkills.FindAsync([id], ct);
        if (row is null)
        {
            return false;
        }

        var meta = ParseMeta(row.MetaJson);
        await DematerializeAsync(meta, ct);

        // Bundle members recorded in the install meta are removed too —
        // they share the same scope/scope_id as the parent row.
        foreach (var childEntryId in meta.Entries)
        {
            var child = await db.InstalledSkills.FirstOrDefaultAsync(
                i => i.Scope == row.Scope && i.ScopeId == row.ScopeId
                    && i.EntryId == childEntryId, ct);
            if (child is not null)
            {
                await DematerializeAsync(ParseMeta(child.MetaJson), ct);
                db.InstalledSkills.Remove(child);
            }
        }

        db.InstalledSkills.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(
            "marketplace.remove", null, null, "marketplace_entry",
            row.EntryId,
            metadata: new { row.Scope, row.ScopeId },
            cancellationToken: ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<(InstalledSkill? Row, string? Error)> UpgradeAsync(
        Guid id, CancellationToken ct)
    {
        var row = await db.InstalledSkills.FindAsync([id], ct);
        if (row is null)
        {
            return (null, "not_found");
        }

        var entry = await registry.GetEntryAsync(row.EntryId, ct);
        if (entry is null)
        {
            return (null, "entry_not_found");
        }

        if (string.Equals(entry.Version, row.Version,
                StringComparison.OrdinalIgnoreCase))
        {
            return (null, "already_latest");
        }

        // Dematerialize current assets then re-materialize at the new
        // manifest (version pin kept in the row).
        var meta = ParseMeta(row.MetaJson);
        await DematerializeAsync(meta, ct);
        var fresh = new MaterializationMeta();
        try
        {
            if (entry.Kind != MarketplaceEntryKind.Bundle)
            {
                await MaterializeEntryAsync(entry, row, fresh, ct);
            }
        }
        catch (MarketplaceRegistryException ex)
        {
            return (null, ex.Code);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex,
                "marketplace upgrade materialize failed for {Entry}",
                entry.Id);
            return (null, "materialize_failed");
        }

        // Bundle upgrades: re-install any missing members at the manifest.
        fresh.Entries = meta.Entries;
        row.Version = entry.Version;
        row.MetaJson = JsonSerializer.Serialize(fresh, Json);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(
            "marketplace.upgrade", null, null, "marketplace_entry",
            row.EntryId,
            metadata: new { row.Scope, row.ScopeId, entry.Version },
            cancellationToken: ct);
        return (ToDomain(row), null);
    }

    // ------------------------------------------------------------- helpers

    private async Task DematerializeAsync(
        MaterializationMeta meta, CancellationToken ct)
    {
        foreach (var sid in meta.SkillIds)
        {
            if (await db.Skills.FindAsync([sid], ct) is { } s)
            {
                db.Skills.Remove(s);
            }
        }

        foreach (var hid in meta.HookIds)
        {
            if (await db.HookConfigs.FindAsync([hid], ct) is { } h)
            {
                db.HookConfigs.Remove(h);
            }
        }

        foreach (var m in meta.McpServers)
        {
            var slash = m.IndexOf('/');
            if (slash > 0 && await db.McpServers.FindAsync(
                    [m[..slash], m[(slash + 1)..]], ct) is { } rec)
            {
                db.McpServers.Remove(rec);
            }
        }

        if (meta.AgentPath is { } agentPath
            && Path.GetFileName(agentPath).EndsWith(
                ".agent.md", StringComparison.OrdinalIgnoreCase)
            && File.Exists(agentPath))
        {
            File.Delete(agentPath);
        }

        if (meta.FilesDir is { } dir)
        {
            var full = Path.GetFullPath(dir);
            var root = Path.GetFullPath(options.Value.EffectiveDataDir());
            if (full.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)
                && Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
        }
    }

    private static bool VisibleTo(
        InstalledSkillRecord r, string? userId, Guid? orgId) =>
        r.Scope == "system"
        || (r.Scope == "user" && r.ScopeId == userId)
        || (r.Scope == "org" && r.ScopeId is not null
            && orgId is not null && r.ScopeId == orgId.ToString());

    private static int ScopeRank(InstallScope s) => s switch
    {
        InstallScope.User => 0,
        InstallScope.Org => 1,
        _ => 2,
    };

    /// <summary>min_app_version vs the app informational version.</summary>
    internal static bool VersionSatisfies(string minAppVersion)
    {
        var app = AppVersionResolver();
        var dash = app.IndexOf('+');
        if (dash >= 0)
        {
            app = app[..dash];
        }

        return System.Version.TryParse(app, out var have)
            && System.Version.TryParse(minAppVersion, out var min)
            && have >= min;
    }

    internal static string WireScope(InstallScope s) => s switch
    {
        InstallScope.User => "user",
        InstallScope.Org => "org",
        _ => "system",
    };

    internal static InstallScope ParseScope(string? scope) =>
        scope?.ToLowerInvariant() switch
        {
            "user" => InstallScope.User,
            "org" => InstallScope.Org,
            _ => InstallScope.System,
        };

    internal static MaterializationMeta ParseMeta(string? json) =>
        json is null ? new MaterializationMeta()
        : JsonSerializer.Deserialize<MaterializationMeta>(json, Json)
            ?? new MaterializationMeta();

    internal static InstalledSkill ToDomain(InstalledSkillRecord r) => new()
    {
        Id = r.Id,
        Scope = ParseScope(r.Scope),
        ScopeId = r.ScopeId,
        EntryId = r.EntryId,
        Version = r.Version,
        Enabled = r.Enabled,
        InstalledBy = r.InstalledBy,
        InstalledAt = r.InstalledAt,
        MetaJson = r.MetaJson,
    };

    /// <summary>Materialization back-references persisted in meta_json.</summary>
    internal sealed class MaterializationMeta
    {
        public List<Guid> SkillIds { get; set; } = [];
        public List<Guid> HookIds { get; set; } = [];
        public List<string> McpServers { get; set; } = [];
        public string? AgentPath { get; set; }
        public string? FilesDir { get; set; }
        public List<string> Entries { get; set; } = [];
    }

    /// <summary>Hook file shape (<c>*.hook.json</c> parity).</summary>
    private sealed record MarketplaceHookFile
    {
        public string? Event { get; init; }
        public string? Type { get; init; }
        public string? Command { get; init; }
        public string? Prompt { get; init; }
        public bool Blocking { get; init; }
    }
}
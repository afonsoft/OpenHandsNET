namespace Sandcastle.Domain.Marketplace;

/// <summary>
/// SPEC-148 RF-001: kind of a marketplace entry — drives the materializer
/// chosen by the install pipeline.
/// </summary>
public enum MarketplaceEntryKind
{
    /// <summary><c>SKILL.md</c> + assets → skills store (<c>.sandcastle/skills/{id}</c> parity).</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("skill")]
    Skill,

    /// <summary>JSON server config → merged into the scope's MCP settings.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("mcp-server")]
    McpServer,

    /// <summary><c>*.agent.md</c> → declarative agent catalog (SPEC-95 layer).</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("agent")]
    Agent,

    /// <summary>Ruleset file → lifecycle hook config (SPEC-11 RF-004).</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("hook")]
    Hook,

    /// <summary>Ordered list of other entries installed recursively.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("bundle")]
    Bundle,
}

/// <summary>Install scope (SPEC-148 RF-002 — Devin parity).</summary>
public enum InstallScope
{
    /// <summary>Platform bundle — every user sees it; platform admin only.</summary>
    System,

    /// <summary>Applies to all members of an org; org admin+ manages.</summary>
    Org,

    /// <summary>Visible to the installing user only.</summary>
    User,
}

/// <summary>
/// One file shipped (or referenced) by a marketplace entry. Content is
/// either inline (<see cref="Content"/> — bundled/embedded registries) or
/// fetched (<see cref="Url"/> — remote, always sha256-verified).
/// </summary>
public sealed record MarketplaceFile
{
    /// <summary>Entry-relative path (e.g. <c>SKILL.md</c>, <c>assets/checklist.md</c>).</summary>
    public required string Path { get; init; }

    /// <summary>Inline content (embedded source).</summary>
    public string? Content { get; init; }

    /// <summary>Remote fetch URL (url source) — HTTPS + AllowedHosts.</summary>
    public string? Url { get; init; }

    /// <summary>Expected sha256 (hex, lowercase) of the resolved content.</summary>
    public string? Sha256 { get; init; }
}

/// <summary>
/// Marketplace registry manifest entry (SPEC-148 RF-001). Everything inside
/// is <b>untrusted</b> — data only, never host-executable code.
/// </summary>
public sealed record MarketplaceEntry
{
    /// <summary>Unique slug (lowercase kebab-case).</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Semver-ish version string (e.g. <c>1.2.0</c>).</summary>
    public required string Version { get; init; }

    /// <summary>Short description shown in the grid.</summary>
    public string? Description { get; init; }

    /// <summary>Entry author/curator.</summary>
    public string? Author { get; init; }

    /// <summary>Materialization kind.</summary>
    public MarketplaceEntryKind Kind { get; init; }

    /// <summary>Filterable tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Minimum app version — install refused below it.</summary>
    public string? MinAppVersion { get; init; }

    /// <summary>Provenance: <c>embedded</c> | <c>url</c> | <c>git</c>.</summary>
    public string Source { get; init; } = "embedded";

    /// <summary>Files to materialize (skill/hook/mcp-server/agent kinds).</summary>
    public IReadOnlyList<MarketplaceFile> Files { get; init; } = [];

    /// <summary>
    /// Capability declarations the operator must explicitly accept at
    /// install time (never auto-granted) — e.g. <c>network.egress</c>.
    /// </summary>
    public IReadOnlyList<string> PermissionsRequired { get; init; } = [];

    /// <summary>
    /// Optional sha256 of the canonical manifest payload — verified before
    /// materialization when present.
    /// </summary>
    public string? Checksum { get; init; }

    /// <summary>Optional changelog link.</summary>
    public string? ChangelogUrl { get; init; }

    /// <summary>Long-form docs rendered on the detail page (markdown).</summary>
    public string? Readme { get; init; }

    /// <summary>Bundle kind: ids of the entries to install recursively.</summary>
    public IReadOnlyList<string> EntryIds { get; init; } = [];

    /// <summary>
    /// mcp-server kind: allow the entry to override a builtin server name —
    /// refused by default (SPEC-148 NFR).
    /// </summary>
    public bool AllowBuiltinOverride { get; init; }
}

/// <summary>
/// <c>installed_skills</c> row domain-side (SPEC-148 RF-002).
/// </summary>
public sealed record InstalledSkill
{
    public required Guid Id { get; init; }

    /// <summary>Install scope.</summary>
    public required InstallScope Scope { get; init; }

    /// <summary>User id (user scope) or org id (org scope); null for system.</summary>
    public string? ScopeId { get; init; }

    /// <summary>Registry entry id.</summary>
    public required string EntryId { get; init; }

    /// <summary>Installed version (manifest at install time).</summary>
    public required string Version { get; init; }

    /// <summary>Enabled flag — toggle keeps the install record.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Actor that installed (user id string).</summary>
    public string? InstalledBy { get; init; }

    public DateTimeOffset InstalledAt { get; init; }

    /// <summary>Materialization back-references (materialized ids, paths).</summary>
    public string? MetaJson { get; init; }
}
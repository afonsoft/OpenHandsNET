using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Agent;

/// <summary>
/// SPEC-29 RF-002: catálogo efetivo de uma conversa — tools <c>deny</c> somem
/// do schema anunciado ao modelo (impossível chamar via prompt injection) e
/// <see cref="Find"/> retorna <see langword="null"/> para negadas.
/// </summary>
public sealed class PermissionedToolRegistry(
    IToolRegistry inner, PermissionEvaluator evaluator) : IToolRegistry
{
    private IReadOnlyList<AgentTool>? _filtered;

    /// <inheritdoc />
    public IReadOnlyList<AgentTool> All => _filtered ??= inner.All
        .Where(t => evaluator.Decide(t.Schema.Name) != PermissionDecision.Deny)
        .ToList();

    /// <inheritdoc />
    public AgentTool? Find(string name) =>
        evaluator.Decide(name) == PermissionDecision.Deny ? null : inner.Find(name);
}

/// <summary>
/// Monta o <see cref="PermissionEvaluator"/> da conversa: regras de org
/// (floor — <c>settings_json.agent_permissions</c>) + conversa
/// (<c>permissions_json</c>) + instância (<c>AgentLlm:Permissions</c>) +
/// overlay de <c>plan</c> mode (SPEC-29 RF-002/RF-003).
/// </summary>
public static class ConversationPermissionResolver
{
    /// <summary>Builds the evaluator for a conversation row.</summary>
    public static async Task<PermissionEvaluator> ResolveAsync(
        AppDbContext db, ConversationRecord conv, AgentLlmOptions opt,
        CancellationToken ct)
    {
        IReadOnlyList<AgentPermissionRule> orgRules = [];
        if (conv.OrgId is { } orgId)
        {
            var settingsJson = await db.Organizations.AsNoTracking()
                .Where(o => o.Id == orgId)
                .Select(o => o.SettingsJson)
                .FirstOrDefaultAsync(ct);
            orgRules = PermissionRules.ParseOrgSettings(settingsJson);
        }

        return new PermissionEvaluator(
            orgRules,
            PermissionRules.Parse(conv.PermissionsJson),
            opt.Permissions,
            planMode: string.Equals(conv.Mode, "plan",
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Filtered catalog for a conversation (deny tools hidden).</summary>
    public static PermissionedToolRegistry Catalog(
        IToolRegistry registry, PermissionEvaluator evaluator) =>
        new(registry, evaluator);
}

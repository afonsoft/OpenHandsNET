using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Routing;

/// <summary>
/// Role-aware model selection (SPEC-23 RF-002): maps <see cref="ModelRole"/>
/// to <c>AgentLlm:RoleModels</c> entries (falling back to <see
/// cref="AgentLlmOptions.Model"/>), enriches the selection with catalog data
/// (provider, context window), and walks <c>AgentLlm:FallbackModels</c> when
/// a model fails — never repeating an already-tried id.
/// </summary>
public sealed class ModelRouter(
    IModelCatalog catalog, IOptions<AgentLlmOptions> options) : IModelRouter
{
    /// <inheritdoc />
    public async Task<ModelSelection> SelectAsync(
        Guid conversationId, ModelRole role, string? preferredModel,
        CancellationToken ct)
    {
        var cfg = options.Value;
        var modelId = ResolveRoleModel(role, cfg) ?? preferredModel ?? cfg.Model;
        var info = await catalog.GetAsync(modelId, ct);
        return new ModelSelection(
            modelId,
            info?.Provider,
            info?.ContextWindow,
            Attempt: 0,
            FallbackFrom: null);
    }

    /// <inheritdoc />
    public async Task<ModelSelection?> NextFallbackAsync(
        ModelSelection current, LlmErrorKind kind, CancellationToken ct)
    {
        // Auth nunca tem fallback; overflow é tratado pelo caminho SPEC-22.
        if (kind is LlmErrorKind.Auth or LlmErrorKind.ContextOverflow)
        {
            return null;
        }

        var chain = options.Value.FallbackModels;
        if (chain.Count == 0)
        {
            return null;
        }

        // O candidato é o item da chain cuja posição segue o último modelo
        // tentado — ids ausentes da chain avançam para o primeiro fallback.
        var idx = chain.FindIndex(m =>
            string.Equals(m, current.Model, StringComparison.OrdinalIgnoreCase));
        var next = idx < 0 ? chain[0]
            : idx + 1 < chain.Count ? chain[idx + 1] : null;
        if (next is null
            || string.Equals(next, current.Model, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var info = await catalog.GetAsync(next, ct);
        return new ModelSelection(
            next, info?.Provider, info?.ContextWindow,
            current.Attempt + 1, current.Model);
    }

    private static string? ResolveRoleModel(ModelRole role, AgentLlmOptions cfg)
    {
        var key = role.ToString().ToLowerInvariant();
        return cfg.RoleModels.TryGetValue(key, out var id) && id.Length > 0
            ? id : null;
    }
}
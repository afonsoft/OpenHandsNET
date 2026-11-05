using System.Collections.Concurrent;

using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent;

/// <summary>
/// Estado por conversa do tool-search-deferred (SPEC-123) — port do
/// OWUIC <c>DeferredToolState</c> adaptado ao catálogo por nome. Tools
/// deferidas não saem no <c>tools/list</c> do LLM; <c>tool_search</c>
/// promove sob demanda. Singleton thread-safe.
/// </summary>
public sealed class DeferredToolState : IDeferredToolState
{
    private sealed class Scope
    {
        public object Gate { get; } = new();
        public HashSet<string> Deferred { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Loaded { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly ConcurrentDictionary<Guid, Scope> _scopes = new();

    /// <inheritdoc />
    public void Set(Guid conversationId, IEnumerable<string> deferredNames)
    {
        var scope = new Scope();
        foreach (var name in deferredNames)
        {
            scope.Deferred.Add(name);
        }
        if (scope.Deferred.Count > 0)
        {
            _scopes[conversationId] = scope;
        }
    }

    /// <inheritdoc />
    public bool IsDeferredOnly(Guid conversationId, string toolName) =>
        _scopes.TryGetValue(conversationId, out var scope)
        && scope.Deferred.Contains(toolName);

    /// <inheritdoc />
    public IReadOnlyCollection<string> Loaded(Guid conversationId) =>
        _scopes.TryGetValue(conversationId, out var scope)
            ? scope.Loaded.ToList() : [];

    /// <inheritdoc />
    public bool Load(Guid conversationId, string toolName)
    {
        if (!_scopes.TryGetValue(conversationId, out var scope))
        {
            return false;
        }
        lock (scope.Gate)
        {
            if (!scope.Deferred.Remove(toolName))
            {
                return false;
            }
            scope.Loaded.Add(toolName);
            return true;
        }
    }

    /// <inheritdoc />
    public void Clear(Guid conversationId) =>
        _scopes.TryRemove(conversationId, out _);
}
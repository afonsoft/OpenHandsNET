using Microsoft.Extensions.Logging;

using Sandcastle.Application.Conversations;

namespace Sandcastle.Infrastructure.Conversations;

/// <summary>
/// SPEC-146 RF-002: orquestra o clone paralelo dos repos da conversa —
/// o PRIMÁRIO é obrigatório (falha → exception → start-task ERROR);
/// secundários são best-effort (falha reportada por repo no outcome e
/// nunca aborta o run).
/// </summary>
internal static class MultiRepoCloner
{
    /// <summary>Resultado de um clone individual.</summary>
    internal sealed record RepoCloneOutcome(
        string Repository, string Path, bool Ok, string? Error);

    /// <summary>
    /// Clona todos os <paramref name="repos"/> em paralelo via
    /// <paramref name="cloneOne"/> (que recebe o repo + path in-sandbox e
    /// deve lançar na falha). Lança <see cref="InvalidOperationException"/>
    /// quando o primário (index 0) falha; secundários retornam
    /// <see cref="RepoCloneOutcome.Ok"/> = false.
    /// </summary>
    internal static async Task<IReadOnlyList<RepoCloneOutcome>> CloneAsync(
        IReadOnlyList<string> repos,
        Func<string, string, CancellationToken, Task> cloneOne,
        ILogger? logger,
        CancellationToken ct)
    {
        var paths = MultiRepoLayout.DirNames(repos)
            .Select(d => $"{MultiRepoLayout.ReposRoot}/{d}")
            .ToList();

        var clones = new List<Task<RepoCloneOutcome>>(repos.Count);
        for (var i = 0; i < repos.Count; i++)
        {
            var repo = repos[i];
            var path = paths[i];
            clones.Add(CloneOneAsync(repo, path));
        }

        var outcomes = await Task.WhenAll(clones);

        if (!outcomes[0].Ok)
        {
            throw new InvalidOperationException(
                $"primary repository '{outcomes[0].Repository}' failed to clone:"
                + $" {outcomes[0].Error}");
        }

        foreach (var o in outcomes.Where(o => !o.Ok))
        {
            logger?.LogWarning(
                "secondary repository {Repo} failed to clone: {Err}",
                o.Repository, o.Error);
        }

        return outcomes;

        async Task<RepoCloneOutcome> CloneOneAsync(string repo, string path)
        {
            try
            {
                await cloneOne(repo, path, ct);
                return new RepoCloneOutcome(repo, path, Ok: true, Error: null);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return new RepoCloneOutcome(repo, path, Ok: false,
                    Error: e.Message);
            }
        }
    }

    /// <summary>
    /// Valor do <c>ConversationStateUpdateEvent(key="repositories")</c> —
    /// status por repo consumido pelo system prompt e pela UI de setup.
    /// </summary>
    internal static object ToStateValue(
        IReadOnlyList<RepoCloneOutcome> outcomes) => new
        {
            repositories = outcomes.Select((o, i) => new
            {
                name = o.Repository,
                path = o.Path,
                primary = i == 0,
                status = o.Ok ? "cloned" : "failed",
                error = o.Error,
            }),
        };
}
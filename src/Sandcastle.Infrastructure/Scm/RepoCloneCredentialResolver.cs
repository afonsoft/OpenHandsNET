using Microsoft.Extensions.Logging;

using Sandcastle.Application.Contracts;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Scm;
using Sandcastle.Infrastructure.Git;
using Sandcastle.Infrastructure.Indexing;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Scm;

/// <summary>
/// SPEC-146 RF-002: resolve a credencial de clone por repo da conversa —
/// 1) token da conexão SCM que cobre o repo (GitHub App installation token
/// ou secret por referência, mesmo padrão de
/// <see cref="Indexing.GitRepoContentProvider"/>); 2) fallback para a
/// credencial git por host do usuário (<see cref="GitCredentialProvider"/>).
/// Falhas de resolução são logadas e NUNCA vazam o token.
/// </summary>
public sealed class RepoCloneCredentialResolver(
    AppDbContext db,
    IGitHubClientFactory ghFactory,
    GitCredentialProvider gitCredentials,
    ILogger<RepoCloneCredentialResolver> logger,
    ISecretStore? secrets = null)
{
    /// <summary>
    /// Melhor credencial disponível para clonar
    /// <paramref name="repository"/> — null = clone anônimo/sem auth.
    /// </summary>
    internal async Task<string?> ResolveAsync(
        string repository, Guid? orgId, string? ownerId,
        CancellationToken ct)
    {
        try
        {
            var conn = await FindConnectionAsync(repository, orgId, ct);
            if (conn is not null
                && await ConnectionTokenAsync(conn, ct) is { } t)
            {
                return t;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e,
                "clone credential resolution failed for {Repo}", repository);
        }

        var host = MultiRepoLayout.TryRepoHost(repository);
        return host is null ? null
            : await gitCredentials.ResolveTokenAsync(host, ownerId, ct);
    }

    /// <summary>Conexão SCM que cobre o repo (escopo da org quando houver).</summary>
    internal Task<ScmConnection?> FindConnectionAsync(
        string repository, Guid? orgId, CancellationToken ct) =>
        RepoConnectionLookup.FindCoveringAsync(db, repository, orgId, ct);

    /// <summary>
    /// Token da conexão: GitHub App → installation token; demais kinds →
    /// secret por referência (PAT). Falha → null (caller decide).
    /// </summary>
    internal async Task<string?> ConnectionTokenAsync(
        ScmConnection connection, CancellationToken ct)
    {
        try
        {
            if (connection.Kind == ScmKind.GitHub
                && connection.InstallationId is not null
                && connection.AppId is not null
                && connection.PemSecretRef is not null)
            {
                return await ghFactory.GetInstallationTokenAsync(
                    connection, ct);
            }

            if (connection.PemSecretRef is not null && secrets is not null)
            {
                return await secrets.ResolveAsync(
                    "org", connection.OrgId?.ToString() ?? "",
                    connection.PemSecretRef, ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e,
                "clone credential resolution failed (conn {Id})",
                connection.Id);
        }

        return null;
    }
}
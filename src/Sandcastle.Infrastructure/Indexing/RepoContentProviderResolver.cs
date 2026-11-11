using Microsoft.Extensions.Logging;

using Sandcastle.Application.Indexing;

namespace Sandcastle.Infrastructure.Indexing;

/// <summary>
/// <see cref="IRepoContentProvider"/> composto (SPEC-145 RF-001):
/// tenta o clone remoto (<see cref="GitRepoContentProvider"/>) quando
/// existe conexão SCM cobrindo o repo; cai no
/// <see cref="LocalRepoContentProvider"/> como fallback dev/local.
/// </summary>
public sealed class RepoContentProviderResolver(
    GitRepoContentProvider remote,
    LocalRepoContentProvider local,
    ILogger<RepoContentProviderResolver> logger) : IRepoContentProvider
{
    /// <inheritdoc />
    public async Task<IRepoContentLease?> AcquireAsync(
        RepoContentRequest request, CancellationToken ct)
    {
        try
        {
            var lease = await remote.AcquireAsync(request, ct);
            if (lease is not null)
            {
                return lease;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Falha de clone NÃO cai no local silenciosamente — indexar o
            // repo errado sob o nome certo é pior que falhar. A exceção
            // sobe e o record fica "failed" com a razão.
            logger.LogWarning(e,
                "git content provider failed for {Repo}@{Branch}",
                request.Repository, request.Branch);
            throw;
        }

        return await local.AcquireAsync(request, ct);
    }
}
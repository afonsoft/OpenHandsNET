using Microsoft.EntityFrameworkCore;

namespace Sandcastle.Infrastructure.Persistence;

/// <summary>
/// <see cref="IDbContextFactory{TContext}"/> mínima sobre as
/// <see cref="DbContextOptions{TContext}"/> já registradas por
/// <c>AddDbContext</c> — para workers paralelos que não podem
/// compartilhar um <see cref="AppDbContext"/> scoped (SPEC-152:
/// EvalRunner executa tasks concorrentes, um contexto por task).
/// </summary>
public sealed class AppDbContextFactory(
    DbContextOptions<AppDbContext> options)
    : IDbContextFactory<AppDbContext>
{
    /// <inheritdoc />
    public AppDbContext CreateDbContext() => new(options);

    /// <inheritdoc />
    public Task<AppDbContext> CreateDbContextAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AppDbContext(options));
}
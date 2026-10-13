using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace OpenHandsNET.Infrastructure.Persistence;

/// <summary>
/// Applies the SQLite runtime pragmas on every opened connection
/// (SPEC-10 RF-003): WAL journal, 5s busy timeout, foreign keys on.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    /// <inheritdoc />
    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Git;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Git;

/// <summary>
/// Resolves stored git credentials and produces an ephemeral
/// <c>GIT_ASKPASS</c> shim for in-sandbox use — tokens never reach
/// <c>.git/config</c>, command args (<c>ps</c>) or logs (SPEC-10 secrets +
/// SPEC-12 guardrails).
/// </summary>
public sealed class GitCredentialProvider(
    AppDbContext db,
    IDataProtectionProvider dataProtection,
    ILogger<GitCredentialProvider> logger)
{
    /// <summary>Secrets scope for git credentials.</summary>
    public const string Scope = "git_credential";

    /// <summary>Lists stored credential metadata (never values).</summary>
    public async Task<IReadOnlyList<GitCredentialMeta>> ListAsync(CancellationToken ct) =>
        await db.Secrets.AsNoTracking()
            .Where(s => s.Scope == Scope)
            .OrderBy(s => s.Name)
            .Select(s => new GitCredentialMeta(s.Name, "token", s.CreatedAt))
            .ToListAsync(ct);

    /// <summary>Stores a credential (encrypted) for <paramref name="host"/>.</summary>
    public async Task PutAsync(
        string host, string kind, string secret, CancellationToken ct)
    {
        var protector = dataProtection.CreateProtector("openhandsnet.secrets");
        var row = await db.Secrets.FindAsync([Scope, "system", host], ct);
        var meta = System.Text.Json.JsonSerializer.Serialize(new { kind });
        if (row is null)
        {
            db.Secrets.Add(new SecretRecord
            {
                Scope = Scope, OwnerId = "system", Name = host,
                Ciphertext = protector.Protect(secret),
                MetaJson = meta, CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            row.Ciphertext = protector.Protect(secret);
            row.MetaJson = meta;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Git credential stored for host {Host} ({Kind})",
            host, kind);
    }

    /// <summary>Deletes a stored credential.</summary>
    public async Task<bool> DeleteAsync(string host, CancellationToken ct)
    {
        if (await db.Secrets.FindAsync([Scope, "system", host], ct) is not { } row)
        {
            return false;
        }

        db.Secrets.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Decrypts the token for <paramref name="host"/> if registered.</summary>
    public async Task<string?> ResolveTokenAsync(string host, CancellationToken ct)
    {
        var row = await db.Secrets.AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.Scope == Scope && s.OwnerId == "system" && s.Name == host, ct);
        if (row is null)
        {
            return null;
        }

        return dataProtection.CreateProtector("openhandsnet.secrets")
            .Unprotect(row.Ciphertext);
    }

    /// <summary>
    /// Generated POSIX <c>askpass</c> script body — echoes the token when git
    /// prompts. Written to a 0700 tmp path inside the sandbox for one clone.
    /// </summary>
    public static string AskPassScript(string token) =>
        "#!/bin/sh\n" +
        "case \"$1\" in\n" +
        "  *Username*) printf '%s\\n' 'oauth2' ;;\n" +
        $"  *) printf '%s\\n' '{token.Replace("'", "'\\''", StringComparison.Ordinal)}' ;;\n" +
        "esac\n";
}

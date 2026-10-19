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
/// SPEC-12 guardrails). SPEC-15 RF-004: credenciais são por usuário
/// (<see cref="SecretRecord.OwnerId"/> = userId) com fallback para o escopo
/// compartilhado <c>"system"</c>.
/// </summary>
public sealed class GitCredentialProvider(
    AppDbContext db,
    IDataProtectionProvider dataProtection,
    ILogger<GitCredentialProvider> logger)
{
    /// <summary>Secrets scope for git credentials.</summary>
    public const string Scope = "git_credential";

    /// <summary>OwnerId das credenciais compartilhadas da instância.</summary>
    public const string SystemOwner = "system";

    /// <summary>Lists credential metadata of the user + shared ones (never values).</summary>
    public async Task<IReadOnlyList<GitCredentialMeta>> ListAsync(
        string? ownerId, CancellationToken ct) =>
        await db.Secrets.AsNoTracking()
            .Where(s => s.Scope == Scope &&
                (s.OwnerId == SystemOwner || s.OwnerId == ownerId))
            .OrderBy(s => s.Name)
            .Select(s => new GitCredentialMeta(
                s.Name, "token",
                s.OwnerId == SystemOwner ? "system" : "user", s.CreatedAt))
            .ToListAsync(ct);

    /// <summary>Stores a credential (encrypted) for <paramref name="host"/> owned by <paramref name="ownerId"/>.</summary>
    public async Task PutAsync(
        string host, string kind, string secret, string ownerId, CancellationToken ct)
    {
        var protector = dataProtection.CreateProtector("openhandsnet.secrets");
        var row = await db.Secrets.FindAsync([Scope, ownerId, host], ct);
        var meta = System.Text.Json.JsonSerializer.Serialize(new { kind });
        if (row is null)
        {
            db.Secrets.Add(new SecretRecord
            {
                Scope = Scope,
                OwnerId = ownerId,
                Name = host,
                Ciphertext = protector.Protect(secret),
                MetaJson = meta,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            row.Ciphertext = protector.Protect(secret);
            row.MetaJson = meta;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Git credential stored for host {Host} ({Kind}, owner {Owner})",
            host, kind, ownerId == SystemOwner ? SystemOwner : "user");
    }

    /// <summary>Deletes a credential owned by <paramref name="ownerId"/>.</summary>
    public async Task<bool> DeleteAsync(string host, string ownerId, CancellationToken ct)
    {
        if (await db.Secrets.FindAsync([Scope, ownerId, host], ct) is not { } row)
        {
            return false;
        }

        db.Secrets.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Resolves the owner (conversation creator) then decrypts the token for
    /// <paramref name="host"/> — per-user first, shared <c>system</c> fallback.
    /// </summary>
    public async Task<string?> ResolveTokenForConversationAsync(
        string host, Guid? conversationId, CancellationToken ct)
    {
        var ownerId = conversationId is null ? null
            : await db.Conversations.AsNoTracking()
                .Where(c => c.Id == conversationId)
                .Select(c => c.CreatedByUserId)
                .FirstOrDefaultAsync(ct);
        return await ResolveTokenAsync(host, ownerId, ct);
    }

    /// <summary>
    /// Decrypts the token for <paramref name="host"/>: prefere a credencial do
    /// <paramref name="ownerId"/> e cai para a compartilhada (<c>system</c>).
    /// </summary>
    public async Task<string?> ResolveTokenAsync(
        string host, string? ownerId, CancellationToken ct)
    {
        var rows = await db.Secrets.AsNoTracking()
            .Where(s => s.Scope == Scope && s.Name == host &&
                (s.OwnerId == SystemOwner || s.OwnerId == ownerId))
            .Select(s => new { s.OwnerId, s.Ciphertext })
            .ToListAsync(ct);

        var row = rows.FirstOrDefault(r => r.OwnerId == ownerId)
            ?? rows.FirstOrDefault(r => r.OwnerId == SystemOwner);
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

    /// <summary>
    /// <c>~/.config/gh/hosts.yml</c> body — autentica o gh CLI para
    /// <paramref name="host"/> sem depender de env (SPEC-15 RF-004).
    /// </summary>
    public static string GhHostsFile(string host, string token) =>
        $"{host}:\n" +
        "    git_protocol: https\n" +
        $"    oauth_token: {token}\n";
}
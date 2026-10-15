using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Application.Contracts;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>
/// <see cref="ISecretStore"/> backed by <c>secrets</c> rows encrypted with
/// ASP.NET DataProtection (keys persisted via
/// <see cref="AppDbContext.DataProtectionKeys"/>). Ciphertext-only at rest;
/// plaintext exists only in the resolve call site (sandbox env injection).
/// </summary>
public sealed class SecretStore(
    AppDbContext db, IDataProtectionProvider protection) : ISecretStore
{
    private const string Purpose = "openhandsnet.secrets.v1";

    /// <inheritdoc />
    public async Task<string?> ResolveAsync(
        string scope, string ownerId, string name, CancellationToken ct)
    {
        var row = await db.Secrets.AsNoTracking().FirstOrDefaultAsync(
            s => s.Scope == scope && s.OwnerId == ownerId && s.Name == name, ct);
        if (row is null)
        {
            return null;
        }

        var protector = protection.CreateProtector(Purpose, row.Nonce ?? "");
        return protector.Unprotect(row.Ciphertext);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListNamesAsync(
        string scope, string ownerId, CancellationToken ct) =>
        await db.Secrets.AsNoTracking()
            .Where(s => s.Scope == scope && s.OwnerId == ownerId)
            .Select(s => s.Name).OrderBy(n => n).ToListAsync(ct);

    /// <inheritdoc />
    public async Task SetAsync(string scope, string ownerId, string name,
        string plaintext, CancellationToken ct)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var protector = protection.CreateProtector(Purpose, nonce);
        var ciphertext = protector.Protect(plaintext);
        var row = await db.Secrets.FindAsync(
            [scope, ownerId, name], ct);
        if (row is null)
        {
            db.Secrets.Add(new SecretRecord
            {
                Scope = scope, OwnerId = ownerId, Name = name,
                Ciphertext = ciphertext, Nonce = nonce,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            row.Ciphertext = ciphertext;
            row.Nonce = nonce;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string scope, string ownerId, string name, CancellationToken ct)
    {
        var row = await db.Secrets.FindAsync([scope, ownerId, name], ct);
        if (row is null)
        {
            return false;
        }

        db.Secrets.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }
}

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Application.BrowserProfiles;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.BrowserProfiles;

/// <summary>SPEC-64 RF-001 — row do profile; ciphertext-only.</summary>
public sealed class BrowserProfileRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public required Guid? OrgId { get; set; }
    public required string OwnerId { get; set; }
    public required string Domain { get; set; }
    public required string Name { get; set; }
    public required string Ciphertext { get; set; }
    public string? Nonce { get; set; }
    public bool Shared { get; set; }
    public DateTimeOffset CreatedAt { get; set; } =
        DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// SPEC-64: storage state criptografado com ASP.NET DataProtection
/// (mesmo mecanismo de <see cref="Identity.SecretStore"/> — keys
/// persistidas via <c>DataProtectionKeys</c>).
/// </summary>
public sealed class BrowserProfileService(
    AppDbContext db, IDataProtectionProvider protection)
    : IBrowserProfileService
{
    private const string Purpose = "openhandsnet.browserprofiles.v1";

    public async Task<IReadOnlyList<BrowserProfileInfo>> ListAsync(
        string ownerId, bool includeShared, CancellationToken ct)
    {
        var mine = await db.BrowserProfiles.AsNoTracking()
            .Where(p => p.OwnerId == ownerId).ToListAsync(ct);
        var rows = mine;
        if (includeShared)
        {
            var shared = await db.BrowserProfiles.AsNoTracking()
                .Where(p => p.Shared && p.OwnerId != ownerId)
                .ToListAsync(ct);
            rows = [.. mine, .. shared];
        }
        return rows.OrderBy(p => p.Domain).Select(ToInfo).ToList();
    }

    public async Task<BrowserProfileInfo?> GetAsync(
        Guid id, string ownerId, bool isAdmin, CancellationToken ct)
    {
        var p = await VisibleAsync(id, ownerId, isAdmin, ct);
        return p is null ? null : ToInfo(p);
    }

    public async Task<BrowserProfileInfo> SaveAsync(
        string ownerId, string domain, string name,
        string storageStateJson, DateTimeOffset? expiresAt,
        CancellationToken ct)
    {
        var normalized = IBrowserProfileService.NormalizeDomain(domain);
        var row = await db.BrowserProfiles.FirstOrDefaultAsync(
            p => p.OwnerId == ownerId && p.Domain == normalized, ct);
        var nonce = Guid.NewGuid().ToString("N");
        var ciphertext = protection
            .CreateProtector(Purpose, nonce)
            .Protect(storageStateJson);
        if (row is null)
        {
            row = new BrowserProfileRecord
            {
                Id = Guid.NewGuid(), OrgId = db.CurrentOrgId,
                OwnerId = ownerId, Domain = normalized,
                Name = name, Ciphertext = ciphertext, Nonce = nonce,
                ExpiresAt = expiresAt,
            };
            db.BrowserProfiles.Add(row);
        }
        else
        {
            row.Name = name;
            row.Ciphertext = ciphertext;
            row.Nonce = nonce;
            row.ExpiresAt = expiresAt;
        }
        await db.SaveChangesAsync(ct);
        return ToInfo(row);
    }

    public async Task<string> ResolveStateAsync(
        Guid id, string ownerId, bool isAdmin, CancellationToken ct)
    {
        var p = await VisibleAsync(id, ownerId, isAdmin, ct)
            ?? throw new KeyNotFoundException(
                $"browser profile {id} not found");
        if (p.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow)
        {
            throw new BrowserProfileExpiredException(id);
        }
        p.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return protection
            .CreateProtector(Purpose, p.Nonce ?? "")
            .Unprotect(p.Ciphertext);
    }

    public async Task<bool> DeleteAsync(
        Guid id, string ownerId, bool isAdmin, CancellationToken ct)
    {
        var p = await db.BrowserProfiles.FindAsync([id], ct);
        if (p is null || (!isAdmin && p.OwnerId != ownerId))
        {
            return false;
        }
        db.BrowserProfiles.Remove(p);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<BrowserProfileRecord?> VisibleAsync(
        Guid id, string ownerId, bool isAdmin, CancellationToken ct)
    {
        var p = await db.BrowserProfiles.FindAsync([id], ct);
        if (p is null)
        {
            return null;
        }
        return isAdmin || p.OwnerId == ownerId || p.Shared ? p : null;
    }

    private static BrowserProfileInfo ToInfo(BrowserProfileRecord p) =>
        new(p.Id, p.OrgId, p.OwnerId, p.Domain, p.Name, p.Shared,
            p.CreatedAt, p.LastUsedAt, p.ExpiresAt);
}

/// <summary>
/// SPEC-64 RF-004 — TOTP RFC 6238 (HMAC-SHA1, 30s, 6 dígitos).
/// O segredo base32 nunca sai do vault; só o código efêmero é
/// retornado para o campo de input.
/// </summary>
public static class TotpGenerator
{
    /// <summary>Gera o código atual (ou para <paramref name="when"/>).</summary>
    public static string Generate(
        string base32Secret, DateTimeOffset? when = null,
        int stepSeconds = 30, int digits = 6)
    {
        var key = Base32Decode(base32Secret);
        var counter = (when ?? DateTimeOffset.UtcNow)
            .ToUnixTimeSeconds() / stepSeconds;
        var msg = new byte[8];
        System.Buffers.Binary.BinaryPrimitives
            .WriteInt64BigEndian(msg, counter);
        var hash = HMACSHA1.HashData(key, msg);
        var offset = hash[^1] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];
        return (code % (int)Math.Pow(10, digits))
            .ToString().PadLeft(digits, '0');
    }

    private static byte[] Base32Decode(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = s.Trim().TrimEnd('=')
            .Replace(" ", "", StringComparison.Ordinal)
            .ToUpperInvariant();
        var bits = new StringBuilder(clean.Length * 5);
        foreach (var c in clean)
        {
            var v = alphabet.IndexOf(c);
            if (v < 0)
            {
                throw new ArgumentException(
                    "invalid base32 totp secret", nameof(s));
            }
            bits.Append(Convert.ToString(v, 2).PadLeft(5, '0'));
        }
        var bytes = new byte[bits.Length / 8];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(
                bits.ToString(i * 8, 8), 2);
        }
        return bytes;
    }
}

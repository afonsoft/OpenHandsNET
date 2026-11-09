using System.Net.Http.Headers;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Agent;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Agent.Routing;

/// <summary>
/// SPEC-134 RF-002/RF-003/RF-004: BYOK por usuário — CRUD owner-scoped +
/// resolução por run (user → BYOR → global). Plaintext persiste só como
/// DataProtection ciphertext e sai do service só no
/// <see cref="UserLlmKeyCallContext"/> em voo — nunca serializado, logado
/// ou cacheado em claro.
/// </summary>
public sealed class UserLlmKeyService(
    AppDbContext db,
    IDataProtectionProvider protection,
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    ILogger<UserLlmKeyService> logger) : IUserLlmKeyService
{
    private const string Purpose = "sandcastle.user_llm_keys.v1";
    private static readonly TimeSpan TestWindow = TimeSpan.FromMinutes(1);
    private const int TestMaxPerWindow = 5;

    /// <inheritdoc />
    public IReadOnlyList<LlmKeyProviderDescriptor> Providers =>
        LlmKeyProviders.Catalog;

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserLlmKeyInfo>> ListAsync(
        string userId, CancellationToken ct) =>
        (await db.UserLlmKeys.AsNoTracking()
            .Where(k => k.UserId == userId)
            .OrderBy(k => k.Provider)
            .ToListAsync(ct))
            .Select(k => new UserLlmKeyInfo(
                k.Provider, Mask(Decrypt(k)), k.BaseUrl, k.UpdatedAt))
            .ToList();

    /// <inheritdoc />
    public async Task<UserLlmKeyInfo> UpsertAsync(
        string userId, string provider, string key, string? baseUrl,
        CancellationToken ct)
    {
        var descriptor = LlmKeyProviders.Find(provider)
            ?? throw new LlmKeyValidationException("unknown_provider");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new LlmKeyValidationException("key_required");
        }

        var normalizedUrl = NormalizeBaseUrl(descriptor, baseUrl);
        var nonce = Guid.NewGuid().ToString("N");
        var ciphertext = protection.CreateProtector(Purpose, nonce)
            .Protect(key.Trim());

        var row = await db.UserLlmKeys.FindAsync(
            [userId, descriptor.Id], ct);
        if (row is null)
        {
            row = new UserLlmKeyRecord
            {
                UserId = userId,
                Provider = descriptor.Id,
                KeyCiphertext = ciphertext,
                Nonce = nonce,
                BaseUrl = normalizedUrl,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.UserLlmKeys.Add(row);
        }
        else
        {
            row.KeyCiphertext = ciphertext;
            row.Nonce = nonce;
            row.BaseUrl = normalizedUrl;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return new UserLlmKeyInfo(
            descriptor.Id, Mask(key.Trim()), normalizedUrl, row.UpdatedAt);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string userId, string provider, CancellationToken ct)
    {
        var descriptor = LlmKeyProviders.Find(provider);
        if (descriptor is null)
        {
            return false;
        }

        var row = await db.UserLlmKeys.FindAsync(
            [userId, descriptor.Id], ct);
        if (row is null)
        {
            return false;
        }

        db.UserLlmKeys.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<UserLlmKeyCallContext?> ResolveAsync(
        string userId, string? modelProvider, CancellationToken ct)
    {
        var descriptor = LlmKeyProviders.Find(modelProvider);
        if (descriptor is null)
        {
            return null;
        }

        var row = await db.UserLlmKeys.AsNoTracking()
            .FirstOrDefaultAsync(
                k => k.UserId == userId && k.Provider == descriptor.Id, ct);
        if (row is null)
        {
            return null;
        }

        return new UserLlmKeyCallContext(
            descriptor.Id, EffectiveBaseUrl(descriptor, row.BaseUrl),
            Decrypt(row));
    }

    /// <inheritdoc />
    public async Task<(bool Ok, string? Detail)> TestAsync(
        string userId, string provider, CancellationToken ct)
    {
        var descriptor = LlmKeyProviders.Find(provider)
            ?? throw new LlmKeyValidationException("unknown_provider");
        var cacheKey = $"llmkeytest:{userId}:{descriptor.Id}";
        var used = cache.GetOrCreate(cacheKey, e =>
        {
            e.AbsoluteExpirationRelativeToNow = TestWindow;
            return 0;
        });
        if (used >= TestMaxPerWindow)
        {
            return (false, "rate_limited");
        }

        cache.Set(cacheKey, used + 1, TestWindow);

        var ctx = await ResolveAsync(userId, descriptor.Id, ct);
        if (ctx is null)
        {
            return (false, "key_not_configured");
        }

        try
        {
            var client = httpFactory.CreateClient("agent-llm");
            client.Timeout = TimeSpan.FromSeconds(10);
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{ctx.BaseUrl.TrimEnd('/')}/models");
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", ctx.Key);
            using var response = await client.SendAsync(request, ct);
            // Sem eco do payload — só o status classifica.
            return response.IsSuccessStatusCode
                ? (true, null)
                : (false, $"http_{(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("LLM key test failed for provider {Provider}: {Type}",
                descriptor.Id, ex.GetType().Name);
            return (false, "unreachable");
        }
    }

    private string Decrypt(UserLlmKeyRecord row) =>
        protection.CreateProtector(Purpose, row.Nonce)
            .Unprotect(row.KeyCiphertext);

    private static string? NormalizeBaseUrl(
        LlmKeyProviderDescriptor descriptor, string? baseUrl)
    {
        var trimmed = baseUrl?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return descriptor.RequiresBaseUrl
                ? throw new LlmKeyValidationException("base_url_required")
                : null;
        }

        if (!LlmKeyProviders.IsValidBaseUrl(trimmed))
        {
            throw new LlmKeyValidationException("base_url_invalid");
        }

        return trimmed.TrimEnd('/');
    }

    private static string EffectiveBaseUrl(
        LlmKeyProviderDescriptor descriptor, string? baseUrl) =>
        !string.IsNullOrEmpty(baseUrl)
            ? baseUrl.TrimEnd('/')
            : descriptor.DefaultBaseUrl.TrimEnd('/');

    /// <summary>Masked = <c>sk-…{last4}</c> — cru nunca serializa.</summary>
    internal static string Mask(string key)
    {
        var tail = key.Length >= 4 ? key[^4..] : key;
        return $"sk-…{tail}";
    }
}
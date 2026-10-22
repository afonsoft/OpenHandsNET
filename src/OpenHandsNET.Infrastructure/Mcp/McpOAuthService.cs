using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Mcp;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Mcp;

/// <summary>
/// OAuth 2.1 + PKCE para servidores MCP (SPEC-07 RF-003).
/// State pendente em <c>secrets</c> (scope <c>mcp-oauth-state</c>, one-shot);
/// tokens cifrados via DataProtection em scope <c>mcp-oauth</c>; refresh
/// automático <see cref="McpOptions.OAuthRefreshSkewSeconds"/>s antes de expirar.
/// </summary>
public sealed class McpOAuthService(
    AppDbContext db,
    IMcpSettingsService settings,
    IHttpClientFactory httpFactory,
    IDataProtectionProvider dp,
    IOptions<McpOptions> options,
    ILogger<McpOAuthService> logger) : IMcpOAuthService
{
    private const string Local = "local";
    private const string TokenScope = "mcp-oauth";
    private const string StateScope = "mcp-oauth-state";
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);

    private sealed record PendingState(
        string Server, string Verifier, string Redirect, long ExpiresAtTicks);

    private sealed record TokenSet(
        string AccessToken, string? RefreshToken, long ExpiresAtTicks);

    /// <inheritdoc />
    public async Task<Uri?> BuildAuthorizeUrlAsync(
        string ownerId, string serverName, Uri callbackUri,
        CancellationToken ct = default)
    {
        var cfg = await settings.GetAsync(ownerId, serverName, ct);
        if (cfg?.OAuthAuthorizeUrl is null || cfg.OAuthClientId is null)
        {
            return null;
        }

        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var challenge = Convert.ToBase64String(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        await SaveSecretAsync(StateScope, ownerId, state,
            JsonSerializer.Serialize(new PendingState(
                serverName, verifier, callbackUri.ToString(),
                DateTimeOffset.UtcNow.Add(StateTtl).UtcTicks)), ct);

        var q = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = cfg.OAuthClientId,
            ["redirect_uri"] = callbackUri.ToString(),
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        var ub = new UriBuilder(cfg.OAuthAuthorizeUrl)
        {
            Query = string.Join('&', q.Select(kv =>
                $"{kv.Key}={Uri.EscapeDataString(kv.Value)}")),
        };
        return ub.Uri;
    }

    /// <inheritdoc />
    public async Task<string> HandleCallbackAsync(
        string code, string state, Uri callbackUri, CancellationToken ct = default)
    {
        var pending = await LoadSecretAsync<PendingState>(
            StateScope, Local, state, ct)
            ?? throw new InvalidOperationException("oauth state inválido/expirado");
        await DeleteSecretAsync(StateScope, Local, state, ct);
        if (DateTimeOffset.UtcNow.UtcTicks > pending.ExpiresAtTicks)
        {
            throw new InvalidOperationException("oauth state expirado");
        }

        var cfg = await settings.GetAsync(Local, pending.Server, ct)
            ?? throw new InvalidOperationException(
                $"mcp server '{pending.Server}' removido");
        if (cfg.OAuthTokenUrl is null || cfg.OAuthClientId is null)
        {
            throw new InvalidOperationException(
                $"mcp server '{pending.Server}' sem token_url/client_id");
        }

        var resp = await httpFactory.CreateClient("mcp-oauth").PostAsync(
            cfg.OAuthTokenUrl,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = callbackUri.ToString(),
                ["client_id"] = cfg.OAuthClientId,
                ["code_verifier"] = pending.Verifier,
            }), ct);
        resp.EnsureSuccessStatusCode();
        var token = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        await StoreTokenAsync(Local, pending.Server, token, ct);
        return pending.Server;
    }

    /// <inheritdoc />
    public async Task<string?> GetAccessTokenAsync(
        string ownerId, string serverName, CancellationToken ct = default)
    {
        var token = await LoadSecretAsync<TokenSet>(
            TokenScope, ownerId, serverName, ct);
        if (token is null)
        {
            return null;
        }

        var skewTicks = TimeSpan.FromSeconds(
            options.Value.OAuthRefreshSkewSeconds).Ticks;
        if (DateTimeOffset.UtcNow.UtcTicks + skewTicks < token.ExpiresAtTicks)
        {
            return token.AccessToken;
        }

        if (token.RefreshToken is null)
        {
            return null;
        }

        var cfg = await settings.GetAsync(ownerId, serverName, ct);
        if (cfg?.OAuthTokenUrl is null || cfg.OAuthClientId is null)
        {
            return null;
        }

        try
        {
            var resp = await httpFactory.CreateClient("mcp-oauth").PostAsync(
                cfg.OAuthTokenUrl,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = token.RefreshToken,
                    ["client_id"] = cfg.OAuthClientId,
                }), ct);
            resp.EnsureSuccessStatusCode();
            var fresh = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
            await StoreTokenAsync(ownerId, serverName, fresh, ct,
                fallbackRefresh: token.RefreshToken);
            return (await LoadSecretAsync<TokenSet>(
                TokenScope, ownerId, serverName, ct))?.AccessToken;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "refresh OAuth falhou p/ {Server}", serverName);
            return null;
        }
    }

    /// <inheritdoc />
    public Task DisconnectAsync(
        string ownerId, string serverName, CancellationToken ct = default) =>
        DeleteSecretAsync(TokenScope, ownerId, serverName, ct);

    /// <inheritdoc />
    public async Task<bool> IsAuthenticatedAsync(
        string ownerId, string serverName, CancellationToken ct = default) =>
        await GetAccessTokenAsync(ownerId, serverName, ct) is not null;

    // ------------------------------------------------------------- helpers

    private async Task StoreTokenAsync(
        string ownerId, string server, JsonElement token,
        CancellationToken ct, string? fallbackRefresh = null)
    {
        var access = token.GetProperty("access_token").GetString()!;
        var refresh = token.TryGetProperty("refresh_token", out var r)
            ? r.GetString() : fallbackRefresh;
        var expiresIn = token.TryGetProperty("expires_in", out var e)
            ? e.GetInt64() : 3600;
        await SaveSecretAsync(TokenScope, ownerId, server,
            JsonSerializer.Serialize(new TokenSet(access, refresh,
                DateTimeOffset.UtcNow.AddSeconds(expiresIn).UtcTicks)), ct);
    }

    private async Task SaveSecretAsync(
        string scope, string owner, string name, string plaintext,
        CancellationToken ct)
    {
        var protector = dp.CreateProtector("openhandsnet.secrets");
        var row = await db.Secrets.FindAsync([scope, owner, name], ct);
        if (row is null)
        {
            db.Secrets.Add(new SecretRecord
            {
                Scope = scope,
                OwnerId = owner,
                Name = name,
                Ciphertext = protector.Protect(plaintext),
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            row.Ciphertext = protector.Protect(plaintext);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<T?> LoadSecretAsync<T>(
        string scope, string owner, string name, CancellationToken ct)
    {
        if (await db.Secrets.FindAsync([scope, owner, name], ct) is not { } row)
        {
            return default;
        }

        try
        {
            var plain = dp.CreateProtector("openhandsnet.secrets")
                .Unprotect(row.Ciphertext);
            return JsonSerializer.Deserialize<T>(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            logger.LogWarning(ex, "secret {Scope}/{Name} ilegível", scope, name);
            return default;
        }
    }

    private async Task DeleteSecretAsync(
        string scope, string owner, string name, CancellationToken ct)
    {
        if (await db.Secrets.FindAsync([scope, owner, name], ct) is { } row)
        {
            db.Secrets.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }
}
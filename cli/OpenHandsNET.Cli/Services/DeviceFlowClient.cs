using System.Net.Http.Json;
using System.Text.Json;

namespace OpenHandsNET.Cli.Services;

/// <summary>
/// OAuth2 Device Authorization Grant (RFC 8628) — o servidor expõe
/// <c>/oauth/device/code</c> + <c>/oauth/token</c> (SPEC-08). Em deployment
/// <c>oss</c> sem OIDC, o comando orienta para <c>--api-key</c>.
/// </summary>
public sealed class DeviceFlowClient(HttpClient http)
{
    public sealed record DeviceCodeResponse(
        string DeviceCode, string UserCode, string VerificationUri,
        string? VerificationUriComplete, int Interval, int ExpiresIn);

    public sealed record TokenResponse(
        string AccessToken, string? RefreshToken, int? ExpiresIn);

    /// <summary>Pede o device code ao servidor.</summary>
    public async Task<DeviceCodeResponse> RequestCodeAsync(
        string serverUrl, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            $"{serverUrl.TrimEnd('/')}/oauth/device/code",
            new { client_id = "openhandsnet-cli" }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"device flow unavailable ({(int)resp.StatusCode}) — " +
                "servidor OSS sem OIDC? use `openhandsnet login --api-key sk-…`");
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new DeviceCodeResponse(
            doc.GetProperty("device_code").GetString()!,
            doc.GetProperty("user_code").GetString()!,
            doc.GetProperty("verification_uri").GetString()!,
            doc.TryGetProperty("verification_uri_complete", out var v)
                ? v.GetString() : null,
            doc.TryGetProperty("interval", out var i) ? i.GetInt32() : 5,
            doc.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 900);
    }

    /// <summary>
    /// Poll do token endpoint até aprovação/expiração. Retorna null em timeout.
    /// </summary>
    public async Task<TokenResponse?> PollTokenAsync(
        string serverUrl, DeviceCodeResponse code,
        Action<string>? onPending = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresIn);
        var interval = Math.Max(1, code.Interval);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var resp = await http.PostAsJsonAsync(
                $"{serverUrl.TrimEnd('/')}/oauth/token",
                new
                {
                    grant_type = "urn:ietf:params:oauth:grant-type:device_code",
                    device_code = code.DeviceCode,
                    client_id = "openhandsnet-cli",
                }, ct);
            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (resp.IsSuccessStatusCode)
            {
                return new TokenResponse(
                    doc.GetProperty("access_token").GetString()!,
                    doc.TryGetProperty("refresh_token", out var r)
                        ? r.GetString() : null,
                    doc.TryGetProperty("expires_in", out var e)
                        ? e.GetInt32() : null);
            }

            var error = doc.TryGetProperty("error", out var err)
                ? err.GetString() : "unknown";
            if (error is "authorization_pending")
            {
                onPending?.Invoke("waiting for browser authorization…");
            }
            else if (error is "slow_down")
            {
                interval += 5;
            }
            else if (error is "access_denied" or "expired_token")
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
        }

        return null;
    }
}
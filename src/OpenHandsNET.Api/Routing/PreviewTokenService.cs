using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OpenHandsNET.Sandboxing;

namespace OpenHandsNET.Api.Routing;

/// <summary>
/// SPEC-40 RF-004: preview tokens stateless-HMAC para surfaces públicas
/// do sandbox (ttyd/code-server atrás do <see cref="SandboxHostRouter"/>).
///
/// Token: <c>pv1.{sandboxId:N}.{port}.{expUnix}.{nonce}.{sig}</c> onde
/// <c>sig</c> = HMAC-SHA256(secret, "pv1.id.port.exp.nonce"). Binding
/// sandbox+port; exp ≤ 15 min; nada é persistido nem vai ao event stream.
/// Secret: <c>Sandbox:PreviewTokenSecret</c>; ausente → chave efêmera
/// por boot (tokens morrem no restart — aceitável, janela é curta).
/// </summary>
public sealed class PreviewTokenService
{
    /// <summary>Janela máxima de validade de um token.</summary>
    public static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(15);

    private readonly byte[] _secret;

    /// <summary>Cria o serviço; secret vem de <c>Sandbox:PreviewTokenSecret</c>.</summary>
    public PreviewTokenService(
        IOptions<SandboxOptions> options,
        ILogger<PreviewTokenService> logger)
    {
        var configured = options.Value.PreviewTokenSecret;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _secret = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        }
        else
        {
            _secret = RandomNumberGenerator.GetBytes(32);
            logger.LogWarning(
                "Sandbox:PreviewTokenSecret unset — preview tokens são " +
                "efêmeros por boot (morrem no restart)");
        }
    }

    /// <summary>Emite um token para <paramref name="port"/> do sandbox.</summary>
    public (string Token, DateTimeOffset ExpiresAt) Mint(
        Guid sandboxId, int port, TimeSpan? ttl = null)
    {
        var window = ttl is { } t && t <= MaxTtl ? t : MaxTtl;
        var exp = DateTimeOffset.UtcNow.Add(window);
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var payload = $"pv1.{sandboxId:N}.{port}.{exp.ToUnixTimeSeconds()}.{nonce}";
        var sig = Sign(payload);
        return ($"{payload}.{sig}", exp);
    }

    /// <summary>
    /// Valida <paramref name="token"/> contra o binding esperado —
    /// assinatura, expiração e sandbox+port têm de bater.
    /// </summary>
    public bool Validate(string? token, Guid sandboxId, int port)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 6 || parts[0] != "pv1"
            || !Guid.TryParseExact(parts[1], "N", out var id)
            || id != sandboxId
            || !int.TryParse(parts[2], out var p) || p != port
            || !long.TryParse(parts[3], out var exp))
        {
            return false;
        }

        var payload = string.Join('.', parts[..5]);
        var expected = Sign(payload);
        byte[] provided;
        try
        {
            provided = Convert.FromHexString(parts[5]);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected), provided)
            && DateTimeOffset.UtcNow.ToUnixTimeSeconds() <= exp;
    }

    private string Sign(string payload) =>
        Convert.ToHexStringLower(
            HMACSHA256.HashData(_secret, Encoding.ASCII.GetBytes(payload)));
}

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Marketplace;
using Sandcastle.Domain.Marketplace;

namespace Sandcastle.Infrastructure.Marketplace;

/// <summary>
/// SPEC-148 RF-001/RF-005: registry loader. Precedence:
/// <c>Marketplace:RegistryUrl</c> (remote — HTTPS + AllowedHosts) &gt;
/// <c>Marketplace:RegistryFile</c> (local override) &gt; bundled
/// <c>marketplace.json</c> (offline default). All entry content is
/// untrusted; remote file bytes always pass sha256 verification.
/// </summary>
public sealed partial class MarketplaceRegistry(
    IOptions<MarketplaceOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<MarketplaceRegistry> logger) : IMarketplaceRegistry
{
    private const string ResourceName =
        "Sandcastle.Infrastructure.Marketplace.marketplace.json";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower),
        },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<MarketplaceEntry>? _cache;
    private DateTimeOffset _loadedAt;

    /// <inheritdoc />
    public async Task<IReadOnlyList<MarketplaceEntry>> ListEntriesAsync(
        CancellationToken ct)
    {
        if (_cache is { } hit && _loadedAt > DateTimeOffset.UtcNow - CacheTtl)
        {
            return hit;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is { } again
                && _loadedAt > DateTimeOffset.UtcNow - CacheTtl)
            {
                return again;
            }

            var doc = await LoadDocumentAsync(ct);
            var parsed = Parse(doc);
            _cache = parsed;
            _loadedAt = DateTimeOffset.UtcNow;
            return parsed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<MarketplaceEntry?> GetEntryAsync(
        string id, CancellationToken ct) =>
        (await ListEntriesAsync(ct))
        .FirstOrDefault(e => string.Equals(
            e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public async Task<string> ResolveFileContentAsync(
        MarketplaceEntry entry, MarketplaceFile file, CancellationToken ct)
    {
        if (file.Content is { } inline)
        {
            VerifySha256(inline, file.Sha256, file.Path);
            return inline;
        }

        if (file.Url is null)
        {
            throw new MarketplaceRegistryException(
                "invalid_manifest",
                $"entry '{entry.Id}' file '{file.Path}' has neither content nor url");
        }

        if (file.Sha256 is null)
        {
            // Remote content must always be pinned — untrusted bytes.
            throw new MarketplaceRegistryException(
                "checksum_required",
                $"entry '{entry.Id}' file '{file.Path}' requires sha256 for remote fetch");
        }

        var bytes = await FetchAsync(file.Url, ct);
        var content = System.Text.Encoding.UTF8.GetString(bytes);
        VerifySha256(content, file.Sha256, file.Path);
        return content;
    }

    // ------------------------------------------------------------------ load

    private async Task<string> LoadDocumentAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!string.IsNullOrWhiteSpace(o.RegistryUrl))
        {
            var bytes = await FetchAsync(o.RegistryUrl!, ct);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        if (!string.IsNullOrWhiteSpace(o.RegistryFile))
        {
            if (!File.Exists(o.RegistryFile))
            {
                throw new MarketplaceRegistryException(
                    "registry_unavailable",
                    $"registry file '{o.RegistryFile}' not found");
            }

            return await File.ReadAllTextAsync(o.RegistryFile, ct);
        }

        var asm = typeof(MarketplaceRegistry).Assembly;
        await using var stream = asm.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new MarketplaceRegistryException(
                "registry_unavailable",
                "bundled marketplace.json resource missing");
        }

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    // ------------------------------------------------------------------ fetch

    /// <summary>
    /// Fetches a remote document — HTTPS only and the host must match
    /// <see cref="MarketplaceOptions.AllowedHosts"/> (exact hostname or
    /// IPv4 CIDR). DNS results are re-checked so a public name cannot
    /// smuggle a private address past the allowlist.
    /// </summary>
    private async Task<byte[]> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new MarketplaceRegistryException(
                "registry_forbidden",
                $"remote fetch requires https: '{url}'");
        }

        EnsureHostAllowed(uri);

        var client = httpClientFactory.CreateClient("marketplace");
        try
        {
            using var cts = CancellationTokenSource
                .CreateLinkedTokenSource(ct);
            cts.CancelAfter(FetchTimeout);
            return await client.GetByteArrayAsync(uri, cts.Token);
        }
        catch (Exception ex) when (ex is not MarketplaceRegistryException
            and not OperationCanceledException)
        {
            throw new MarketplaceRegistryException(
                "registry_unavailable",
                $"remote fetch failed for '{uri.Host}': {ex.Message}");
        }
    }

    /// <summary>Allowlist check — host literal or CIDR.</summary>
    internal void EnsureHostAllowed(Uri uri)
    {
        var allowed = options.Value.AllowedHosts ?? [];
        if (allowed.Length == 0)
        {
            throw new MarketplaceRegistryException(
                "registry_forbidden",
                "remote marketplace fetches disabled (Marketplace:AllowedHosts empty)");
        }

        foreach (var rule in allowed)
        {
            if (rule.Contains('/'))
            {
                if (IPAddress.TryParse(uri.Host, out var ip)
                    && CidrMatches(rule, ip))
                {
                    return;
                }
            }
            else if (string.Equals(rule, uri.Host,
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new MarketplaceRegistryException(
            "registry_forbidden",
            $"host '{uri.Host}' not in Marketplace:AllowedHosts");
    }

    /// <summary>IPv4/IPv6 CIDR containment (e.g. <c>10.0.0.0/8</c>).</summary>
    internal static bool CidrMatches(string cidr, IPAddress ip)
    {
        var slash = cidr.IndexOf('/');
        if (slash <= 0
            || !IPAddress.TryParse(cidr[..slash], out var network)
            || !int.TryParse(cidr[(slash + 1)..], out var bits)
            || network.AddressFamily != ip.AddressFamily)
        {
            return false;
        }

        var netBytes = network.GetAddressBytes();
        var ipBytes = ip.GetAddressBytes();
        if (bits < 0 || bits > netBytes.Length * 8)
        {
            return false;
        }

        var full = bits / 8;
        for (var i = 0; i < full; i++)
        {
            if (netBytes[i] != ipBytes[i])
            {
                return false;
            }
        }

        var rem = bits % 8;
        return rem == 0
            || (netBytes[full] & (0xFF << (8 - rem)))
                == (ipBytes[full] & (0xFF << (8 - rem)));
    }

    // ------------------------------------------------------------------ parse

    /// <summary>
    /// Parses + validates the manifest. Invalid entries are dropped with a
    /// warning — a malformed entry must never break the whole catalog.
    /// </summary>
    internal IReadOnlyList<MarketplaceEntry> Parse(string json)
    {
        RegistryDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<RegistryDocument>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new MarketplaceRegistryException(
                "invalid_manifest", $"registry manifest is not valid JSON: {ex.Message}");
        }

        if (doc?.Entries is null)
        {
            throw new MarketplaceRegistryException(
                "invalid_manifest", "registry manifest has no entries");
        }

        var entries = new List<MarketplaceEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in doc.Entries)
        {
            string? error = null;
            if (e is null || !Validate(e, out error))
            {
                logger.LogWarning(
                    "marketplace entry skipped: {Error}",
                    error ?? "null entry");
                continue;
            }

            if (!seen.Add(e.Id))
            {
                logger.LogWarning(
                    "marketplace duplicate entry id '{Id}' — keeping first",
                    e.Id);
                continue;
            }

            entries.Add(e);
        }

        return entries;
    }

    /// <summary>Manifest validation (id slug, version, kind, files).</summary>
    internal static bool Validate(
        MarketplaceEntry e, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(e.Id) || e.Id.Length > 64
            || !SlugRegex().IsMatch(e.Id))
        {
            error = $"entry id '{e.Id}' is not a valid slug";
            return false;
        }

        if (string.IsNullOrWhiteSpace(e.Name) || e.Name.Length > 128)
        {
            error = $"entry '{e.Id}' missing name";
            return false;
        }

        if (string.IsNullOrWhiteSpace(e.Version) || e.Version.Length > 32)
        {
            error = $"entry '{e.Id}' missing version";
            return false;
        }

        if (e.MinAppVersion is { } v && !SemverRegex().IsMatch(v))
        {
            error = $"entry '{e.Id}' min_app_version '{v}' is not semver";
            return false;
        }

        if (e.Kind == MarketplaceEntryKind.Bundle)
        {
            if (e.EntryIds.Count == 0)
            {
                error = $"bundle '{e.Id}' has no entry_ids";
                return false;
            }

            return true;
        }

        if (e.Files.Count == 0)
        {
            error = $"entry '{e.Id}' has no files";
            return false;
        }

        foreach (var f in e.Files)
        {
            if (string.IsNullOrWhiteSpace(f.Path) || f.Path.Contains("..")
                || f.Path.StartsWith('/') || f.Path.StartsWith('\\'))
            {
                error = $"entry '{e.Id}' file path '{f.Path}' is unsafe";
                return false;
            }

            if (f.Content is null && f.Url is null)
            {
                error = $"entry '{e.Id}' file '{f.Path}' has no content/url";
                return false;
            }

            if (f.Sha256 is { } s && !Sha256Regex().IsMatch(s))
            {
                error = $"entry '{e.Id}' file '{f.Path}' sha256 malformed";
                return false;
            }
        }

        return true;
    }

    /// <summary>SHA-256 hex verification of resolved content.</summary>
    internal static void VerifySha256(
        string content, string? expected, string path)
    {
        if (expected is null)
        {
            return;
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new MarketplaceRegistryException(
                "checksum_mismatch",
                $"sha256 mismatch for '{path}'");
        }
    }

    /// <summary>Hex sha256 of a string (entry checksum support).</summary>
    internal static string Sha256Hex(string content) =>
        Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)))
        .ToLowerInvariant();

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[a-zA-Z0-9.\-]+)?$")]
    private static partial Regex SemverRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Regex();

    /// <summary>Registry document shape: <c>{version, entries[]}</c>.</summary>
    private sealed record RegistryDocument
    {
        public int Version { get; init; }

        public List<MarketplaceEntry>? Entries { get; init; }
    }
}
namespace Sandcastle.Infrastructure.Marketplace;

/// <summary>
/// SPEC-148 RF-001/RF-005: marketplace registry configuration. The default
/// registry is the bundled <c>marketplace.json</c> embedded in the app —
/// installs work fully offline.
/// </summary>
public sealed class MarketplaceOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Marketplace";

    /// <summary>
    /// Remote registry URL (JSON manifest). Must be HTTPS and its host must
    /// match <see cref="AllowedHosts"/>; empty = bundled registry only.
    /// </summary>
    public string? RegistryUrl { get; set; }

    /// <summary>
    /// Local file override (JSON manifest) — takes precedence over the
    /// bundled resource, ignored when <see cref="RegistryUrl"/> is set.
    /// </summary>
    public string? RegistryFile { get; set; }

    /// <summary>
    /// Host/CIDR allowlist for remote fetches (registry + file URLs).
    /// Entries: hostname (exact, case-insensitive) or IPv4 CIDR.
    /// Empty = no remote fetch allowed.
    /// </summary>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>
    /// Root dir for materialized file artifacts. Default
    /// <c>~/.sandcastle/marketplace</c>.
    /// </summary>
    public string? DataDir { get; set; }

    /// <summary>
    /// Effective data dir (option > home default). Used by the
    /// materializer; tests point it at a temp dir.
    /// </summary>
    public string EffectiveDataDir() =>
        !string.IsNullOrWhiteSpace(DataDir)
            ? DataDir!
            : Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                ".sandcastle", "marketplace");
}
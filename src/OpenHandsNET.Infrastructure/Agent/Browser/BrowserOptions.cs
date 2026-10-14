namespace OpenHandsNET.Infrastructure.Agent.Browser;

/// <summary>Browser tool options (SPEC-18 RF-001/RF-004) — <c>Browser:*</c>.</summary>
public sealed class BrowserOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Browser";

    /// <summary>Master switch for the <c>browser</c> tool.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Launch Chromium headless (always true outside local dev).</summary>
    public bool Headless { get; set; } = true;

    /// <summary>Per-navigation timeout.</summary>
    public int NavigationTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// Downloads Chromium via <c>Microsoft.Playwright.Program install</c> on
    /// first use when binaries are missing. Disable in containers that ship
    /// without the browser cache (set <c>Browser__AutoInstall=false</c> and
    /// preinstall in the image instead).
    /// </summary>
    public bool AutoInstall { get; set; } = true;

    /// <summary>Cap for extracted page text returned to the LLM.</summary>
    public int MaxContentChars { get; set; } = 8192;

    /// <summary>Viewport used by every conversation context.</summary>
    public int ViewportWidth { get; set; } = 1280;

    /// <summary>Viewport height.</summary>
    public int ViewportHeight { get; set; } = 800;
}

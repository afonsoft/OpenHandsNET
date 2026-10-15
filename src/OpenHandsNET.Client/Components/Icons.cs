namespace OpenHandsNET.Client.Components;

/// <summary>
/// SPEC-26 RF-002: set de ícones SVG vendored (traços estilo Tabler, MIT).
/// Paths com <c>viewBox="0 0 24 24"</c>, <c>stroke="currentColor"</c>,
/// <c>stroke-width="2"</c>, fill none. Substitui todos os emoji de chrome.
/// </summary>
public static class Icons
{
    /// <summary>nome → inner SVG markup (paths/círculos).</summary>
    public static readonly IReadOnlyDictionary<string, string> Paths =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["chat"] = "<path d='M8 12h.01M12 12h.01M16 12h.01M21 12a9 9 0 0 1-13.2 7.9L3 21l1.1-4.8A9 9 0 1 1 21 12Z'/>",
        ["settings"] = "<path d='M10.3 4.3a1.7 1.7 0 0 1 3.4 0 1.7 1.7 0 0 0 2.5 1 1.7 1.7 0 0 1 2.4 2.4 1.7 1.7 0 0 0 1 2.5 1.7 1.7 0 0 1 0 3.4 1.7 1.7 0 0 0-1 2.5 1.7 1.7 0 0 1-2.4 2.4 1.7 1.7 0 0 0-2.5 1 1.7 1.7 0 0 1-3.4 0 1.7 1.7 0 0 0-2.5-1 1.7 1.7 0 0 1-2.4-2.4 1.7 1.7 0 0 0-1-2.5 1.7 1.7 0 0 1 0-3.4 1.7 1.7 0 0 0 1-2.5 1.7 1.7 0 0 1 2.4-2.4 1.7 1.7 0 0 0 2.5-1Z'/><circle cx='12' cy='12' r='3'/>",
        ["wrench"] = "<path d='M14.7 6.3a4.5 4.5 0 0 0-6 5.9L3 18l3 3 5.8-5.7a4.5 4.5 0 0 0 5.9-6l-3 3-2.4-.6-.6-2.4 3-3Z'/>",
        ["code"] = "<path d='m7 8-4 4 4 4M17 8l4 4-4 4M14 4l-4 16'/>",
        ["terminal"] = "<path d='m4 17 6-6-6-6M12 19h8'/>",
        ["folder"] = "<path d='M4 5h4l3 3h9a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2Z'/>",
        ["file"] = "<path d='M14 3v4a1 1 0 0 0 1 1h4M17 21H7a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h7l5 5v11a2 2 0 0 1-2 2Z'/>",
        ["git-branch"] = "<circle cx='6' cy='6' r='2.5'/><circle cx='6' cy='18' r='2.5'/><circle cx='18' cy='8' r='2.5'/><path d='M6 8.5v7M18 10.5a5 5 0 0 1-5 5H9'/>",
        ["search"] = "<circle cx='10' cy='10' r='7'/><path d='m21 21-6-6'/>",
        ["sun"] = "<circle cx='12' cy='12' r='4'/><path d='M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4'/>",
        ["moon"] = "<path d='M12 3a9 9 0 1 0 9 9 7 7 0 0 1-9-9Z'/>",
        ["monitor"] = "<rect x='3' y='4' width='18' height='12' rx='2'/><path d='M8 20h8M12 16v4'/>",
        ["bell"] = "<path d='M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9M10.3 21a2 2 0 0 0 3.4 0'/>",
        ["x"] = "<path d='M18 6 6 18M6 6l12 12'/>",
        ["check"] = "<path d='m5 12 5 5L20 7'/>",
        ["chevron-down"] = "<path d='m6 9 6 6 6-6'/>",
        ["chevron-left"] = "<path d='m15 18-6-6 6-6'/>",
        ["chevron-right"] = "<path d='m9 18 6-6-6-6'/>",
        ["menu"] = "<path d='M4 6h16M4 12h16M4 18h16'/>",
        ["send"] = "<path d='m22 2-7 20-4-9-9-4Z'/><path d='M22 2 11 13'/>",
        ["stop"] = "<rect x='6' y='6' width='12' height='12' rx='2'/>",
        ["play"] = "<path d='m7 4 13 8-13 8Z'/>",
        ["pause"] = "<rect x='6' y='4' width='4' height='16' rx='1'/><rect x='14' y='4' width='4' height='16' rx='1'/>",
        ["refresh"] = "<path d='M20 11A8 8 0 0 0 5.6 6.6L4 8M4 4v4h4M4 13a8 8 0 0 0 14.4 4.4L20 16M20 20v-4h-4'/>",
        ["alert"] = "<path d='M12 9v4M12 17h.01M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z'/>",
        ["info"] = "<circle cx='12' cy='12' r='9'/><path d='M12 16v-4M12 8h.01'/>",
        ["clock"] = "<circle cx='12' cy='12' r='9'/><path d='M12 7v5l3 3'/>",
        ["user"] = "<circle cx='12' cy='8' r='4'/><path d='M4 21c0-4 3.6-6 8-6s8 2 8 6'/>",
        ["users"] = "<circle cx='9' cy='7' r='4'/><path d='M2 21v-2a4 4 0 0 1 4-4h6a4 4 0 0 1 4 4v2M16 3.1a4 4 0 0 1 0 7.8M22 21v-2a4 4 0 0 0-3-3.9'/>",
        ["key"] = "<circle cx='8' cy='15' r='4.5'/><path d='m11.4 11.6 8.6-8.6M15 8l3 3M18 5l2 2'/>",
        ["shield"] = "<path d='M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10Z'/>",
        ["home"] = "<path d='m3 9 9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z'/><path d='M9 22V12h6v10'/>",
        ["command"] = "<path d='M9 9V6a3 3 0 1 0-3 3h3Zm0 0v6m0-6h6m-6 6H6a3 3 0 1 0 3 3v-3Zm6-6h3a3 3 0 1 0-3-3v3Zm0 0v6m0 0v3a3 3 0 1 0 3-3h-3Z'/>",
        ["plus"] = "<path d='M12 5v14M5 12h14'/>",
        ["filter"] = "<path d='M4 5h16l-6 8v5l-4 2v-7Z'/>",
        ["copy"] = "<rect x='9' y='9' width='13' height='13' rx='2'/><path d='M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1'/>",
        ["download"] = "<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5M12 15V3'/>",
        ["upload"] = "<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M17 8l-5-5-5 5M12 3v12'/>",
        ["external"] = "<path d='M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6M15 3h6v6M10 14 21 3'/>",
        ["dot"] = "<circle cx='12' cy='12' r='5' fill='currentColor' stroke='none'/>",
        ["cpu"] = "<rect x='4' y='4' width='16' height='16' rx='2'/><rect x='9' y='9' width='6' height='6'/><path d='M9 1v3M15 1v3M9 20v3M15 20v3M1 9h3M1 15h3M20 9h3M20 15h3'/>",
        ["book"] = "<path d='M4 19.5A2.5 2.5 0 0 1 6.5 17H20V2H6.5A2.5 2.5 0 0 0 4 4.5Z'/><path d='M4 19.5A2.5 2.5 0 0 0 6.5 22H20v-5'/>",
        ["zap"] = "<path d='M13 2 3 14h9l-1 8 10-12h-9l1-8Z'/>",
        ["eye"] = "<path d='M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z'/><circle cx='12' cy='12' r='3'/>",
        ["logout"] = "<path d='M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9'/>",
        ["spin"] = "<path d='M21 12a9 9 0 1 1-9-9'/>",
    };

    /// <summary>Inner markup do ícone ou fallback <c>info</c>.</summary>
    public static string Get(string? name) =>
        name is not null && Paths.TryGetValue(name, out var p)
            ? p : Paths["info"];
}

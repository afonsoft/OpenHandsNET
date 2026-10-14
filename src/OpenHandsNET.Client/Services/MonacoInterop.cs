using Microsoft.JSInterop;

namespace OpenHandsNET.Client.Services;

/// <summary>
/// SPEC-21 RF-004: wrapper tipado do <c>monacoInterop.js</c> (vendored em
/// <c>lib/monaco/min/vs</c>). Toda falha de interop propaga
/// <see cref="JSException"/> — o chamador decide o fallback para
/// <c>&lt;pre&gt;</c>.
/// </summary>
public sealed class MonacoInterop(IJSRuntime js)
{
    /// <summary>Cria um editor readonly no host.</summary>
    public async Task<bool> CreateEditorAsync(
        string hostId, string value, string? language, bool readOnly = true,
        CancellationToken ct = default) =>
        await js.InvokeAsync<bool>("monacoInterop.createEditor", ct,
            hostId, value, language ?? "plaintext", readOnly);

    /// <summary>Cria um diff editor (original HEAD × modified workspace).</summary>
    public async Task<bool> CreateDiffEditorAsync(
        string hostId, string original, string modified, string? language,
        CancellationToken ct = default) =>
        await js.InvokeAsync<bool>("monacoInterop.createDiffEditor", ct,
            hostId, original, modified, language ?? "plaintext");

    /// <summary>Troca o conteúdo do editor simples.</summary>
    public async Task SetValueAsync(
        string hostId, string value, string? language,
        CancellationToken ct = default) =>
        await js.InvokeVoidAsync("monacoInterop.setValue", ct,
            hostId, value, language ?? "plaintext");

    /// <summary>Troca os dois lados do diff editor.</summary>
    public async Task SetDiffAsync(
        string hostId, string original, string modified, string? language,
        CancellationToken ct = default) =>
        await js.InvokeVoidAsync("monacoInterop.setDiff", ct,
            hostId, original, modified, language ?? "plaintext");

    /// <summary>Libera editor + modelos.</summary>
    public async Task DisposeAsync(string hostId, CancellationToken ct = default) =>
        await js.InvokeVoidAsync("monacoInterop.dispose", ct, hostId);

    /// <summary>Mapa extensão → linguagem Monaco.</summary>
    public static string LanguageFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".razor" or ".cshtml" => "razor",
            ".js" or ".mjs" or ".cjs" => "javascript",
            ".ts" or ".tsx" => "typescript",
            ".jsx" => "javascript",
            ".json" => "json",
            ".md" => "markdown",
            ".py" => "python",
            ".yml" or ".yaml" => "yaml",
            ".xml" or ".csproj" or ".props" or ".targets" => "xml",
            ".html" or ".htm" => "html",
            ".css" => "css",
            ".scss" => "scss",
            ".sh" or ".bash" => "shell",
            ".ps1" => "powershell",
            ".sql" => "sql",
            ".go" => "go",
            ".rs" => "rust",
            ".java" => "java",
            ".kt" => "kotlin",
            ".rb" => "ruby",
            ".php" => "php",
            ".c" or ".h" => "c",
            ".cpp" or ".hpp" or ".cc" => "cpp",
            ".dockerfile" or ".dockerignore" => "dockerfile",
            ".toml" => "ini",
            ".ini" => "ini",
            ".gitignore" or ".env" => "ini",
            ".txt" or ".log" => "plaintext",
            _ => path.EndsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)
                ? "dockerfile" : "plaintext",
        };
}

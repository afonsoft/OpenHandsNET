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
    private const string Plaintext = "plaintext";
    /// <summary>Cria um editor readonly no host.</summary>
    public async Task<bool> CreateEditorAsync(
        string hostId, string value, string? language, bool readOnly = true,
        CancellationToken ct = default) =>
        await js.InvokeAsync<bool>("monacoInterop.createEditor", ct,
            hostId, value, language ?? Plaintext, readOnly);

    /// <summary>Cria um diff editor (original HEAD × modified workspace).</summary>
    public async Task<bool> CreateDiffEditorAsync(
        string hostId, string original, string modified, string? language,
        CancellationToken ct = default) =>
        await js.InvokeAsync<bool>("monacoInterop.createDiffEditor", ct,
            hostId, original, modified, language ?? Plaintext);

    /// <summary>Troca o conteúdo do editor simples.</summary>
    public async Task SetValueAsync(
        string hostId, string value, string? language,
        CancellationToken ct = default) =>
        await js.InvokeVoidAsync("monacoInterop.setValue", ct,
            hostId, value, language ?? Plaintext);

    /// <summary>Troca os dois lados do diff editor.</summary>
    public async Task SetDiffAsync(
        string hostId, string original, string modified, string? language,
        CancellationToken ct = default) =>
        await js.InvokeVoidAsync("monacoInterop.setDiff", ct,
            hostId, original, modified, language ?? Plaintext);

    /// <summary>Libera editor + modelos.</summary>
    public async Task DisposeAsync(string hostId, CancellationToken ct = default) =>
        await js.InvokeVoidAsync("monacoInterop.dispose", ct, hostId);

    // ---- SPEC-42 RF-004: workbench editável -----------------------------

    /// <summary>Conteúdo atual do modelo do editor.</summary>
    public async Task<string> GetValueAsync(
        string hostId, CancellationToken ct = default) =>
        await js.InvokeAsync<string>("monacoInterop.getValue", ct, hostId);

    /// <summary>Callback DotNet a cada mudança de conteúdo (dirty flag).</summary>
    public async Task OnChangedAsync<T>(
        string hostId, DotNetObjectReference<T> self, string method,
        CancellationToken ct = default) where T : class =>
        await js.InvokeVoidAsync(
            "monacoInterop.onChanged", ct, hostId, self, method);

    /// <summary>Binding Ctrl+S → callback DotNet de save.</summary>
    public async Task OnSaveAsync<T>(
        string hostId, DotNetObjectReference<T> self, string method,
        CancellationToken ct = default) where T : class =>
        await js.InvokeVoidAsync(
            "monacoInterop.onSave", ct, hostId, self, method);

    /// <summary>Posição do cursor (line, col) → status bar.</summary>
    public async Task OnCursorAsync<T>(
        string hostId, DotNetObjectReference<T> self, string method,
        CancellationToken ct = default) where T : class =>
        await js.InvokeVoidAsync(
            "monacoInterop.onCursor", ct, hostId, self, method);

    /// <summary>Abre/revela uma linha no editor (jump de busca/SCM).</summary>
    public async Task RevealLineAsync(
        string hostId, int line, int col = 1, CancellationToken ct = default) =>
        await js.InvokeVoidAsync(
            "monacoInterop.revealLine", ct, hostId, line, col);

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
            ".txt" or ".log" => Plaintext,
            _ => path.EndsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)
                ? "dockerfile" : Plaintext,
        };
}
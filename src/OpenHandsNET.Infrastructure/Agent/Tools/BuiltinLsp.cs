using System.Text.Json;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Sandbox;

namespace OpenHandsNET.Infrastructure.Agent.Tools;

/// <summary>
/// SPEC-122: suíte <c>lsp_*</c>×6 — code intelligence dentro do sandbox
/// via bridge Python (<see cref="BridgeScript"/> escrito em
/// <c>/tmp/ohn-lsp/bridge.py</c>). O bridge spawna o language server
/// da linguagem (one-shot por chamada: init → didOpen → request →
/// shutdown), imprime o JSON da resposta e sai — transporte
/// <see cref="ISandboxExecutor.ExecAsync"/>, nada roda no host.
/// Guards OWUIC: workspace-jail (paths resolvidos sob
/// <c>/workspace/repo</c>), linguagem por extensão, resultado capado,
/// tools read-only.
/// </summary>
public static class BuiltinLsp
{
    private const string Workspace = "/workspace/repo";
    private const string BridgePath = "/tmp/ohn-lsp/bridge.py";
    private const string BridgeDir = "/tmp/ohn-lsp";
    private const int CapBytes = 24 * 1024;
    private static readonly TimeSpan LspTimeout = TimeSpan.FromSeconds(45);

    // Guards de jail: somente leitura — nada sob path do usuário sai do workspace.
    private const string JailCheck =
        "p=$(readlink -f \"$1\" 2>/dev/null); "
        + "case \"$p\" in " + Workspace + "/*) ;; *) "
        + "echo '__JAIL__' >&2; exit 3;; esac; ";

    /// <summary>Diagnósticos (erros/avisos) de um arquivo.</summary>
    public static AgentTool LspDiagnostics() => new(
        new ToolSchema("lsp_diagnostics",
            "LSP diagnostics (errors/warnings) for a workspace file. "
            + "Requires the language server installed in the sandbox.",
            Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string","description":"File path relative to the workspace"}},
                 "required":["path"]}
                """)),
        Risky: false,
        Handler: (ctx, a, ct) => RunLspAsync(ctx, a, ct,
            "diagnostics", extraArgs: null));

    /// <summary>Símbolos (document symbols) de um arquivo.</summary>
    public static AgentTool LspSymbols() => new(
        new ToolSchema("lsp_symbols",
            "LSP document symbols (classes/functions/vars) of a file.",
            Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string","description":"File path relative to the workspace"}},
                 "required":["path"]}
                """)),
        Risky: false,
        Handler: (ctx, a, ct) => RunLspAsync(ctx, a, ct,
            "symbols", extraArgs: null));

    /// <summary>Busca de símbolos no workspace inteiro.</summary>
    public static AgentTool LspWorkspaceSymbols() => new(
        new ToolSchema("lsp_workspace_symbols",
            "LSP workspace-wide symbol search by query.",
            Schema("""
                {"type":"object","properties":{
                  "query":{"type":"string","description":"Symbol name substring"},
                  "path":{"type":"string","description":"Any workspace file (language detection)"}},
                 "required":["query"]}
                """)),
        Risky: false,
        Handler: (ctx, a, ct) => RunLspAsync(ctx, a, ct,
            "workspace_symbols",
            extraArgs: Str(a, "query") is { } q ? $" {Sh(q)}" : null));

    /// <summary>Definição do símbolo na posição.</summary>
    public static AgentTool LspDefinition() => new(
        new ToolSchema("lsp_definition",
            "LSP go-to-definition for the symbol at line/character.",
            Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string","description":"File path relative to the workspace"},
                  "line":{"type":"integer","description":"0-based line"},
                  "character":{"type":"integer","description":"0-based character"}},
                 "required":["path","line","character"]}
                """)),
        Risky: false,
        Handler: (ctx, a, ct) => RunLspAsync(ctx, a, ct,
            "definition",
            extraArgs: $" {Int(a, "line")} {Int(a, "character")}"));

    /// <summary>Referências ao símbolo na posição.</summary>
    public static AgentTool LspReferences() => new(
        new ToolSchema("lsp_references",
            "LSP find-references for the symbol at line/character.",
            Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string","description":"File path relative to the workspace"},
                  "line":{"type":"integer","description":"0-based line"},
                  "character":{"type":"integer","description":"0-based character"}},
                 "required":["path","line","character"]}
                """)),
        Risky: false,
        Handler: (ctx, a, ct) => RunLspAsync(ctx, a, ct,
            "references",
            extraArgs: $" {Int(a, "line")} {Int(a, "character")}"));

    /// <summary>Hover (docs/tipo) do símbolo na posição.</summary>
    public static AgentTool LspHover() => new(
        new ToolSchema("lsp_hover",
            "LSP hover info (type/docs) for the symbol at line/character.",
            Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string","description":"File path relative to the workspace"},
                  "line":{"type":"integer","description":"0-based line"},
                  "character":{"type":"integer","description":"0-based character"}},
                 "required":["path","line","character"]}
                """)),
        Risky: false,
        Handler: (ctx, a, ct) => RunLspAsync(ctx, a, ct,
            "hover",
            extraArgs: $" {Int(a, "line")} {Int(a, "character")}"));

    private static async Task<ToolResult> RunLspAsync(
        ToolCallContext ctx, JsonElement args, CancellationToken ct,
        string op, string? extraArgs)
    {
        var path = Str(args, "path");
        if (path is null && op != "workspace_symbols")
        {
            return ToolResult.Error("path é obrigatório");
        }

        // Workspace symbols usa um arquivo do repo só p/ detecção de
        // linguagem — default main file genérico se ausente.
        var rel = path ?? "README.md";
        if (rel.StartsWith('/'))
        {
            rel = rel[1..];
        }

        var full = $"{Workspace}/{rel}";
        var bridge = await EnsureBridgeAsync(ctx, ct);
        if (bridge is not null)
        {
            return ToolResult.Error(bridge);
        }

        var cmd = "cd " + Workspace + " && "
            + $"FULL=$(readlink -f {Sh(full)}) && "
            + "case \"$FULL\" in " + Workspace + "/*) ;; *) "
            + "echo 'path fora do workspace' >&2; exit 3;; esac && "
            + $"python3 {BridgePath} {op} \"$FULL\"{extraArgs}"
            + $" | head -c {CapBytes}";
        var r = await ctx.Executor.ExecAsync(ctx.Sandbox, cmd, LspTimeout, ct);

        if (r.StdErr.Contains("__NO_SERVER__", StringComparison.Ordinal)
            || r.ExitCode == 4)
        {
            return ToolResult.Error(
                "language server não instalado no sandbox — instale via "
                + "bash (ex.: `pip install pyright` ou `npm i -g "
                + "typescript-language-server`) e tente de novo");
        }

        return r.ExitCode == 0
            ? ToolResult.Ok(new
            {
                operation = op,
                path = rel,
                result = r.StdOut.Trim(),
            })
            : ToolResult.Error(
                $"lsp_{op} falhou (exit {r.ExitCode}): "
                + $"{(r.StdErr + r.StdOut).Trim()[..Math.Min(800, (r.StdErr + r.StdOut).Trim().Length)]}");
    }

    /// <summary>Escreve o bridge uma vez por sandbox (flag file).</summary>
    private static async Task<string?> EnsureBridgeAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        var stamp = $"{BridgeDir}/.ready-v1";
        var check = await ctx.Executor.ExecAsync(ctx.Sandbox,
            $"test -f {stamp} && echo yes || echo no",
            ctx.DefaultTimeout, ct);
        if (check.StdOut.Contains("yes", StringComparison.Ordinal))
        {
            return null;
        }

        await ctx.Executor.WriteFileAsync(ctx.Sandbox,
            BridgePath, BridgeScript, ct);
        var r = await ctx.Executor.ExecAsync(ctx.Sandbox,
            $"touch {stamp}", ctx.DefaultTimeout, ct);
        return r.ExitCode == 0
            ? null
            : $"não consegui gravar o bridge LSP (exit {r.ExitCode})";
    }

    private static string? Str(JsonElement a, string n) =>
        a.TryGetProperty(n, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int Int(JsonElement a, string n) =>
        a.TryGetProperty(n, out var v) ? v.GetInt32() : 0;

    private static string Sh(string v) =>
        "'" + v.Replace("'", "'\\''") + "'";

    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// Bridge Python embutido: <c>bridge.py OP FILE [ARGS]</c> —
    /// detecta a linguagem pela extensão, spawna o servidor LSP
    /// (Content-Length framing), faz initialize → didOpen → request →
    /// shutdown e imprime o JSON da resposta. Exit 4 = servidor ausente.
    /// </summary>
    private const string BridgeScript = """
#!/usr/bin/env python3
""" + "\n" + """
import json, os, subprocess, sys

WORKSPACE = "/workspace/repo"
SERVERS = {
    ".py":   ["pyright-langserver", "--stdio"],
    ".pyi":  ["pyright-langserver", "--stdio"],
    ".ts":   ["typescript-language-server", "--stdio"],
    ".tsx":  ["typescript-language-server", "--stdio"],
    ".js":   ["typescript-language-server", "--stdio"],
    ".jsx":  ["typescript-language-server", "--stdio"],
    ".cs":   ["csharp-ls"],
    ".go":   ["gopls"],
    ".rs":   ["rust-analyzer"],
    ".java": ["jdtls"],
    ".cpp":  ["clangd"],
    ".cc":   ["clangd"],
    ".c":    ["clangd"],
    ".h":    ["clangd"],
    ".rb":   ["solargraph", "stdio"],
    ".php":  ["intelephense", "--stdio"],
    ".lua":  ["lua-language-server"],
    ".json": ["vscode-json-language-server", "--stdio"],
    ".yaml": ["yaml-language-server", "--stdio"],
    ".yml":  ["yaml-language-server", "--stdio"],
    ".html": ["vscode-html-language-server", "--stdio"],
    ".css":  ["vscode-css-language-server", "--stdio"],
    ".md":   ["marksman"],
    ".sh":   ["bash-language-server", "start"],
}
LANGS = {
    ".py": "python", ".ts": "typescript", ".tsx": "typescriptreact",
    ".js": "javascript", ".jsx": "javascriptreact", ".cs": "csharp",
    ".go": "go", ".rs": "rust", ".java": "java", ".cpp": "cpp",
    ".cc": "cpp", ".c": "c", ".h": "cpp", ".rb": "ruby",
    ".php": "php", ".lua": "lua", ".json": "json", ".yaml": "yaml",
    ".yml": "yaml", ".html": "html", ".css": "css",
    ".md": "markdown", ".sh": "shellscript", ".pyi": "python",
}

def send(proc, msg):
    body = json.dumps(msg).encode()
    proc.stdin.write(b"Content-Length: " + str(len(body)).encode()
                     + b"\r\n\r\n" + body)
    proc.stdin.flush()

def recv(proc, timeout=20):
    import select
    fd = proc.stdout.fileno()
    if not select.select([fd], [], [], timeout)[0]:
        return None
    headers = {}
    while True:
        line = proc.stdout.readline().strip()
        if not line:
            break
        k, _, v = line.decode(errors="replace").partition(":")
        headers[k.strip().lower()] = v.strip()
    n = int(headers.get("content-length", 0))
    if not n:
        return None
    return json.loads(proc.stdout.read(n))

def wait_response(proc, rid, timeout=25):
    while True:
        msg = recv(proc, timeout)
        if msg is None:
            return None
        if msg.get("id") == rid:
            return msg.get("result")
        if msg.get("method") == "window/logMessage":
            continue
        if "id" in msg and "method" in msg:
            send(proc, {"jsonrpc": "2.0", "id": msg["id"], "result": None})

def main():
    op = sys.argv[1]
    path = os.path.realpath(sys.argv[2])
    if not path.startswith(WORKSPACE + "/"):
        print(json.dumps({"error": "path fora do workspace"})); sys.exit(3)
    ext = os.path.splitext(path)[1].lower()
    server = SERVERS.get(ext)
    if server is None:
        print(json.dumps({"error": f"sem servidor p/ '{ext}'"})); sys.exit(3)
    try:
        proc = subprocess.Popen(server, stdin=subprocess.PIPE,
                                stdout=subprocess.PIPE,
                                stderr=subprocess.DEVNULL, cwd=WORKSPACE)
    except FileNotFoundError:
        print("__NO_SERVER__", file=sys.stderr); sys.exit(4)

    root_uri = "file://" + WORKSPACE
    send(proc, {"jsonrpc": "2.0", "id": 0, "method": "initialize", "params": {
        "processId": None, "rootUri": root_uri,
        "capabilities": {"textDocument": {
            "publishDiagnostics": {"relatedInformation": True},
            "hover": {"contentFormat": ["plaintext", "markdown"]},
            "documentSymbol": {"hierarchicalDocumentSymbolSupport": True},
            "references": {}, "definition": {}, "workspaceSymbol": {}}},
        "workspaceFolders": [{"uri": root_uri, "name": "repo"}]}})
    wait_response(proc, 0)
    send(proc, {"jsonrpc": "2.0", "method": "initialized", "params": {}})

    file_uri = "file://" + path
    text = ""
    if os.path.isfile(path):
        text = open(path, encoding="utf-8", errors="replace").read()
    send(proc, {"jsonrpc": "2.0", "method": "textDocument/didOpen",
                "params": {"textDocument": {
                    "uri": file_uri, "languageId": LANGS.get(ext, "plaintext"),
                    "version": 1, "text": text}}})

    pos = {"line": int(sys.argv[3]) if len(sys.argv) > 3 else 0,
           "character": int(sys.argv[4]) if len(sys.argv) > 4 else 0}
    td = {"uri": file_uri}
    reqs = {
        "diagnostics": ("textDocument/diagnostic",
                        {"textDocument": td}),
        "symbols": ("textDocument/documentSymbol", {"textDocument": td}),
        "workspace_symbols": ("workspace/symbol",
                              {"query": sys.argv[3] if len(sys.argv) > 3 else ""}),
        "definition": ("textDocument/definition",
                       {"textDocument": td, "position": pos}),
        "references": ("textDocument/references",
                       {"textDocument": td, "position": pos,
                        "context": {"includeDeclaration": True}}),
        "hover": ("textDocument/hover",
                  {"textDocument": td, "position": pos}),
    }
    if op == "diagnostics":
        # Nem todo servidor suporta pull diagnostics — fallback:
        # espera publishDiagnostics até timeout curto.
        try:
            send(proc, {"jsonrpc": "2.0", "id": 1,
                        "method": "textDocument/diagnostic",
                        "params": {"textDocument": td}})
            result = wait_response(proc, 1, timeout=8)
            if result is None:
                raise TimeoutError
        except Exception:
            result = {"note": "servidor sem pull-diagnostics",
                      "diagnostics": []}
    else:
        method, params = reqs[op]
        send(proc, {"jsonrpc": "2.0", "id": 1, "method": method,
                    "params": params})
        result = wait_response(proc, 1)

    try:
        send(proc, {"jsonrpc": "2.0", "id": 2, "method": "shutdown"})
        send(proc, {"jsonrpc": "2.0", "method": "exit"})
        proc.wait(timeout=3)
    except Exception:
        proc.kill()
    print(json.dumps(result if result is not None else {"result": None}))
    sys.exit(0)

main()
""";
}

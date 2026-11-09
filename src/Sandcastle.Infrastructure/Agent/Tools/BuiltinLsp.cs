using System.Text.Json;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Sandbox;

namespace Sandcastle.Infrastructure.Agent.Tools;

/// <summary>
/// SPEC-122: suíte <c>lsp_*</c>×6 — code intelligence dentro do sandbox
/// via bridge Python (<see cref="BridgeScript"/> escrito em
/// <c>/home/openhands/.local/state/sc/lsp/bridge.py</c>). O bridge spawna o language server
/// da linguagem (one-shot por chamada: init → didOpen → request →
/// shutdown), imprime o JSON da resposta e sai — transporte
/// <see cref="ISandboxExecutor.ExecAsync"/>, nada roda no host.
/// Guards OWUIC: workspace-jail (paths resolvidos sob
/// <c>/workspace/repo</c>), linguagem por extensão, resultado capado,
/// tools read-only.
/// </summary>
public static class BuiltinLsp
{
    private const string Character = "character";

    private const string Workspace = "/workspace/repo";
    private const string BridgePath = "/home/openhands/.local/state/sc/lsp/bridge.py";
    private const string CallPath = "/home/openhands/.local/state/sc/lsp/call.py";
    private const string BridgeDir = "/home/openhands/.local/state/sc/lsp";
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
            extraArgs: $" {Int(a, "line")} {Int(a, Character)}"));

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
            extraArgs: $" {Int(a, "line")} {Int(a, Character)}"));

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
            extraArgs: $" {Int(a, "line")} {Int(a, Character)}"));

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

        var jail = "cd " + Workspace + " && "
            + $"FULL=$(readlink -f {Sh(full)}) && "
            + "case \"$FULL\" in " + Workspace + "/*) ;; *) "
            + "echo 'path fora do workspace' >&2; exit 3;; esac && ";

        // SPEC-126 RF-001/002: caminho primário — daemon residente
        // (Unix socket, índice quente). Falha → degrada p/ one-shot.
        var req = BuildRequest(op, full, args, extraArgs);
        var r = await ctx.Executor.ExecAsync(ctx.Sandbox,
            jail + $"python3 {CallPath} {Sh(req)} | head -c {CapBytes}",
            LspTimeout, ct);

        if (r.ExitCode == 5
            || r.StdErr.Contains("__NO_DAEMON__", StringComparison.Ordinal))
        {
            await StartDaemonAsync(ctx, ct);
            r = await ctx.Executor.ExecAsync(ctx.Sandbox,
                jail + $"python3 {CallPath} {Sh(req)} | head -c {CapBytes}",
                LspTimeout, ct);
        }

        if (r.ExitCode == 5
            || r.StdErr.Contains("__NO_DAEMON__", StringComparison.Ordinal))
        {
            // RF-003: fallback transparente ao modo one-shot da SPEC-122.
            r = await ctx.Executor.ExecAsync(ctx.Sandbox,
                jail + $"python3 {BridgePath} {op} \"$FULL\"{extraArgs}"
                + $" | head -c {CapBytes}",
                LspTimeout, ct);
        }

        if (r.StdErr.Contains("__NO_SERVER__", StringComparison.Ordinal)
            || r.ExitCode == 4
            || r.StdOut.Contains("\"__no_server__\"", StringComparison.Ordinal))
        {
            return ToolResult.Error(
                "language server não instalado no sandbox — instale via "
                + "bash (ex.: `pip install pyright` ou `npm i -g "
                + "typescript-language-server`) e tente de novo");
        }

        var daemonError = TryDaemonError(r.StdOut);
        if (daemonError is not null)
        {
            return ToolResult.Error(
                daemonError.Contains("__no_server__", StringComparison.Ordinal)
                ? "language server não instalado no sandbox — instale via "
                + "bash (ex.: `pip install pyright` ou `npm i -g "
                + "typescript-language-server`) e tente de novo"
                : daemonError);
        }

        return r.ExitCode == 0
            ? ToolResult.Ok(new
            {
                operation = op,
                path = rel,
                result = r.StdOut.Trim(),
                persistent = r.StdOut.Contains(
                    "\"bridge\":\"daemon\"", StringComparison.Ordinal),
            })
            : ToolResult.Error(
                $"lsp_{op} falhou (exit {r.ExitCode}): "
                + $"{(r.StdErr + r.StdOut).Trim()[..Math.Min(800, (r.StdErr + r.StdOut).Trim().Length)]}");
    }

    /// <summary>Request JSON do daemon: op + path + parâmetros nomeados.</summary>
    private static string BuildRequest(
        string op, string full, JsonElement args, string? extraArgs)
    {
        var req = new Dictionary<string, object?>
        {
            ["op"] = op,
            ["path"] = full,
        };
        if (extraArgs is not null && Str(args, "query") is { } q)
        {
            req["query"] = q;
        }
        if (extraArgs is not null
            && args.TryGetProperty("line", out var ln))
        {
            req["line"] = ln.GetInt32();
            req[Character] = args.TryGetProperty(Character, out var ch)
                ? ch.GetInt32() : 0;
        }
        return JsonSerializer.Serialize(req);
    }

    /// <summary>Extrai <c>{"error": ...}</c> da resposta do daemon.</summary>
    private static string? TryDaemonError(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout.Trim());
            if (doc.RootElement.TryGetProperty("error", out var e)
                && e.ValueKind == JsonValueKind.String)
            {
                return e.GetString();
            }
        }
        catch (JsonException)
        {
            // stdout não-JSON (daemon morto, traceback) → reporta genérico.
        }
        return null;
    }

    /// <summary>Sobe o daemon serve em background (nohup + socket).</summary>
    private static async Task StartDaemonAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        await ctx.Executor.ExecAsync(ctx.Sandbox,
            $"rm -f {BridgeDir}/bridge.sock && "
            + $"nohup python3 {BridgePath} serve "
            + $">{BridgeDir}/serve.log 2>&1 & sleep 1",
            ctx.DefaultTimeout, ct);
    }

    /// <summary>Escreve bridge + client uma vez por sandbox (flag file,
    /// versionada — v2 = daemon serve, SPEC-126).</summary>
    private static async Task<string?> EnsureBridgeAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        var stamp = $"{BridgeDir}/.ready-v2";
        var check = await ctx.Executor.ExecAsync(ctx.Sandbox,
            $"test -f {stamp} && echo yes || echo no",
            ctx.DefaultTimeout, ct);
        if (check.StdOut.Contains("yes", StringComparison.Ordinal))
        {
            return null;
        }

        await ctx.Executor.WriteFileAsync(ctx.Sandbox,
            BridgePath, BridgeScript, ct);
        await ctx.Executor.WriteFileAsync(ctx.Sandbox,
            CallPath, CallScript, ct);
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
    result = run_op(proc, file_uri, op, pos,
                    sys.argv[3] if op == "workspace_symbols" and len(sys.argv) > 3 else "")
    try:
        send(proc, {"jsonrpc": "2.0", "id": 2, "method": "shutdown"})
        send(proc, {"jsonrpc": "2.0", "method": "exit"})
        proc.wait(timeout=3)
    except Exception:
        proc.kill()
    print(json.dumps(result if result is not None else {"result": None}))
    sys.exit(0)

def run_op(proc, file_uri, op, pos, query):
    td = {"uri": file_uri}
    reqs = {
        "symbols": ("textDocument/documentSymbol", {"textDocument": td}),
        "workspace_symbols": ("workspace/symbol", {"query": query}),
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
        return result
    method, params = reqs[op]
    send(proc, {"jsonrpc": "2.0", "id": 1, "method": method,
                "params": params})
    return wait_response(proc, 1)

# ---- SPEC-126: modo serve — daemon residente por sandbox ----
# Unix socket /home/openhands/.local/state/sc/lsp/bridge.sock; uma conexão por request
# (serialização natural). Servidores LSP ficam quentes por linguagem;
# arquivos já abertos recebem didChange (versão incremental).
# Idle > IDLE_TTL_S → encerra (o próximo call reinicia sob demanda).

IDLE_TTL_S = 600
SOCK = "/home/openhands/.local/state/sc/lsp/bridge.sock"
_servers = {}   # ext -> Popen
_opened = {}    # ext -> {uri: version}
_reqid = [1]

def _init_server(ext):
    cmd = SERVERS.get(ext)
    if cmd is None:
        return None
    try:
        proc = subprocess.Popen(cmd, stdin=subprocess.PIPE,
                                stdout=subprocess.PIPE,
                                stderr=subprocess.DEVNULL, cwd=WORKSPACE)
    except FileNotFoundError:
        return "NO_SERVER"
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
    _servers[ext] = proc
    _opened[ext] = {}
    return proc

def _shutdown_all():
    for proc in _servers.values():
        try:
            send(proc, {"jsonrpc": "2.0", "id": 99, "method": "shutdown"})
            send(proc, {"jsonrpc": "2.0", "method": "exit"})
            proc.wait(timeout=2)
        except Exception:
            proc.kill()

def _ensure_open(proc, ext, path):
    uri = "file://" + path
    text = ""
    if os.path.isfile(path):
        text = open(path, encoding="utf-8", errors="replace").read()
    ver = _opened[ext].get(uri, 0) + 1
    if ver == 1:
        send(proc, {"jsonrpc": "2.0", "method": "textDocument/didOpen",
                    "params": {"textDocument": {
                        "uri": uri, "languageId": LANGS.get(ext, "plaintext"),
                        "version": 1, "text": text}}})
    else:
        send(proc, {"jsonrpc": "2.0", "method": "textDocument/didChange",
                    "params": {"textDocument": {"uri": uri, "version": ver},
                               "contentChanges": [{"text": text}]}})
    _opened[ext][uri] = ver

def _handle(req):
    op = req.get("op")
    if op == "ping":
        return {"pong": True, "servers": list(_servers.keys())}
    if op == "shutdown":
        _shutdown_all()
        return {"bye": True}
    path = os.path.realpath(req.get("path", ""))
    if not path.startswith(WORKSPACE + "/"):
        return {"error": "path fora do workspace"}
    ext = os.path.splitext(path)[1].lower()
    proc = _servers.get(ext)
    if proc is None or proc.poll() is not None:
        _servers.pop(ext, None)
        proc = _init_server(ext)
    if proc is None:
        return {"error": f"sem servidor p/ '{ext}'"}
    if proc == "NO_SERVER":
        return {"error": "__no_server__"}
    _ensure_open(proc, ext, path)
    pos = {"line": int(req.get("line", 0)),
           "character": int(req.get("character", 0))}
    _reqid[0] += 1
    result = run_op(proc, "file://" + path, op, pos,
                    req.get("query", ""))
    return {"bridge": "daemon", "result": result}

def serve():
    import socket as _s
    import time as _t
    if os.path.exists(SOCK):
        os.unlink(SOCK)
    srv = _s.socket(_s.AF_UNIX, _s.SOCK_STREAM)
    srv.bind(SOCK)
    srv.listen(2)
    srv.settimeout(30)
    last = _t.time()
    while True:
        try:
            conn, _ = srv.accept()
        except _s.timeout:
            if _t.time() - last > IDLE_TTL_S:
                break
            continue
        last = _t.time()
        try:
            line = conn.makefile().readline()
            req = json.loads(line) if line.strip() else {}
            resp = _handle(req)
        except Exception as e:
            resp = {"error": str(e)}
        try:
            conn.sendall((json.dumps(resp) + "\n").encode())
            conn.close()
        except Exception:
            pass
    _shutdown_all()
    try:
        os.unlink(SOCK)
    except OSError:
        pass

if len(sys.argv) > 1 and sys.argv[1] == "serve":
    serve()
    sys.exit(0)

main()
""";

    /// <summary>
    /// Cliente do daemon (SPEC-126): <c>call.py '&lt;json&gt;'</c> —
    /// conecta no socket, manda 1 linha, imprime a resposta. Exit 5 +
    /// <c>__NO_DAEMON__</c> no stderr quando o daemon está fora → o
    /// chamador reinicia ou degrada p/ one-shot.
    /// </summary>
    private const string CallScript = """
#!/usr/bin/env python3
import socket, sys
try:
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    s.settimeout(45)
    s.connect("/home/openhands/.local/state/sc/lsp/bridge.sock")
    s.sendall(sys.argv[1].encode() + b"\n")
    s.shutdown(socket.SHUT_WR)
    out = b""
    while True:
        chunk = s.recv(65536)
        if not chunk:
            break
        out += chunk
    sys.stdout.write(out.decode(errors="replace"))
except OSError:
    print("__NO_DAEMON__", file=sys.stderr)
    sys.exit(5)
""";
}
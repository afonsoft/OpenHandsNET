using System.Text.Json;

using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Tools;

/// <summary>
/// SPEC-121 (port OWUIC <c>notebook_read/notebook_edit</c>): leitura e
/// edição estruturada de <c>.ipynb</c> no workspace — read devolve
/// cells (index/type/source preview); edit faz insert/replace/delete
/// de célula via python3 no sandbox (JSON seguro, sem python no host).
/// </summary>
public static class BuiltinNotebook
{
    private const string Workspace = "/workspace/repo";
    private const int PreviewChars = 300;
    private const int CapCells = 200;

    /// <summary>Resumo das células de um notebook.</summary>
    public static AgentTool NotebookRead() => new(
        new ToolSchema("notebook_read",
            "Read a Jupyter notebook: cell index, type and source "
            + "preview (markdown/code).",
            Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string","description":"Notebook path relative to the workspace"}},
                 "required":["path"]}
                """)),
        Risky: false,
        Handler: async (ctx, a, ct) =>
        {
            var rel = RelPath(a);
            var r = await ctx.Executor.ExecAsync(ctx.Sandbox,
                $"cd {Workspace} && python3 - <<'PYEOF'\n"
                + PyRead + $"\nPYEOF\n{Sh(rel)}",
                ctx.DefaultTimeout, ct);
            return r.ExitCode == 0
                ? ToolResult.Ok(new
                {
                    path = rel,
                    cells = JsonDocument.Parse(r.StdOut.Trim()).RootElement.Clone(),
                })
                : ToolResult.Error($"notebook_read falhou: {r.StdErr.Trim()}");
        });

    /// <summary>Insere/substitui/remove célula de um notebook.</summary>
    public static AgentTool NotebookEdit() => new(
        new ToolSchema("notebook_edit",
            "Edit a notebook cell: op=insert|replace|delete. "
            + "insert/replace need cell_type (code|markdown) + source.",
            Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string","description":"Notebook path relative to the workspace"},
                  "op":{"type":"string","enum":["insert","replace","delete"]},
                  "index":{"type":"integer","description":"Cell index (insert = position, replace/delete = target)"},
                  "cell_type":{"type":"string","enum":["code","markdown"]},
                  "source":{"type":"string","description":"Cell source text"}},
                 "required":["path","op","index"]}
                """)),
        Risky: true,
        Handler: async (ctx, a, ct) =>
        {
            var rel = RelPath(a);
            var op = Str(a, "op") ?? "replace";
            var idx = a.TryGetProperty("index", out var i)
                ? i.GetInt32() : 0;
            var ctype = Str(a, "cell_type") ?? "code";
            var source = Str(a, "source") ?? "";
            var r = await ctx.Executor.ExecAsync(ctx.Sandbox,
                $"cd {Workspace} && OHN_NB_OP={Sh(op)} OHN_NB_IDX={idx} "
                + $"OHN_NB_TYPE={Sh(ctype)} python3 - <<'PYEOF'\n"
                + PyEdit + $"\nPYEOF\n{Sh(rel)}\n{Heredoc(source)}",
                ctx.DefaultTimeout, ct);
            return r.ExitCode == 0
                ? ToolResult.Ok(new
                {
                    path = rel,
                    op,
                    index = int.Parse(r.StdOut.Trim()),
                })
                : ToolResult.Error($"notebook_edit falhou: {r.StdErr.Trim()}");
        });

    private static string RelPath(JsonElement a)
    {
        var p = a.GetProperty("path").GetString()!;
        return p.StartsWith('/') ? p[1..] : p;
    }

    private static string Heredoc(string source) =>
        "OHN_NB_SRC\n" + source + "\nOHN_NB_SRC";

    // Lê source do heredoc (2º arg posicional depois do script).
    private const string PyEdit = """
        import json, os, sys
        path = sys.argv[1]
        nb = json.load(open(path, encoding='utf-8'))
        cells = nb.setdefault('cells', [])
        op, idx = os.environ['OHN_NB_OP'], int(os.environ['OHN_NB_IDX'])
        src_lines = []
        if op != 'delete':
            raw = sys.stdin.read().split('OHN_NB_SRC\n', 1)[1].rsplit('\nOHN_NB_SRC', 1)[0]
            src_lines = [l + '\n' for l in raw.split('\n')]
            if src_lines:
                src_lines[-1] = src_lines[-1].rstrip('\n')
        if op == 'insert':
            cells.insert(idx, {
                'cell_type': os.environ['OHN_NB_TYPE'],
                'source': src_lines, 'metadata': {},
                **({'outputs': [], 'execution_count': None}
                   if os.environ['OHN_NB_TYPE'] == 'code' else {})})
        elif op == 'replace':
            c = cells[idx]
            c['source'] = src_lines
            c['cell_type'] = os.environ['OHN_NB_TYPE']
            if c['cell_type'] == 'code':
                c.setdefault('outputs', []); c['outputs'] = []
        elif op == 'delete':
            cells.pop(idx)
        json.dump(nb, open(path, 'w', encoding='utf-8'), indent=1)
        print(idx)
        """;

    private const string PyRead = """
        import json, sys
        cells = json.load(open(sys.argv[1], encoding='utf-8'))['cells']
        out = []
        for i, c in enumerate(cells[:200]):
            src = ''.join(c.get('source', []))
            out.append({'index': i, 'type': c.get('cell_type'),
                        'chars': len(src),
                        'preview': src[:300]})
        print(json.dumps(out))
        """;

    private static string? Str(JsonElement a, string n) =>
        a.TryGetProperty(n, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string Sh(string v) =>
        "'" + v.Replace("'", "'\\''") + "'";

    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
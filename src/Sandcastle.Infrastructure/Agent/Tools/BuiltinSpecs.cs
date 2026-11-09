using System.Text.Json;

using Sandcastle.Application.Agent;

namespace Sandcastle.Infrastructure.Agent.Tools;

/// <summary>
/// SPEC-121 (port OWUIC <c>specs</c>/<c>write_plan</c>/<c>context_notes</c>
/// file-based): gestão de artefatos markdown dentro do workspace —
/// specs em <c>.openhands/specs</c>, planos em <c>.openhands/plans</c>,
/// notas de contexto em <c>.openhands/context-notes.md</c>. Tudo via
/// <see cref="Application.Sandbox.ISandboxExecutor.ExecAsync"/>.
/// </summary>
public static class BuiltinSpecs
{
    private const string SpecsDir = ".openhands/specs";
    private const string PlansDir = ".openhands/plans";
    private const string NotesFile = ".openhands/context-notes.md";
    private const int CapBytes = 32 * 1024;

    /// <summary>Operações sobre <c>.openhands/specs/*.md</c>.</summary>
    public static AgentTool Specs() => new(
        new ToolSchema("specs",
            "Manage workspace spec files (.openhands/specs/*.md): "
            + "op=list|read|write.",
            Schema("""
                {"type":"object","properties":{
                  "op":{"type":"string","enum":["list","read","write"]},
                  "name":{"type":"string","description":"Spec file name (read/write)"},
                  "content":{"type":"string","description":"Markdown body (write)"}},
                 "required":["op"]}
                """)),
        Risky: true,
        Handler: (ctx, a, ct) => FileOpAsync(ctx, a, ct, SpecsDir));

    /// <summary>Escreve/atualiza o plano corrente em markdown.</summary>
    public static AgentTool WritePlan() => new(
        new ToolSchema("write_plan",
            "Write or update the run plan (.openhands/plans/{name}.md) "
            + "— persists the plan so a compacted context can recover it.",
            Schema("""
                {"type":"object","properties":{
                  "name":{"type":"string","description":"Plan file name (default 'current')"},
                  "content":{"type":"string","description":"Plan markdown"}},
                 "required":["content"]}
                """)),
        Risky: true,
        Handler: async (ctx, a, ct) =>
        {
            var name = Str(a, "name") ?? "current";
            if (!SafeName(name))
            {
                return ToolResult.Error("name inválido");
            }

            var content = a.GetProperty("content").GetString()!;
            var r = await ctx.Executor.ExecAsync(ctx.Sandbox,
                $"cd /workspace/repo && mkdir -p {PlansDir} && "
                + $"cat > {PlansDir}/{name}.md <<'SC_EOF'\n{content}\nSC_EOF\n"
                + "&& echo wrote",
                ctx.DefaultTimeout, ct);
            return r.ExitCode == 0
                ? ToolResult.Ok(new { path = $"{PlansDir}/{name}.md" })
                : ToolResult.Error($"write_plan falhou: {r.StdErr.Trim()}");
        });

    /// <summary>Lê ou adiciona notas no arquivo de contexto do run.</summary>
    public static AgentTool ContextNotes() => new(
        new ToolSchema("context_notes",
            "Read or append to the run's context notes "
            + "(.openhands/context-notes.md) — working memory that "
            + "survives context compaction.",
            Schema("""
                {"type":"object","properties":{
                  "op":{"type":"string","enum":["read","append"]},
                  "note":{"type":"string","description":"Markdown note (append)"}},
                 "required":["op"]}
                """)),
        Risky: false,
        Handler: async (ctx, a, ct) =>
        {
            var op = Str(a, "op");
            if (op == "read")
            {
                var r = await ctx.Executor.ExecAsync(ctx.Sandbox,
                    $"cd /workspace/repo && cat {NotesFile} 2>/dev/null "
                    + $"| head -c {CapBytes} || true",
                    ctx.DefaultTimeout, ct);
                return ToolResult.Ok(new
                {
                    notes = r.StdOut.Length > 0
                        ? r.StdOut : "(vazio)",
                });
            }

            var note = Str(a, "note");
            if (note is null)
            {
                return ToolResult.Error("note é obrigatório em op=append");
            }

            var w = await ctx.Executor.ExecAsync(ctx.Sandbox,
                "cd /workspace/repo && mkdir -p .openhands && "
                + $"cat >> {NotesFile} <<'SC_EOF'\n{note}\nSC_EOF\n"
                + "&& echo appended",
                ctx.DefaultTimeout, ct);
            return w.ExitCode == 0
                ? ToolResult.Ok(new { appended = true, path = NotesFile })
                : ToolResult.Error($"context_notes falhou: {w.StdErr.Trim()}");
        });

    private static async Task<ToolResult> FileOpAsync(
        ToolCallContext ctx, JsonElement a, CancellationToken ct,
        string dir)
    {
        var op = Str(a, "op");
        switch (op)
        {
            case "list":
                {
                    var r = await ctx.Executor.ExecAsync(ctx.Sandbox,
                        $"cd /workspace/repo && ls {dir}/*.md 2>/dev/null "
                        + "| xargs -r -n1 basename || true",
                        ctx.DefaultTimeout, ct);
                    var files = r.StdOut.Split('\n',
                        StringSplitOptions.RemoveEmptyEntries);
                    return ToolResult.Ok(new { files });
                }
            case "read":
                return await ReadSpecAsync(ctx, a, ct, dir);
            case "write":
                return await WriteSpecAsync(ctx, a, ct, dir);
            default:
                return ToolResult.Error("op deve ser list|read|write");
        }
    }


    private static async Task<ToolResult> ReadSpecAsync(
        ToolCallContext ctx, JsonElement a, CancellationToken ct,
        string dir)
    {
        var name = Str(a, "name");
        if (name is null || !SafeName(name.Replace(".md", "")))
        {
            return ToolResult.Error("name inválido");
        }

        var file = name.EndsWith(".md", StringComparison.Ordinal)
            ? name : name + ".md";
        var r = await ctx.Executor.ExecAsync(ctx.Sandbox,
            $"cd /workspace/repo && cat {dir}/{file} 2>/dev/null "
            + $"| head -c {CapBytes}",
            ctx.DefaultTimeout, ct);
        return r.StdOut.Length > 0
            ? ToolResult.Ok(new { name = file, content = r.StdOut })
            : ToolResult.Error($"spec '{file}' não encontrada");
    }

    private static async Task<ToolResult> WriteSpecAsync(
        ToolCallContext ctx, JsonElement a, CancellationToken ct,
        string dir)
    {
        var name = Str(a, "name");
        var content = Str(a, "content");
        if (name is null || !SafeName(name.Replace(".md", "")))
        {
            return ToolResult.Error("name inválido");
        }
        if (content is null)
        {
            return ToolResult.Error("content é obrigatório");
        }

        var file = name.EndsWith(".md", StringComparison.Ordinal)
            ? name : name + ".md";
        var r = await ctx.Executor.ExecAsync(ctx.Sandbox,
            $"cd /workspace/repo && mkdir -p {dir} && "
            + $"cat > {dir}/{file} <<'SC_EOF'\n{content}\nSC_EOF\n"
            + "&& echo wrote",
            ctx.DefaultTimeout, ct);
        return r.ExitCode == 0
            ? ToolResult.Ok(new { path = $"{dir}/{file}" })
            : ToolResult.Error($"specs write falhou: {r.StdErr.Trim()}");
    }

    private static bool SafeName(string v) =>
        v.Length is > 0 and <= 100 && v.All(c =>
            char.IsLetterOrDigit(c) || c is '.' or '_' or '-');

    private static string? Str(JsonElement a, string n) =>
        a.TryGetProperty(n, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
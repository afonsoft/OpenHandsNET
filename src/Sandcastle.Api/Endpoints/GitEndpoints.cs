using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http.HttpResults;

using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Git;
using Sandcastle.Application.Sandbox;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// SPEC-12: proxies git/files da conversa + gestão de credenciais
/// (<c>/api/v1/git/credentials</c> — metadados; valores ficam cifrados no
/// secrets store).
/// </summary>
public static class GitEndpoints
{
    private const string PathMustStayUnderWorkspace = "path must stay under /workspace";
    private const string NoRepository = "no_repository";
    /// <summary>Maps the git/files proxy + credentials endpoints.</summary>
    public static IEndpointRouteBuilder MapGitEndpoints(this IEndpointRouteBuilder app)
    {
        var conv = app.MapGroup("/api/v1/app-conversations").WithTags("Git");

        conv.MapGet("/{id:guid}/git/changes", GitChangesAsync);
        conv.MapGet("/{id:guid}/git/diff", GitDiffAsync);
        conv.MapGet("/{id:guid}/git/file", GitFileAtRefAsync);
        conv.MapGet("/{id:guid}/git/log", GitLogAsync);
        conv.MapGet("/{id:guid}/files", ListFilesAsync);
        conv.MapGet("/{id:guid}/file", ReadFileAsync);
        // SPEC-42 RF-001: escrita com optimistic lock (ETag sha256) +
        // operações de diretório/arquivo via comandos fixos server-side +
        // busca global + commit git.
        conv.MapPut("/{id:guid}/file", WriteFileAsync);
        conv.MapPost("/{id:guid}/files/mkdir", MkdirAsync);
        conv.MapPost("/{id:guid}/files/rename", RenameAsync);
        conv.MapPost("/{id:guid}/files/delete", DeleteAsync);
        conv.MapGet("/{id:guid}/files/search", SearchFilesAsync);
        conv.MapPost("/{id:guid}/git/commit", GitCommitAsync);
        conv.MapPost("/{id:guid}/exec", ExecCommandAsync);

        // SPEC-135 RF-005: reverse-apply de hunks do diff por índice —
        // hunks que falham voltam em conflicts (409) sem abortar os demais.
        conv.MapPost("/{id:guid}/files/revert-hunks", RevertHunksAsync);

        app.MapGet("/api/v1/git/credentials", async (
            Sandcastle.Infrastructure.Git.GitCredentialProvider store,
            Sandcastle.Application.Identity.ICurrentUserAccessor current,
            CancellationToken ct) =>
            Results.Ok(await store.ListAsync(current.User?.UserId.ToString(), ct)))
            .WithTags("Git Credentials");
        app.MapPut("/api/v1/git/credentials/{host}", PutCredentialAsync)
            .WithTags("Git Credentials");
        app.MapDelete("/api/v1/git/credentials/{host}", async (
            string host,
            Sandcastle.Infrastructure.Git.GitCredentialProvider store,
            Sandcastle.Application.Identity.ICurrentUserAccessor current,
            HttpContext http, CancellationToken ct) =>
            await store.DeleteAsync(host,
                current.User?.UserId.ToString()
                    ?? Sandcastle.Infrastructure.Git.GitCredentialProvider.SystemOwner,
                ct)
                ? Results.NoContent()
                : SandcastleProblemFactory.Create(
                    http, 404, $"No credential for {host}"))
            .WithTags("Git Credentials");

        return app;
    }

    private static async Task<IResult> GitChangesAsync(
        Guid id, IAppConversationService svc, IGitService git,
        HttpContext http, CancellationToken ct)
    {
        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning);
        }

        if (!await git.HasRepositoryAsync(sb, ct))
        {
            return SandcastleProblemFactory.Create(http, 404, NoRepository);
        }

        return Results.Ok(await git.GetChangesAsync(sb, ct));
    }

    private static async Task<IResult> GitDiffAsync(
        Guid id, string? path, string? format,
        IAppConversationService svc, IGitService git,
        HttpContext http, CancellationToken ct)
    {
        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning);
        }

        if (!await git.HasRepositoryAsync(sb, ct))
        {
            return SandcastleProblemFactory.Create(http, 404, NoRepository);
        }

        if (string.Equals(format, "raw", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Text(
                await git.GetRawDiffAsync(sb, path, ct) ?? "", "text/plain");
        }

        var report = await git.GetDiffAsync(sb, path, ct);
        return Results.Ok(report);
    }

    /// <summary>
    /// SPEC-21 RF-006: conteúdo do arquivo na revisão (default HEAD) — o
    /// client usa como lado "original" do Monaco diff editor. O path pode ser
    /// absoluto (<c>/workspace/…</c>) ou relativo ao repo; <c>..</c> → 400.
    /// </summary>
    private static async Task<IResult> GitFileAtRefAsync(
        Guid id, string? path, string? @ref,
        IAppConversationService svc, IGitService git,
        HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Contains("..", StringComparison.Ordinal))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "path required and must not contain '..'");
        }

        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning);
        }

        if (!await git.HasRepositoryAsync(sb, ct))
        {
            return SandcastleProblemFactory.Create(http, 404, NoRepository);
        }

        var content = await git.GetFileAtRefAsync(sb, path, @ref ?? "HEAD", ct);
        return content is null
            ? SandcastleProblemFactory.Create(
                http, 404, $"file not found at ref: {path}")
            : Results.Ok(new { encoding = "utf-8", content });
    }

    private static async Task<IResult> GitLogAsync(
        Guid id, int? limit,
        IAppConversationService svc, IGitService git,
        HttpContext http, CancellationToken ct)
    {
        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning);
        }

        if (!await git.HasRepositoryAsync(sb, ct))
        {
            return SandcastleProblemFactory.Create(http, 404, NoRepository);
        }

        return Results.Ok(await git.GetLogAsync(sb, limit ?? 50, ct));
    }

    private static async Task<IResult> ListFilesAsync(
        Guid id, string? path,
        IAppConversationService svc, ISandboxExecutor executor,
        HttpContext http, CancellationToken ct)
    {
        // Input inválido → 400 antes de qualquer checagem de estado.
        var dir = path ?? "/workspace";
        if (!IsValidWorkspacePath(dir))
        {
            return SandcastleProblemFactory.Create(
                http, 400, PathMustStayUnderWorkspace);
        }

        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning);
        }

        var files = await executor.ListFilesAsync(sb, dir, ct);
        return Results.Ok(files.Select(f => new
        {
            path = f.Path,
            is_directory = f.IsDirectory,
            size = f.Size,
        }));
    }

    private static async Task<IResult> ReadFileAsync(
        Guid id, string path,
        IAppConversationService svc, ISandboxExecutor executor,
        HttpContext http, CancellationToken ct)
    {
        if (path is null || !IsValidWorkspacePath(path))
        {
            return SandcastleProblemFactory.Create(
                http, 400, PathMustStayUnderWorkspace);
        }

        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning);
        }

        const int limit = 1024 * 1024; // 1 MB
        var head = await executor.ExecAsync(sb,
            $"head -c {limit + 1} -- '{path.Replace("'", "'\\''")}'",
            TimeSpan.FromSeconds(30), ct);
        if (head.ExitCode != 0)
        {
            return SandcastleProblemFactory.Create(
                http, 404, $"file not found: {path}");
        }

        var bytes = Encoding.UTF8.GetBytes(head.StdOut);
        var truncated = bytes.Length > limit;
        if (truncated)
        {
            Array.Resize(ref bytes, limit);
        }

        // SPEC-42 RF-001: ETag sha256 do conteúdo — optimistic lock do PUT.
        var etag = ETag(bytes);

        // Binário? devolve base64 + flag (paridade upstream).
        if (bytes.AsSpan().IndexOf((byte)0) >= 0)
        {
            return Results.Ok(new
            {
                encoding = "base64",
                content = Convert.ToBase64String(bytes),
                truncated,
                etag,
            });
        }

        return Results.Ok(new
        {
            encoding = "utf-8",
            content = Encoding.UTF8.GetString(bytes),
            truncated,
            etag,
        });
    }

    /// <summary>
    /// SPEC-42 RF-001: PUT de arquivo — <c>{content_base64, etag?}</c>.
    /// ETag divergente → 409 <c>{current_etag}</c> (optimistic concurrency
    /// contra escritas do agente no mesmo workdir). Cap 5MB.
    /// </summary>
    private static async Task<IResult> WriteFileAsync(
        Guid id, string path, WriteFileRequest body,
        IAppConversationService svc, ISandboxExecutor executor,
        HttpContext http, CancellationToken ct)
    {
        if (!IsValidWorkspacePath(path))
        {
            return SandcastleProblemFactory.Create(
                http, 400, PathMustStayUnderWorkspace);
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(body.ContentBase64 ?? "");
        }
        catch (FormatException)
        {
            return SandcastleProblemFactory.Create(
                http, 400, "content_base64 must be valid base64");
        }

        const int maxBytes = 5 * 1024 * 1024; // 5 MB
        if (bytes.Length > maxBytes)
        {
            return SandcastleProblemFactory.Create(
                http, 400, "file exceeds 5MB cap");
        }

        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning);
        }

        if (body.Etag is not null)
        {
            var current = await TryReadEtagAsync(sb, path, executor, ct);
            if (!string.Equals(current, body.Etag, StringComparison.Ordinal))
            {
                return SandcastleProblemFactory.Create(
                    http, 409,
                    $"etag conflict — current={current ?? "missing"}");
            }
        }

        var dir = Path.GetDirectoryName(path.Replace('/', '/'))
            ?.Replace('\\', '/');
        if (dir is { Length: > 0 })
        {
            await executor.ExecAsync(sb,
                $"mkdir -p -- '{dir.Replace("'", "'\\''")}'",
                TimeSpan.FromSeconds(15), ct);
        }

        await executor.WriteFileAsync(
            sb, path, Encoding.UTF8.GetString(bytes), ct);
        return Results.Ok(new { etag = ETag(bytes) });
    }

    /// <summary>SPEC-42 RF-001: <c>mkdir -p</c> — comando fixo server-side.</summary>
    private static async Task<IResult> MkdirAsync(
        Guid id, FilePathRequest body,
        IAppConversationService svc, ISandboxExecutor executor,
        HttpContext http, CancellationToken ct) =>
        await FixedExecAsync(id, svc, executor, http, ct, body.Path,
            p => $"mkdir -p -- '{Escape(p)}'");

    /// <summary>SPEC-42 RF-001: <c>mv</c> — ambos os paths validados.</summary>
    private static async Task<IResult> RenameAsync(
        Guid id, RenameRequest body,
        IAppConversationService svc, ISandboxExecutor executor,
        HttpContext http, CancellationToken ct)
    {
        if (body.From is null || body.To is null
            || !IsValidWorkspacePath(body.From)
            || !IsValidWorkspacePath(body.To))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "paths must stay under /workspace");
        }

        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning);
        }

        var r = await executor.ExecAsync(sb,
            $"mv -- '{Escape(body.From)}' '{Escape(body.To)}'",
            TimeSpan.FromSeconds(15), ct);
        if (r.ExitCode == 0)
        {
            return Results.NoContent();
        }

        var err = r.StdErr.Trim() is { Length: > 0 } e
            ? e : "rename failed";
        return SandcastleProblemFactory.Create(http, 400, err);
    }

    /// <summary>SPEC-42 RF-001: <c>rm -rf</c> — raiz do workspace proibida.</summary>
    private static async Task<IResult> DeleteAsync(
        Guid id, FilePathRequest body,
        IAppConversationService svc, ISandboxExecutor executor,
        HttpContext http, CancellationToken ct)
    {
        var p = body.Path?.TrimEnd('/');
        if (p is null || !IsValidWorkspacePath(p) || p.Length <= "/workspace/".Length)
        {
            return SandcastleProblemFactory.Create(
                http, 400, PathMustStayUnderWorkspace);
        }

        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning);
        }

        var r = await executor.ExecAsync(sb,
            $"rm -rf -- '{Escape(p)}'",
            TimeSpan.FromSeconds(15), ct);
        if (r.ExitCode == 0)
        {
            return Results.NoContent();
        }

        var err = r.StdErr.Trim() is { Length: > 0 } e
            ? e : "delete failed";
        return SandcastleProblemFactory.Create(http, 400, err);
    }

    /// <summary>
    /// SPEC-42 RF-001: busca global — <c>grep -rInF</c> via exec (literal,
    /// -I ignora binários), cap 500 matches, timeout 30s.
    /// Retorna <c>[{path,line,col,preview}]</c>.
    /// </summary>
    private static async Task<IResult> SearchFilesAsync(
        Guid id, string? q, string? glob,
        IAppConversationService svc, ISandboxExecutor executor,
        HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Contains('\n'))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "q required (single line)");
        }

        if (glob is { Length: > 0 } g
            && (g.Contains('\'', StringComparison.Ordinal)
                || g.Contains("..", StringComparison.Ordinal)))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "invalid glob");
        }

        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning);
        }

        // BusyBox grep (imagem bash:5) não tem --include/-I — glob é
        // filtrado no parse abaixo. 2>/dev/null silencia binários/dirs.
        var r = await executor.ExecAsync(sb,
            $"cd /workspace && grep -rnF -e '{Escape(q)}' ."
            + " 2>/dev/null | head -500",
            TimeSpan.FromSeconds(30), ct);

        // grep exit 1 = zero matches (não é erro); 2+ = erro real.
        if (r.ExitCode > 1 && !r.TimedOut)
        {
            return SandcastleProblemFactory.Create(
                http, 500, r.StdErr.Trim() is { Length: > 0 } e
                    ? e : "search failed");
        }

        var matches = ParseGrepMatches(r.StdOut, q, glob);
        return Results.Ok(new { matches, timed_out = r.TimedOut });
    }

    private static List<object> ParseGrepMatches(
        string stdOut, string q, string? glob)
    {
        var globRe = glob is { Length: > 0 }
            ? new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(glob)
                    .Replace("\\*", ".*").Replace("\\?", ".") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1))
            : null;

        var matches = new List<object>();
        foreach (var line in stdOut.Split('\n',
            StringSplitOptions.RemoveEmptyEntries))
        {
            // ./rel/path:NN:text — primeiro ':' separa path, segundo a linha.
            var first = line.IndexOf(':');
            var second = first < 0 ? -1 : line.IndexOf(':', first + 1);
            if (second < 0
                || !int.TryParse(line.AsSpan(first + 1, second - first - 1),
                    out var ln))
            {
                continue;
            }

            var p = line[..first].TrimStart('.', '/');
            if (globRe is not null
                && !globRe.IsMatch(Path.GetFileName(p)))
            {
                continue;
            }

            var preview = line[(second + 1)..];
            var col = preview.IndexOf(q, StringComparison.Ordinal);
            matches.Add(new
            {
                path = "/workspace/" + p,
                line = ln,
                col = col < 0 ? 0 : col + 1,
                preview = preview.Length > 200 ? preview[..200] : preview,
            });
        }

        return matches;
    }

    /// <summary>SPEC-42 RF-005: commit via GitService (author agent).</summary>
    private static async Task<IResult> GitCommitAsync(
        Guid id, GitCommitRequest body,
        IAppConversationService svc, IGitService git,
        HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Message))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "message required");
        }

        if (body.Files is { } files
            && files.Any(f => f.Contains("..", StringComparison.Ordinal)
                || f.Contains('\'', StringComparison.Ordinal)))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "invalid file path");
        }

        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning);
        }

        if (!await git.HasRepositoryAsync(sb, ct))
        {
            return SandcastleProblemFactory.Create(http, 404, NoRepository);
        }

        var r = await git.CommitAsync(sb, body.Message, body.Files, ct);
        return r.Success
            ? Results.Ok(new { sha = r.Hash })
            : SandcastleProblemFactory.Create(
                http, 400, r.Error ?? "commit failed");
    }

    private static async Task<IResult> FixedExecAsync(
        Guid id, IAppConversationService svc, ISandboxExecutor executor,
        HttpContext http, CancellationToken ct, string? path,
        Func<string, string> command)
    {
        if (path is null || !IsValidWorkspacePath(path))
        {
            return SandcastleProblemFactory.Create(
                http, 400, PathMustStayUnderWorkspace);
        }

        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning);
        }

        var r = await executor.ExecAsync(
            sb, command(path), TimeSpan.FromSeconds(15), ct);
        if (r.ExitCode == 0)
        {
            return Results.NoContent();
        }

        var err = r.StdErr.Trim() is { Length: > 0 } e
            ? e : "operation failed";
        return SandcastleProblemFactory.Create(http, 400, err);
    }

    private static async Task<string?> TryReadEtagAsync(
        Domain.Sandbox.Sandbox sb, string path,
        ISandboxExecutor executor, CancellationToken ct)
    {
        try
        {
            // Mesmo cap de leitura do GET (1MB) — ETags comparáveis.
            var head = await executor.ExecAsync(sb,
                $"head -c {1024 * 1024 + 1} -- '{Escape(path)}'",
                TimeSpan.FromSeconds(30), ct);
            if (head.ExitCode != 0)
            {
                return null;
            }

            var bytes = Encoding.UTF8.GetBytes(head.StdOut);
            if (bytes.Length > 1024 * 1024)
            {
                Array.Resize(ref bytes, 1024 * 1024);
            }

            return ETag(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ETag(byte[] bytes) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(bytes));

    private static string Escape(string s) =>
        s.Replace("'", "'\\''");

    private sealed record WriteFileRequest(
        string? ContentBase64, string? Etag);
    private sealed record FilePathRequest(string? Path);
    private sealed record RenameRequest(string? From, string? To);
    private sealed record GitCommitRequest(
        string? Message, IReadOnlyList<string>? Files);

    /// <summary>
    /// SPEC-39 RF-001: exec one-shot pós-run para asserts do eval harness
    /// (<c>exit_code</c>). Comando roda no sandbox via <c>bash -c</c>.
    /// </summary>
    private static async Task<IResult> ExecCommandAsync(
        Guid id, JsonElement body,
        IAppConversationService svc, ISandboxExecutor executor,
        HttpContext http, CancellationToken ct)
    {
        // SPEC-97 RF-002: body não-objeto (ex.: `null`) → 400, não 500.
        if (body.ValueKind != JsonValueKind.Object)
        {
            return SandcastleProblemFactory.Create(
                http, 400, "object body required");
        }

        var cmd = body.TryGetProperty("command", out var c)
            ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(cmd))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "command required");
        }

        var timeoutS = body.TryGetProperty("timeout_s", out var t)
            && t.TryGetInt32(out var ts) ? ts : 120;

        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning);
        }

        var r = await executor.ExecAsync(sb,
            $"cd /workspace && {cmd}",
            TimeSpan.FromSeconds(Math.Clamp(timeoutS, 1, 600)), ct);
        return Results.Ok(new
        {
            exit_code = r.ExitCode,
            stdout = r.StdOut,
            stderr = r.StdErr,
            timed_out = r.TimedOut,
        });
    }

    /// <summary>Rejeita <c>..</c> e caminhos fora de <c>/workspace</c>.</summary>
    private static bool IsValidWorkspacePath(string path) =>
        !path.Contains("..", StringComparison.Ordinal)
        && path.StartsWith("/workspace", StringComparison.Ordinal);

    private static async Task<IResult> PutCredentialAsync(
        string host, JsonElement body,
        Sandcastle.Infrastructure.Git.GitCredentialProvider store,
        Sandcastle.Application.Identity.ICurrentUserAccessor current,
        HttpContext http, CancellationToken ct)
    {
        // SPEC-97 RF-002: body não-objeto (ex.: `null`) → 400, não 500.
        if (body.ValueKind != JsonValueKind.Object)
        {
            return SandcastleProblemFactory.Create(
                http, 400, "object body required");
        }

        var kind = body.TryGetProperty("kind", out var k)
            ? k.GetString() ?? "token" : "token";
        string? secret;
        if (body.TryGetProperty("token", out var t))
        {
            secret = t.GetString();
        }
        else if (body.TryGetProperty("ssh_key", out var s))
        {
            secret = s.GetString();
        }
        else
        {
            secret = null;
        }
        if (string.IsNullOrEmpty(secret))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "token or ssh_key required");
        }

        await store.PutAsync(
            host, kind, secret,
            current.User?.UserId.ToString()
                ?? Sandcastle.Infrastructure.Git.GitCredentialProvider.SystemOwner,
            ct);
        return Results.NoContent();
    }

    /// <summary>SPEC-135 RF-005: corpo do revert-hunks.</summary>
    private sealed record RevertHunksBody(
        string? Path, IReadOnlyList<int>? HunkIndexes);

    /// <summary>
    /// SPEC-135 RF-005: <c>files/revert-hunks</c> — delega o reverse-apply
    /// por hunk ao <see cref="IConversationUxService"/> (guards de
    /// sandbox/path/tracked internos).
    /// </summary>
    private static async Task<IResult> RevertHunksAsync(
        Guid id, RevertHunksBody body,
        IAppConversationService svc, IConversationUxService ux,
        HttpContext http, CancellationToken ct)
    {
        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning);
        }

        var (result, error) = await ux.RevertHunksAsync(
            sb.Id, body.Path ?? "", body.HunkIndexes ?? [], ct);
        if (error is not null)
        {
            return error switch
            {
                RevertHunksError.NotFound
                    => SandcastleProblemFactory.Create(
                        http, 404, "sandbox not found"),
                RevertHunksError.SandboxNotRunning
                    => SandcastleProblemFactory.Create(
                        http, 409, SandcastleProblemFactory.SandboxNotRunning),
                RevertHunksError.InvalidPath
                    => SandcastleProblemFactory.Create(
                        http, 400, PathMustStayUnderWorkspace),
                RevertHunksError.NotTracked
                    => SandcastleProblemFactory.Create(
                        http, 400, "file not tracked by the workdir git"),
                _ => SandcastleProblemFactory.Create(
                    http, 502, "sandbox exec failed"),
            };
        }

        // 409 com applied+conflicts quando algum hunk falha (spec RF-005).
        return result!.Conflicts.Count > 0
            ? Results.Json(new
            {
                applied = result.Applied,
                conflicts = result.Conflicts,
            }, statusCode: 409)
            : Results.Ok(new { applied = result.Applied });
    }

    private static async Task<Domain.Sandbox.Sandbox?> SandboxOfAsync(
        Guid conversationId, IAppConversationService svc, HttpContext http,
        CancellationToken ct)
    {
        var conv = await svc.GetAsync(conversationId, ct);
        if (conv?.SandboxId is null)
        {
            return null;
        }

        var sandboxes = http.RequestServices
            .GetRequiredService<ISandboxService>();
        var sb = await sandboxes.GetAsync(conv.SandboxId.Value, ct);
        return sb?.Status is Domain.Sandbox.SandboxStatus.Running ? sb : null;
    }
}
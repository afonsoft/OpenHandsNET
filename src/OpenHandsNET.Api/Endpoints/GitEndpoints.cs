using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Git;
using OpenHandsNET.Application.Sandbox;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// SPEC-12: proxies git/files da conversa + gestão de credenciais
/// (<c>/api/v1/git/credentials</c> — metadados; valores ficam cifrados no
/// secrets store).
/// </summary>
public static class GitEndpoints
{
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
        conv.MapPost("/{id:guid}/exec", ExecCommandAsync);

        app.MapGet("/api/v1/git/credentials", async (
            OpenHandsNET.Infrastructure.Git.GitCredentialProvider store,
            OpenHandsNET.Application.Identity.ICurrentUserAccessor current,
            CancellationToken ct) =>
            Results.Ok(await store.ListAsync(current.User?.UserId.ToString(), ct)))
            .WithTags("Git Credentials");
        app.MapPut("/api/v1/git/credentials/{host}", PutCredentialAsync)
            .WithTags("Git Credentials");
        app.MapDelete("/api/v1/git/credentials/{host}", async (
            string host,
            OpenHandsNET.Infrastructure.Git.GitCredentialProvider store,
            OpenHandsNET.Application.Identity.ICurrentUserAccessor current,
            HttpContext http, CancellationToken ct) =>
            await store.DeleteAsync(host,
                current.User?.UserId.ToString()
                    ?? OpenHandsNET.Infrastructure.Git.GitCredentialProvider.SystemOwner,
                ct)
                ? Results.NoContent()
                : OpenHandsNetProblemFactory.Create(
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
            return OpenHandsNetProblemFactory.Create(
                http, 409, OpenHandsNetProblemFactory.SandboxNotRunning);
        }

        if (!await git.HasRepositoryAsync(sb, ct))
        {
            return OpenHandsNetProblemFactory.Create(http, 404, "no_repository");
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
            return OpenHandsNetProblemFactory.Create(
                http, 409, OpenHandsNetProblemFactory.SandboxNotRunning);
        }

        if (!await git.HasRepositoryAsync(sb, ct))
        {
            return OpenHandsNetProblemFactory.Create(http, 404, "no_repository");
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
            return OpenHandsNetProblemFactory.Create(
                http, 400, "path required and must not contain '..'");
        }

        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return OpenHandsNetProblemFactory.Create(
                http, 409, OpenHandsNetProblemFactory.SandboxNotRunning);
        }

        if (!await git.HasRepositoryAsync(sb, ct))
        {
            return OpenHandsNetProblemFactory.Create(http, 404, "no_repository");
        }

        var content = await git.GetFileAtRefAsync(sb, path, @ref ?? "HEAD", ct);
        return content is null
            ? OpenHandsNetProblemFactory.Create(
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
            return OpenHandsNetProblemFactory.Create(
                http, 409, OpenHandsNetProblemFactory.SandboxNotRunning);
        }

        if (!await git.HasRepositoryAsync(sb, ct))
        {
            return OpenHandsNetProblemFactory.Create(http, 404, "no_repository");
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
            return OpenHandsNetProblemFactory.Create(
                http, 400, "path must stay under /workspace");
        }

        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return OpenHandsNetProblemFactory.Create(
                http, 409, OpenHandsNetProblemFactory.SandboxNotRunning);
        }

        var files = await executor.ListFilesAsync(sb, dir, ct);
        return Results.Ok(files.Select(f => new
        {
            path = f.Path, is_directory = f.IsDirectory, size = f.Size,
        }));
    }

    private static async Task<IResult> ReadFileAsync(
        Guid id, string path,
        IAppConversationService svc, ISandboxExecutor executor,
        HttpContext http, CancellationToken ct)
    {
        if (path is null || !IsValidWorkspacePath(path))
        {
            return OpenHandsNetProblemFactory.Create(
                http, 400, "path must stay under /workspace");
        }

        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return OpenHandsNetProblemFactory.Create(
                http, 409, OpenHandsNetProblemFactory.SandboxNotRunning);
        }

        const int limit = 1024 * 1024; // 1 MB
        var head = await executor.ExecAsync(sb,
            $"head -c {limit + 1} -- '{path.Replace("'", "'\\''")}'",
            TimeSpan.FromSeconds(30), ct);
        if (head.ExitCode != 0)
        {
            return OpenHandsNetProblemFactory.Create(
                http, 404, $"file not found: {path}");
        }

        var bytes = Encoding.UTF8.GetBytes(head.StdOut);
        var truncated = bytes.Length > limit;
        if (truncated)
        {
            Array.Resize(ref bytes, limit);
        }

        // Binário? devolve base64 + flag (paridade upstream).
        if (bytes.AsSpan().IndexOf((byte)0) >= 0)
        {
            return Results.Ok(new
            {
                encoding = "base64",
                content = Convert.ToBase64String(bytes),
                truncated,
            });
        }

        return Results.Ok(new
        {
            encoding = "utf-8",
            content = Encoding.UTF8.GetString(bytes),
            truncated,
        });
    }

    /// <summary>
    /// SPEC-39 RF-001: exec one-shot pós-run para asserts do eval harness
    /// (<c>exit_code</c>). Comando roda no sandbox via <c>bash -c</c>.
    /// </summary>
    private static async Task<IResult> ExecCommandAsync(
        Guid id, JsonElement body,
        IAppConversationService svc, ISandboxExecutor executor,
        HttpContext http, CancellationToken ct)
    {
        var cmd = body.TryGetProperty("command", out var c)
            ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(cmd))
        {
            return OpenHandsNetProblemFactory.Create(
                http, 400, "command required");
        }

        var timeoutS = body.TryGetProperty("timeout_s", out var t)
            && t.TryGetInt32(out var ts) ? ts : 120;

        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return OpenHandsNetProblemFactory.Create(
                http, 409, OpenHandsNetProblemFactory.SandboxNotRunning);
        }

        var r = await executor.ExecAsync(sb,
            $"cd /workspace && {cmd}",
            TimeSpan.FromSeconds(Math.Clamp(timeoutS, 1, 600)), ct);
        return Results.Ok(new
        {
            exit_code = r.ExitCode, stdout = r.StdOut,
            stderr = r.StdErr, timed_out = r.TimedOut,
        });
    }

    /// <summary>Rejeita <c>..</c> e caminhos fora de <c>/workspace</c>.</summary>
    private static bool IsValidWorkspacePath(string path) =>
        !path.Contains("..", StringComparison.Ordinal)
        && path.StartsWith("/workspace", StringComparison.Ordinal);

    private static async Task<IResult> PutCredentialAsync(
        string host, JsonElement body,
        OpenHandsNET.Infrastructure.Git.GitCredentialProvider store,
        OpenHandsNET.Application.Identity.ICurrentUserAccessor current,
        HttpContext http, CancellationToken ct)
    {
        var kind = body.TryGetProperty("kind", out var k)
            ? k.GetString() ?? "token" : "token";
        var secret = body.TryGetProperty("token", out var t)
            ? t.GetString()
            : body.TryGetProperty("ssh_key", out var s) ? s.GetString() : null;
        if (string.IsNullOrEmpty(secret))
        {
            return OpenHandsNetProblemFactory.Create(
                http, 400, "token or ssh_key required");
        }

        await store.PutAsync(
            host, kind, secret,
            current.User?.UserId.ToString()
                ?? OpenHandsNET.Infrastructure.Git.GitCredentialProvider.SystemOwner,
            ct);
        return Results.NoContent();
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

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
        conv.MapGet("/{id:guid}/git/log", GitLogAsync);
        conv.MapGet("/{id:guid}/files", ListFilesAsync);
        conv.MapGet("/{id:guid}/file", ReadFileAsync);

        var creds = app.MapGroup("/api/v1/git/credentials").WithTags("Git Credentials");
        creds.MapGet("/", async (
            OpenHandsNET.Infrastructure.Git.GitCredentialProvider store,
            CancellationToken ct) => Results.Ok(await store.ListAsync(ct)));
        creds.MapPut("/{host}", PutCredentialAsync);
        creds.MapDelete("/{host}", async (
            string host,
            OpenHandsNET.Infrastructure.Git.GitCredentialProvider store,
            HttpContext http, CancellationToken ct) =>
            await store.DeleteAsync(host, ct)
                ? Results.NoContent()
                : OpenHandsNetProblemFactory.Create(
                    http, 404, $"No credential for {host}"));

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
        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return OpenHandsNetProblemFactory.Create(
                http, 409, OpenHandsNetProblemFactory.SandboxNotRunning);
        }

        var dir = path ?? "/workspace";
        if (!IsValidWorkspacePath(dir))
        {
            return OpenHandsNetProblemFactory.Create(
                http, 400, "path must stay under /workspace");
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
        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return OpenHandsNetProblemFactory.Create(
                http, 409, OpenHandsNetProblemFactory.SandboxNotRunning);
        }

        if (!IsValidWorkspacePath(path))
        {
            return OpenHandsNetProblemFactory.Create(
                http, 400, "path must stay under /workspace");
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

    /// <summary>Rejeita <c>..</c> e caminhos fora de <c>/workspace</c>.</summary>
    private static bool IsValidWorkspacePath(string path) =>
        !path.Contains("..", StringComparison.Ordinal)
        && path.StartsWith("/workspace", StringComparison.Ordinal);

    private static async Task<IResult> PutCredentialAsync(
        string host, JsonElement body,
        OpenHandsNET.Infrastructure.Git.GitCredentialProvider store,
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

        await store.PutAsync(host, kind, secret, ct);
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

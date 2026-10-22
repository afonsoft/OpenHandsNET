using System.Text;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc;

using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Sandbox;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// <c>POST /api/v1/app-conversations/{id}/uploads</c> — SPEC-45 RF-005:
/// anexo do composer → <c>/workspace/uploads/{yyyyMMdd}/{slug}</c>.
/// Extensão allowlist + cap 10MB/arquivo + quota 100MB/conversa; o texto
/// enviado carrega só o path (agente acessa via <c>file_read</c>).
/// </summary>
public static class UploadEndpoints
{
    private const long MaxFileBytes = 10L * 1024 * 1024;
    private const long MaxDirBytes = 100L * 1024 * 1024;

    private static readonly HashSet<string> AllowedExts = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg",
        ".txt", ".md", ".csv", ".json", ".xml", ".yaml", ".yml",
        ".log", ".pdf", ".zip", ".tar", ".gz",
        ".cs", ".csproj", ".sln", ".razor", ".ts", ".js", ".py",
        ".go", ".rs", ".java", ".sh", ".sql", ".html", ".css",
    };

    /// <summary>Maps the uploads route group.</summary>
    public static IEndpointRouteBuilder MapUploadEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/app-conversations/{id:guid}/uploads",
            UploadAsync)
            .WithTags("Conversations")
            .DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> UploadAsync(
        Guid id, HttpRequest request, IAppConversationService svc,
        ISandboxExecutor executor, HttpContext http, CancellationToken ct)
    {
        IFormFile? file;
        try
        {
            file = (await request.ReadFormAsync(ct))
                .Files.GetFile("file");
        }
        catch (Exception)
        {
            file = null;
        }
        if (file is null || file.Length == 0)
        {
            return OpenHandsNetProblemFactory.Create(
                http, 400, "multipart field 'file' is required");
        }

        if (file.Length > MaxFileBytes)
        {
            return OpenHandsNetProblemFactory.Create(
                http, 413, $"file exceeds {MaxFileBytes / (1024 * 1024)}MB cap");
        }

        var slug = Slugify(Path.GetFileName(file.FileName));
        var ext = Path.GetExtension(slug);
        if (slug.Length == 0 || !AllowedExts.Contains(ext))
        {
            return OpenHandsNetProblemFactory.Create(
                http, 400, $"file type not allowed: {ext}");
        }

        var sb = await SandboxOfAsync(id, svc, http, ct);
        if (sb is null)
        {
            return OpenHandsNetProblemFactory.Create(
                http, 409, OpenHandsNetProblemFactory.SandboxNotRunning);
        }

        // Quota por conversa — du -sm do diretório de uploads.
        var dir = "/workspace/uploads";
        var du = await executor.ExecAsync(sb,
            $"du -sm -- '{dir}' 2>/dev/null || echo 0",
            TimeSpan.FromSeconds(15), ct);
        if (long.TryParse(du.StdOut.Trim().Split(
                ['\t', ' '], StringSplitOptions.None)[0],
                out var mb)
            && mb * 1024 * 1024 + file.Length > MaxDirBytes)
        {
            return OpenHandsNetProblemFactory.Create(
                http, 413, "uploads quota exceeded (100MB)");
        }

        var bytes = new byte[file.Length];
        await using (var s = file.OpenReadStream())
        {
            var read = 0;
            while (read < bytes.Length)
            {
                read += await s.ReadAsync(
                    bytes.AsMemory(read), ct);
            }
        }

        var rel = $"{dir}/{DateTimeOffset.UtcNow:yyyyMMdd}/{slug}";
        // Colisão de nome → sufixo curto.
        var probe = await executor.ExecAsync(sb,
            $"test -e '{rel}' && echo exists", TimeSpan.FromSeconds(10), ct);
        if (probe.StdOut.Contains("exists"))
        {
            rel = $"{dir}/{DateTimeOffset.UtcNow:yyyyMMdd}/" +
                  $"{Guid.NewGuid():N}"[..8] + $"-{slug}";
        }

        try
        {
            await executor.WriteFileBytesAsync(sb, rel, bytes, ct);
        }
        catch (Exception ex)
        {
            return OpenHandsNetProblemFactory.Create(
                http, 500, $"upload failed: {ex.Message}");
        }

        return Results.Ok(new { path = rel });
    }

    private static string Slugify(string name)
    {
        var s = name.Trim().Replace(' ', '-');
        s = Regex.Replace(s, @"[^A-Za-z0-9._-]", "",
            RegexOptions.None, TimeSpan.FromMilliseconds(500));
        s = Regex.Replace(s, @"\.{2,}", ".",
            RegexOptions.None, TimeSpan.FromMilliseconds(500));
        return s.Trim('.', '-');
    }

    private static async Task<Domain.Sandbox.Sandbox?> SandboxOfAsync(
        Guid id, IAppConversationService svc, HttpContext http,
        CancellationToken ct)
    {
        var conv = await svc.GetAsync(id, ct);
        if (conv?.SandboxId is null)
        {
            return null;
        }

        var sandboxes = http.RequestServices
            .GetRequiredService<ISandboxService>();
        var sb = await sandboxes.GetAsync(conv.SandboxId.Value, ct);
        return sb?.Status is Domain.Sandbox.SandboxStatus.Running
            ? sb : null;
    }
}
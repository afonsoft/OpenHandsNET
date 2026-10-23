using System.Text.RegularExpressions;

using Microsoft.Extensions.FileProviders;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// SPEC-94: espelho de <c>wwwroot/_framework</c> em URLs sem extensão
/// bloqueada para que proxies corporativos que filtram downloads por
/// extensão (.wasm/.dat/.dll) ou inspecionam payloads binários não
/// quebrem o boot do Blazor WASM. Anônimo — mesma exposição que
/// <see cref="StaticAssetsEndpointRouteBuilderExtensions.MapStaticAssets"/>;
/// estritamente read-only, nomes validados e confinados a
/// <c>_framework</c> com whitelist fechada de extensões.
/// </summary>
public static partial class FrameworkAssetsEndpoints
{
    private static readonly HashSet<string> AllowedExts =
        new(StringComparer.Ordinal) { "wasm", "dat", "dll", "pdb", "json", "js", "map" };

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex ValidName();

    /// <summary>Mapeia <c>GET /framework-assets/{stem}/{ext}</c> (raw e <c>?enc=b64</c>).</summary>
    public static RouteGroupBuilder MapFrameworkAssetsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/framework-assets").AllowAnonymous();
        group.MapGet("/{stem}/{ext}", Serve);
        return group;
    }

    private static IResult Serve(string stem, string ext, HttpContext http,
        IWebHostEnvironment env, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("FrameworkAssets");
        if (stem.Contains("..", StringComparison.Ordinal)
            || !ValidName().IsMatch(stem)
            || !AllowedExts.Contains(ext))
        {
            return Results.NotFound();
        }

        var fileName = $"{stem}.{ext}";
        var root = env.WebRootFileProvider;
        var file = root.GetFileInfo($"_framework/{fileName}");
        if (!file.Exists || file.IsDirectory)
        {
            logger.LogDebug("framework-assets miss: {File}", fileName);
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = "public,max-age=31536000,immutable";

        var enc = (string?)http.Request.Query["enc"];
        if (string.Equals(enc, "b64", StringComparison.Ordinal))
        {
            // Sinal de proxy hostil (content-sniffing) — métrica/log
            logger.LogInformation(
                "framework-assets b64 fallback: {File}", fileName);
            using var raw = file.CreateReadStream();
            using var buffer = new MemoryStream();
            raw.CopyTo(buffer);
            return Results.Text(
                Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length),
                "text/plain");
        }

        logger.LogDebug("framework-assets hit: {File}", fileName);
        // Caches compartilhados (proxy corporativo) precisam chavear por Accept-Encoding.
        http.Response.Headers.Vary = "Accept-Encoding";
        var compressed = TryCompressed(root, fileName,
            http.Request.Headers.AcceptEncoding.ToString(), out var encoding);
        if (compressed is not null)
        {
            http.Response.Headers.ContentEncoding = encoding;
            return Results.File(compressed.CreateReadStream(), ContentType(ext));
        }
        return Results.File(file.CreateReadStream(), ContentType(ext));
    }

    private static string ContentType(string ext) => ext switch
    {
        "wasm" => "application/wasm",
        "json" or "map" => "application/json",
        "js" => "text/javascript",
        _ => "application/octet-stream",
    };

    private static IFileInfo? TryCompressed(
        IFileProvider root, string fileName, string acceptEncoding,
        out string encoding)
    {
        foreach (var (suffix, enc) in new[] { (".br", "br"), (".gz", "gzip") })
        {
            if (!acceptEncoding.Contains(enc, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var sibling = root.GetFileInfo($"_framework/{fileName}{suffix}");
            if (sibling.Exists && !sibling.IsDirectory)
            {
                encoding = enc;
                return sibling;
            }
        }
        encoding = "";
        return null;
    }
}
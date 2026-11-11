using System.Reflection;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Sandbox;
using Sandcastle.Application.Scm;


namespace Sandcastle.Infrastructure.Scm.Qa;

/// <summary>
/// <see cref="IQaBrowserDriverFactory"/> — driver real: copia
/// <c>qa-driver.mjs</c> (embedded resource) para o sandbox e o executa
/// com Node + Playwright da runtime image (SPEC-131). Unit tests usam
/// um factory fake — nenhum teste exige browser.
/// </summary>
public sealed class SandboxQaBrowserDriverFactory(
    ISandboxExecutor executor,
    IOptions<QaOptions> options,
    ILogger<SandboxQaBrowserDriverFactory> logger)
    : IQaBrowserDriverFactory
{
    /// <inheritdoc />
    public IQaBrowserDriver Create(Domain.Sandbox.Sandbox sandbox) =>
        new SandboxQaBrowserDriver(
            executor, options.Value, logger, sandbox);
}

/// <summary>
/// Driver Playwright-in-sandbox (SPEC-142 RF-003): um batch = um script
/// Node (uma sessão browser, um vídeo). Screenshot por passo +
/// <c>page.video()</c> quando habilitado; fallback honesto — browser
/// indisponível → <see cref="QaBrowserRunResult.Executed"/> false.
/// </summary>
internal sealed class SandboxQaBrowserDriver(
    ISandboxExecutor executor,
    QaOptions options,
    ILogger logger,
    Domain.Sandbox.Sandbox sandbox) : IQaBrowserDriver
{
    private const string DriverPath = "/tmp/qa/qa-driver.mjs";
    private const string JobPath = "/tmp/qa/job.json";
    private const string ResultPath = "/tmp/qa/result.json";
    private const string ShotDir = "/tmp/qa/shots";
    private const string VideoDir = "/tmp/qa/video";
    private bool? _available;

    /// <inheritdoc />
    public async Task<QaBrowserRunResult> RunStepsAsync(
        string baseUrl, IReadOnlyList<QaPlanStep> steps,
        CancellationToken ct)
    {
        if (!await EnsureDriverAsync(ct))
        {
            return new QaBrowserRunResult(
                false, "playwright/chromium not available in image",
                [], null);
        }

        var job = new
        {
            base_url = baseUrl,
            out_dir = ShotDir,
            video_dir = options.VideoEnabled ? VideoDir : null,
            timeout_ms = options.StepTimeoutSeconds * 1000,
            steps = steps.Select(s => new
            {
                action = s.Action,
                url = s.Url,
                selector = s.Selector,
                text = s.Text,
                expect = s.Expect,
                timeout_seconds = s.TimeoutSeconds,
            }).ToList(),
        };
        var jobJson = JsonSerializer.Serialize(job);
        await WriteFileAsync(JobPath, jobJson, ct);

        var timeout = TimeSpan.FromSeconds(
            options.StepTimeoutSeconds * (steps.Count + 2) + 30);
        var run = await executor.ExecAsync(sandbox,
            $"cd /tmp/qa && node qa-driver.mjs {JobPath} > {ResultPath} "
            + "2>/tmp/qa/driver.err",
            timeout, ct);
        if (run.TimedOut)
        {
            return new QaBrowserRunResult(
                false, "qa driver timed out", [], null);
        }

        var read = await executor.ExecAsync(sandbox,
            $"cat {ResultPath}", TimeSpan.FromSeconds(15), ct);
        if (read.ExitCode != 0
            || string.IsNullOrWhiteSpace(read.StdOut))
        {
            var tail = await executor.ExecAsync(sandbox,
                "tail -c 1000 /tmp/qa/driver.err 2>/dev/null",
                TimeSpan.FromSeconds(15), ct);
            return new QaBrowserRunResult(
                false, $"qa driver failed: {tail.StdOut.Trim()}",
                [], null);
        }

        using var doc = JsonDocument.Parse(read.StdOut);
        var root = doc.RootElement;
        var stepResults = new List<QaBrowserStepResult>();
        var n = 0;
        foreach (var s in root.GetProperty("steps").EnumerateArray())
        {
            n++;
            byte[]? png = null;
            var shot = s.TryGetProperty("screenshot", out var sh)
                ? sh.GetString() : null;
            if (shot is not null)
            {
                png = await FetchFileAsync(
                    shot, 4 * 1024 * 1024, ct);
            }
            stepResults.Add(new QaBrowserStepResult(
                s.GetProperty("ok").GetBoolean(),
                s.TryGetProperty("detail", out var d)
                    ? d.GetString() : null,
                png,
                s.TryGetProperty("content", out var c)
                    ? c.GetString() : null));
        }
        var stepsMissed = steps.Count - stepResults.Count;
        for (var i = 0; i < stepsMissed; i++)
        {
            stepResults.Add(new QaBrowserStepResult(
                false, "not executed", null, null));
        }

        byte[]? video = null;
        if (root.TryGetProperty("video", out var v)
            && v.ValueKind == JsonValueKind.String
            && v.GetString() is { } vpath)
        {
            video = await FetchFileAsync(
                vpath, options.MaxVideoMb * 1024 * 1024, ct);
        }
        return new QaBrowserRunResult(
            true, null, stepResults, video);
    }

    /// <summary>
    /// Push do driver para o sandbox + checagem da toolchain
    /// (node/playwright/chromium) — cacheado por instância.
    /// </summary>
    private async Task<bool> EnsureDriverAsync(CancellationToken ct)
    {
        if (_available is { } ok)
        {
            return ok;
        }
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            const string res =
                "Sandcastle.Infrastructure.Scm.Qa.qa-driver.mjs";
            await using var stream = asm.GetManifestResourceStream(res);
            if (stream is null)
            {
                logger.LogWarning("embedded resource {Res} missing", res);
                _available = false;
                return false;
            }
            using var reader = new StreamReader(stream);
            var script = await reader.ReadToEndAsync(ct);
            var setup = await executor.ExecAsync(sandbox,
                "mkdir -p /tmp/qa/shots /tmp/qa/video",
                TimeSpan.FromSeconds(15), ct);
            if (setup.ExitCode != 0)
            {
                _available = false;
                return false;
            }
            await WriteFileAsync(DriverPath, script, ct);
            var check = await executor.ExecAsync(sandbox,
                "node -e \"import('playwright').then(()=>process.exit(0))"
                + ".catch(()=>process.exit(1))\"",
                TimeSpan.FromSeconds(30), ct);
            _available = check.ExitCode == 0;
            if (!_available.Value)
            {
                logger.LogInformation(
                    "qa browser driver unavailable in sandbox {Id}",
                    sandbox.Id);
            }
            return _available.Value;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "qa driver bootstrap failed");
            _available = false;
            return false;
        }
    }

    /// <summary>Escreve arquivo texto no sandbox via heredoc-base64.</summary>
    private async Task WriteFileAsync(
        string path, string content, CancellationToken ct)
    {
        var b64 = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(content));
        await executor.ExecAsync(sandbox,
            $"printf '%s' '{b64}' | base64 -d > {path}",
            TimeSpan.FromSeconds(15), ct);
    }

    /// <summary>Lê arquivo do sandbox como bytes (base64 via stdout).</summary>
    private async Task<byte[]?> FetchFileAsync(
        string path, int maxBytes, CancellationToken ct)
    {
        var r = await executor.ExecAsync(sandbox,
            $"base64 -w0 {path} 2>/dev/null | head -c {maxBytes * 4 / 3 + 8}",
            TimeSpan.FromSeconds(30), ct);
        if (r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.StdOut))
        {
            return null;
        }
        try
        {
            return Convert.FromBase64String(r.StdOut.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
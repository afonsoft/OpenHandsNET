using System.Diagnostics;
using System.Text.Json;

namespace OpenHandsNET.Cli.Services;

/// <summary>
/// <c>openhandsnet doctor</c> probes (SPEC-14 RF-003): api readiness,
/// docker daemon, LLM gateway and workspace disk. Server-side checks reuse
/// <c>/api/v1/readiness</c>; docker/disk are probed locally.
/// </summary>
public sealed class DoctorService(HttpClient http)
{
    /// <summary>One probe result: ok flag + detail for display.</summary>
    public sealed record ProbeResult(string Name, bool Ok, string Detail);

    /// <summary>Full doctor report.</summary>
    public sealed record DoctorReport(
        IReadOnlyList<ProbeResult> Probes, string ServerUrl)
    {
        /// <summary>True when every required probe passed.</summary>
        public bool Healthy => Probes.All(p => p.Ok);
    }

    /// <summary>Runs all probes against the server + local machine.</summary>
    public async Task<DoctorReport> RunAsync(
        string serverUrl, string workspaceRoot, CancellationToken ct)
    {
        var probes = new List<ProbeResult>();
        var readiness = await ProbeApiAsync(serverUrl, ct);
        probes.AddRange(readiness);
        probes.Add(await ProbeDockerAsync(ct));
        probes.Add(ProbeDisk(workspaceRoot));
        return new DoctorReport(probes, serverUrl);
    }

    private async Task<IReadOnlyList<ProbeResult>> ProbeApiAsync(
        string serverUrl, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var resp = await http.GetAsync(
                $"{serverUrl.TrimEnd('/')}/api/v1/readiness", cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode && resp.StatusCode
                    is not System.Net.HttpStatusCode.ServiceUnavailable)
            {
                return [new("api", false, $"HTTP {(int)resp.StatusCode}")];
            }

            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("status").GetString() ?? "?";
            var checks = doc.RootElement.TryGetProperty("checks", out var c)
                ? c : default;
            var results = new List<ProbeResult>
            {
                new("api", status == "ready", $"readiness: {status}"),
            };
            if (checks.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in checks.EnumerateObject())
                {
                    var v = kv.Value.GetString() ?? "?";
                    results.Add(new(kv.Name,
                        v is "healthy" or "configured" or "mock" or "true",
                        v));
                }
            }
            return results;
        }
        catch (Exception ex)
        {
            return [new("api", false, $"unreachable: {ex.GetType().Name}")];
        }
    }

    private static async Task<ProbeResult> ProbeDockerAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return proc.ExitCode == 0
                ? new("docker_daemon", true, $"server {stdout.Trim()}")
                : new("docker_daemon", false, "docker info failed");
        }
        catch (Exception ex)
        {
            return new("docker_daemon", false,
                $"unavailable: {ex.GetType().Name}");
        }
    }

    private static ProbeResult ProbeDisk(string workspaceRoot)
    {
        try
        {
            var root = Path.GetFullPath(workspaceRoot);
            Directory.CreateDirectory(root);
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            var ok = freeGb >= 1.0;
            return new("workspace", ok,
                $"{root} — {freeGb:F1} GiB free{(ok ? "" : " (low!)")}");
        }
        catch (Exception ex)
        {
            return new("workspace", false,
                $"error: {ex.Message}");
        }
    }
}

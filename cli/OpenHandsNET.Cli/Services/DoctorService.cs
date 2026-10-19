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

    /// <summary>
    /// SPEC-31 RF-002 / SPEC-32 RF-09: preflight de instalação — DNS do
    /// public domain, egress aos registries, docker, cgroup v2, disco ≥50GB
    /// e portas livres. Falha rápida (gate de CI/install).
    /// </summary>
    public async Task<DoctorReport> RunPreflightAsync(
        string serverUrl, string workspaceRoot, CancellationToken ct,
        bool k8s = false)
    {
        var probes = new List<ProbeResult>
        {
            await ProbePublicDomainAsync(ct),
            await ProbeRegistryAsync("registry-1.docker.io", ct),
            await ProbeRegistryAsync("ghcr.io", ct),
            await ProbeDockerAsync(ct),
            ProbeCgroupV2(),
            ProbeDiskPreflight(workspaceRoot),
            ProbePortFree(8080),
            ProbePortFree(30000),
        };
        // SPEC-37 §3: probes de cluster/sysobx quando --k8s ou provider=k8s.
        if (k8s || string.Equals(
                Environment.GetEnvironmentVariable("OPENHANDSNET_SANDBOX_PROVIDER")
                    ?? Environment.GetEnvironmentVariable(
                        "OPENHANDSNET_Sandbox__Provider"),
                "k8s", StringComparison.OrdinalIgnoreCase))
        {
            probes.Add(await ProbeKubectlAsync(ct));
            probes.Add(await ProbeSysboxAsync(ct));
            probes.Add(await ProbeStorageClassAsync(ct));
        }

        return new DoctorReport(probes, serverUrl);
    }

    /// <summary>kubectl presente + cluster alcançável (context atual).</summary>
    private static async Task<ProbeResult> ProbeKubectlAsync(
        CancellationToken ct)
    {
        var (code, output) = await TryRunAsync(
            "kubectl", "version --client=true -o json", ct);
        if (code < 0)
        {
            return new("k8s_cluster", false,
                "kubectl not found in PATH");
        }

        var (ccode, cinfo) = await TryRunAsync(
            "kubectl", "cluster-info --request-timeout=5s", ct);
        return ccode == 0
            ? new("k8s_cluster", true,
                cinfo.Split('\n')[0].Trim())
            : new("k8s_cluster", false,
                $"cluster unreachable: {cinfo.Split('\n')[0].Trim()}");
    }

    /// <summary>RuntimeClass sysbox-runc — exigida p/ DinD sem privileged.</summary>
    private static async Task<ProbeResult> ProbeSysboxAsync(
        CancellationToken ct)
    {
        var (code, output) = await TryRunAsync("kubectl",
            "get runtimeclass sysbox-runc --request-timeout=5s", ct);
        if (code < 0)
        {
            return new("k8s_sysbox", false, "kubectl not found in PATH");
        }

        return code == 0
            ? new("k8s_sysbox", true, "runtimeclass sysbox-runc present")
            : new("k8s_sysbox", false,
                "sysbox-runc RuntimeClass missing — install sysbox "
                + "(ou rode com privileged em cluster dedicado)");
    }

    /// <summary>Ao menos uma StorageClass default que provisiona PVCs.</summary>
    private static async Task<ProbeResult> ProbeStorageClassAsync(
        CancellationToken ct)
    {
        var (code, output) = await TryRunAsync("kubectl",
            "get storageclass --request-timeout=5s", ct);
        if (code < 0)
        {
            return new("k8s_storageclass", false,
                "kubectl not found in PATH");
        }

        if (code != 0)
        {
            return new("k8s_storageclass", false,
                $"cannot list storageclasses: {output.Split('\n')[0].Trim()}");
        }

        var hasDefault = output.Contains("(default)", StringComparison.Ordinal);
        return new("k8s_storageclass", hasDefault,
            hasDefault ? "default StorageClass present"
                : "no default StorageClass — set sandbox.workspaceStorageClass");
    }

    /// <summary>Roda um executável; devolve (exit_code, stdout+stderr) ou
    /// (-1, erro) quando o binário não existe.</summary>
    private static async Task<(int Code, string Output)> TryRunAsync(
        string file, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            var stderr = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return (p.ExitCode, stdout + stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or FileNotFoundException)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>DNS do domínio público (quando configurado).</summary>
    private static async Task<ProbeResult> ProbePublicDomainAsync(
        CancellationToken ct)
    {
        var domain = Environment.GetEnvironmentVariable(
            "OPENHANDSNET_PUBLIC_DOMAIN");
        if (string.IsNullOrWhiteSpace(domain))
        {
            return new("dns_public_domain", true,
                "skipped (OPENHANDSNET_PUBLIC_DOMAIN unset)");
        }

        try
        {
            var ips = await System.Net.Dns.GetHostAddressesAsync(
                domain, ct);
            return new("dns_public_domain", ips.Length > 0,
                $"{domain} → {string.Join(',', ips.Take(3))}");
        }
        catch (Exception ex)
        {
            return new("dns_public_domain", false,
                $"{domain}: {ex.Message}");
        }
    }

    /// <summary>TCP/443 reachability de um registry/host.</summary>
    private static async Task<ProbeResult> ProbeRegistryAsync(
        string host, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource
                .CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(host, 443, cts.Token);
            return new($"egress_{host}", true, "tcp/443 reachable");
        }
        catch (Exception ex)
        {
            return new($"egress_{host}", false, ex.Message);
        }
    }

    /// <summary>cgroup v2 — exigido por sandboxes com limits.</summary>
    private static ProbeResult ProbeCgroupV2()
    {
        var unified = File.Exists("/sys/fs/cgroup/cgroup.controllers");
        return new("cgroup_v2", unified,
            unified ? "unified hierarchy" : "cgroup v1 only");
    }

    /// <summary>Disco do workspace ≥50GB (sizing enterprise).</summary>
    private static ProbeResult ProbeDiskPreflight(string workspaceRoot)
    {
        try
        {
            var root = Path.GetFullPath(workspaceRoot);
            Directory.CreateDirectory(root);
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            var ok = freeGb >= 50.0;
            return new("disk_50gb", ok,
                $"{root} — {freeGb:F1} GiB free{(ok ? "" : " (<50!)")}");
        }
        catch (Exception ex)
        {
            return new("disk_50gb", false, $"error: {ex.Message}");
        }
    }

    /// <summary>Porta livre localmente (8080 api, 30000 admin console).</summary>
    private static ProbeResult ProbePortFree(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(
                System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return new($"port_{port}", true, "free");
        }
        catch (Exception)
        {
            return new($"port_{port}", false, "in use");
        }
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


    private static string ResolveOnPath(string cmd)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH")
            ?? "").Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir, cmd);
            if (File.Exists(full)) { return full; }
        }
        return cmd;
    }

    private static async Task<ProbeResult> ProbeDockerAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(ResolveOnPath("docker"), "info --format {{.ServerVersion}}")
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
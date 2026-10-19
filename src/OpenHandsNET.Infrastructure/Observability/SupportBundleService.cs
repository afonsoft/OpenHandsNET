using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

using Docker.DotNet;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Observability;

/// <summary>
/// Diagnostics ZIP for support (SPEC-14 RF-004): detailed readiness, recent
/// redacted logs, versions, entity counts and the effective configuration
/// with every secret-shaped value masked. Nothing secret ever leaves in the
/// bundle — redaction is covered by tests.
/// </summary>
public sealed class SupportBundleService(
    AppDbContext db,
    HealthCheckService health,
    IConfiguration config,
    IDockerClient? docker = null)
{
    /// <summary>Log file glob the bundle tails (Serilog rolling file).</summary>
    public const string LogGlob = "openhandsnet-*.log";

    /// <summary>
    /// Keys whose values are always masked. Match is case-insensitive on
    /// path segments (e.g. <c>Agent:ApiKey</c>, <c>ConnectionStrings:db</c>).
    /// </summary>
    private static readonly string[] SensitiveTokens =
        ["key", "secret", "token", "password", "credential", "authorization"];

    /// <summary>Compound names matched by substring ("ConnectionStrings", "conn_string").</summary>
    private static readonly string[] SensitiveSubstrings =
        ["connectionstring", "connection_string"];

    /// <summary>Builds the support bundle ZIP and returns its bytes.</summary>
    public async Task<byte[]> GenerateAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteJsonAsync(zip, "readiness.json",
                await ReadinessAsync(ct), ct);
            await WriteJsonAsync(zip, "versions.json", Versions(), ct);
            await WriteJsonAsync(zip, "counts.json", await CountsAsync(ct), ct);
            await WriteJsonAsync(zip, "config.json", EffectiveConfig(), ct);
            await WriteTextAsync(zip, "logs-recent.txt", RecentLogs(), ct);
        }

        return ms.ToArray();
    }

    private async Task<object> ReadinessAsync(CancellationToken ct)
    {
        var report = await health.CheckHealthAsync(
            r => r.Tags.Contains("ready"), ct);
        string dockerStatus;
        try
        {
            if (docker is null)
            {
                dockerStatus = "unavailable";
            }
            else
            {
                await docker.System.PingAsync(ct);
                dockerStatus = "healthy";
            }
        }
        catch (Exception ex)
        {
            dockerStatus = $"unreachable: {ex.GetType().Name}";
        }

        return new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => (object)new
                {
                    status = e.Value.Status.ToString().ToLowerInvariant(),
                    duration_ms = e.Value.Duration.TotalMilliseconds,
                    error = e.Value.Exception?.Message,
                }),
            docker = dockerStatus,
            duration_ms = report.TotalDuration.TotalMilliseconds,
        };
    }

    private static object Versions() => new
    {
        app = typeof(SupportBundleService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "dev",
        runtime = Environment.Version.ToString(),
        os = Environment.OSVersion.ToString(),
        machine = Environment.MachineName,
        time_utc = DateTimeOffset.UtcNow,
    };

    private async Task<object> CountsAsync(CancellationToken ct) => new
    {
        conversations = await db.Conversations.CountAsync(ct),
        events = await db.Events.CountAsync(ct),
        sandboxes = await db.Sandboxes.CountAsync(ct),
        sandbox_specs = await db.SandboxSpecs.CountAsync(ct),
        organizations = await db.Organizations.CountAsync(ct),
        users = await db.Users.CountAsync(ct),
        api_keys = await db.ApiKeys.CountAsync(ct),
        audit_events = await db.AuditEvents.CountAsync(ct),
        skills = await db.Skills.CountAsync(ct),
        mcp_servers = await db.McpServers.CountAsync(ct),
    };

    /// <summary>
    /// Flattens the effective configuration; every value whose path smells
    /// like a secret — or which itself is shaped like one — becomes <c>***</c>.
    /// </summary>
    public static Dictionary<string, string?> EffectiveConfigOf(
        IConfiguration config)
    {
        var flat = new Dictionary<string, string?>();
        foreach (var kv in config.AsEnumerable())
        {
            if (kv.Value is null)
            {
                continue;
            }

            flat[kv.Key] = IsSensitivePath(kv.Key) || LooksSecret(kv.Value)
                ? "***" : kv.Value;
        }
        return flat;
    }

    private object EffectiveConfig() => EffectiveConfigOf(config);

    private static bool IsSensitivePath(string path)
    {
        // Only ':' separates config segments — splitting '_' would break
        // words like "connectionstring" that must stay whole.
        var segments = path.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s =>
            SensitiveTokens.Any(t =>
                s.Equals(t, StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(t, StringComparison.OrdinalIgnoreCase)) ||
            SensitiveSubstrings.Any(t =>
                s.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool LooksSecret(string value) =>
        value.StartsWith("sk-", StringComparison.Ordinal) ||
        (value.Length >= 24 &&
            (value.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
             value.Contains("pwd=", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Last ~400 lines of the newest rolling log file, redacted. Missing log
    /// dir is normal for dev/test — the entry then says so.
    /// </summary>
    private static string RecentLogs()
    {
        var dir = new DirectoryInfo("logs");
        var file = dir.Exists
            ? dir.GetFiles(LogGlob).OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        if (file is null)
        {
            return "# no log files found under logs/";
        }

        var lines = File.ReadLines(file.FullName).TakeLast(400);
        return string.Join('\n', lines.Select(RedactLine));
    }

    /// <summary>Redacts placeholders and <c>sk-*</c> key shapes from a log line.</summary>
    public static string RedactLine(string line)
    {
        var redacted = SecretRedactor.Redact(line);
        // API-key-shaped values in logs must never survive the bundle.
        return System.Text.RegularExpressions.Regex.Replace(
            redacted, @"sk-[A-Za-z0-9\-_]{8,}", "***",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromMilliseconds(500));
    }

    private static async Task WriteJsonAsync(
        ZipArchive zip, string name, object content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, content, JsonOptions(), ct);
    }

    private static async Task WriteTextAsync(
        ZipArchive zip, string name, string content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content.AsMemory(), ct);
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
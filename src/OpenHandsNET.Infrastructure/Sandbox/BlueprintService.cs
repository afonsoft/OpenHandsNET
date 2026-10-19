using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Sandboxing;

using YamlDotNet.Serialization;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>SPEC-17 RF-004/005: blueprint resolvido para um repo.</summary>
/// <param name="Image">Override de imagem (<c>openhandsnet.yaml:image</c>).</param>
/// <param name="Sdks">SDKs a instalar — nome → versão ("" = default do installer).</param>
/// <param name="Env">Env extra exportado no sandbox.</param>
/// <param name="SetupScript">Script relativo ao repo (default <c>.openhands/setup.sh</c>).</param>
public sealed record BlueprintSpec(
    string? Image,
    IReadOnlyDictionary<string, string> Sdks,
    IReadOnlyDictionary<string, string> Env,
    string SetupScript)
{
    /// <summary>Blueprint vazio — nada a provisionar.</summary>
    public static readonly BlueprintSpec Empty = new(
        null, new Dictionary<string, string>(),
        new Dictionary<string, string>(), ".openhands/setup.sh");
}

/// <summary>
/// SPEC-17 RF-004: resolve e executa o blueprint do repo —
/// <c>openhandsnet.yaml</c> → installers de SDK → <c>.openhands/setup.sh</c>.
/// Sem YAML, auto-detecta SDKs por manifestos (§4.4 da spec). A resolução lê
/// o workspace no host (bind-mount); a execução roda dentro do container via
/// <see cref="ISandboxExecutor"/>. Falha de blueprint não derruba a conversa —
/// grava <c>BlueprintStatus=failed</c> + cauda de log no sandbox record.
/// </summary>
public sealed class BlueprintService(
    WorkspaceProvisioner workspaces,
    IOptions<SandboxOptions> options,
    AppDbContext db,
    ILogger<BlueprintService> logger)
{
    /// <summary>Cauda máxima persistida em <c>blueprint_log_tail</c> (4 KB).</summary>
    public const int LogTailBytes = 4096;

    /// <summary>Scripts de SDK instalados na imagem runtime.</summary>
    public const string SdkDir = "/opt/ohn/sdks";

    /// <summary>
    /// Resolve o blueprint para a conversa: YAML do repo → blueprint da org
    /// (<c>settings.blueprint</c> inline YAML) → auto-detect por manifestos.
    /// </summary>
    public async Task<BlueprintSpec> ResolveAsync(
        Guid conversationId, Guid? orgId, CancellationToken ct)
    {
        var repoDir = RepoDir(conversationId);
        var yamlText = ReadIfExists(Path.Combine(repoDir, "openhandsnet.yaml"));

        // Precedência: repo → org → global (org guarda YAML inline em settings).
        if (yamlText is null && orgId is not null)
        {
            yamlText = await OrgBlueprintAsync(orgId.Value, ct);
        }

        if (yamlText is not null)
        {
            var parsed = Parse(yamlText);
            if (parsed is not null)
            {
                return parsed with
                {
                    Sdks = parsed.Sdks.Count > 0
                        ? parsed.Sdks
                        : DetectSdks(repoDir),
                };
            }
            logger.LogWarning("openhandsnet.yaml inválido — auto-detect de SDK");
        }

        return BlueprintSpec.Empty with { Sdks = DetectSdks(repoDir) };
    }

    /// <summary>
    /// Executa o blueprint dentro do container: installers de SDK
    /// (<c>sudo /opt/ohn/sdks/{name}.sh {ver}</c>) e depois o setup script
    /// como o usuário do container em <c>/workspace/repo</c>. Persiste
    /// status + cauda de log + env/imagem no sandbox record.
    /// </summary>
    public async Task RunAsync(
        Domain.Sandbox.Sandbox sandbox, Guid conversationId,
        BlueprintSpec spec, ISandboxExecutor executor, CancellationToken ct)
    {
        var record = await db.Sandboxes.FindAsync([sandbox.Id], ct);
        if (record is null)
        {
            return;
        }

        var log = new StringBuilder();
        var timeout = TimeSpan.FromMinutes(
            options.Value.BlueprintTimeoutMinutes);
        var failed = false;

        try
        {
            foreach (var (sdk, version) in spec.Sdks)
            {
                var cmd = $"sudo -n {SdkDir}/{sdk}.sh" +
                    (string.IsNullOrEmpty(version) ? "" : $" {version}");
                var r = await executor.ExecAsync(sandbox, cmd, timeout, ct);
                Append(log, $"$ {cmd}", r);
                if (r.ExitCode != 0)
                {
                    failed = true;
                    break;
                }
            }

            var repoDir = RepoDir(conversationId);
            if (!failed && File.Exists(Path.Combine(
                    repoDir, spec.SetupScript.Replace('/', Path.DirectorySeparatorChar))))
            {
                var cmd = $"cd /workspace/repo && " +
                    $"DEBIAN_FRONTEND=noninteractive bash {spec.SetupScript}";
                var r = await executor.ExecAsync(sandbox, cmd, timeout, ct);
                Append(log, $"$ bash {spec.SetupScript}", r);
                failed = r.ExitCode != 0;
            }

            if (spec.Env.Count > 0)
            {
                await WriteEnvAsync(sandbox, spec.Env, executor, log, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.AppendLine($"blueprint error: {ex.Message}");
            failed = true;
        }

        record.BlueprintStatus = failed ? "failed" : "ok";
        record.BlueprintLogTail = Tail(SecretRedactor.Redact(log.ToString()));
        record.BlueprintImage = spec.Image;
        record.BlueprintEnvJson = spec.Env.Count > 0
            ? JsonSerializer.Serialize(spec.Env) : null;
        await db.SaveChangesAsync(ct);
        if (failed)
        {
            logger.LogWarning(
                "Blueprint failed for conversation {ConversationId} — see log tail",
                conversationId);
        }
    }

    /// <summary>SPEC-17 §4.4: auto-detect de SDK por manifestos do repo.</summary>
    public static IReadOnlyDictionary<string, string> DetectSdks(string repoDir)
    {
        var sdks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(repoDir))
        {
            return sdks;
        }

        var has = new Func<string, bool>(p =>
            Directory.EnumerateFiles(repoDir, p,
                SearchOption.TopDirectoryOnly).Any());

        if (has("*.csproj") || has("*.slnx") || has("*.sln") || has("global.json"))
        {
            sdks["dotnet"] = DotnetVersion(repoDir);
        }
        if (has("package.json") || has("package-lock.json"))
        {
            sdks["node"] = NodeVersion(repoDir);
        }
        if (has("requirements.txt") || has("requirements-dev.txt") || has("pyproject.toml"))
        {
            sdks["python"] = PythonVersion(repoDir);
        }
        if (has("pom.xml") || has("build.gradle") || has("build.gradle.kts"))
        {
            sdks["java"] = "21";
        }
        if (has("go.mod"))
        {
            sdks["go"] = GoVersion(repoDir);
        }

        return sdks;
    }

    /// <summary>Parse de <c>openhandsnet.yaml</c> — null quando inválido.</summary>
    public static BlueprintSpec? Parse(string yaml)
    {
        try
        {
            var root = new DeserializerBuilder()
                .Build()
                .Deserialize<Dictionary<object, object>>(yaml);
            if (root is null)
            {
                return null;
            }

            string? image = Str(root, "image");
            var setup = Str(root, "setup") ?? ".openhands/setup.sh";
            var sdks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetValue("sdk", out var sdkList) && sdkList is List<object> list)
            {
                foreach (var item in list.OfType<object>())
                {
                    var s = item?.ToString() ?? "";
                    var colon = s.IndexOf(':');
                    if (colon > 0)
                    {
                        sdks[s[..colon].Trim()] = s[(colon + 1)..].Trim();
                    }
                    else if (s.Trim().Length > 0)
                    {
                        sdks[s.Trim()] = "";
                    }
                }
            }

            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetValue("env", out var envMap) &&
                envMap is Dictionary<object, object> envDict)
            {
                foreach (var (k, v) in envDict)
                {
                    env[k?.ToString() ?? ""] = v?.ToString() ?? "";
                }
            }

            return new BlueprintSpec(image, sdks, env, setup);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }

    /// <summary>Blueprint da org: <c>settings.blueprint</c> = YAML inline.</summary>
    private async Task<string?> OrgBlueprintAsync(Guid orgId, CancellationToken ct)
    {
        var org = await db.Organizations
            .Where(o => o.Id == orgId)
            .Select(o => o.SettingsJson)
            .FirstOrDefaultAsync(ct);
        if (org is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(org);
            return doc.RootElement.TryGetProperty("blueprint", out var b)
                && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task WriteEnvAsync(
        Domain.Sandbox.Sandbox sandbox,
        IReadOnlyDictionary<string, string> env,
        ISandboxExecutor executor, StringBuilder log, CancellationToken ct)
    {
        var exports = string.Join("\n",
            env.Select(kv => $"export {kv.Key}={ShellQuote(kv.Value)}"));
        // profile.d cobre shells de login; fallback ~/.bashrc quando sudo off.
        var cmd = $"printf '%s' {ShellQuote(exports + "\n")} | " +
            "(sudo -n tee /etc/profile.d/ohn-blueprint.sh >/dev/null 2>&1 || " +
            "cat >> ~/.bashrc)";
        var r = await executor.ExecAsync(sandbox, cmd,
            TimeSpan.FromSeconds(30), ct);
        Append(log, "$ write blueprint env", r);
    }

    private string RepoDir(Guid conversationId) =>
        Path.Combine(workspaces.PreparePath(conversationId), "repo");

    private static string? ReadIfExists(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;

    private static string? Str(Dictionary<object, object> root, string key) =>
        root.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

    private static void Append(StringBuilder log, string header, CommandResult r)
    {
        log.AppendLine(header);
        if (r.StdOut.Length > 0)
        {
            log.AppendLine(r.StdOut);
        }
        if (r.StdErr.Length > 0)
        {
            log.AppendLine(r.StdErr);
        }
        if (r.TimedOut)
        {
            log.AppendLine("[timed out]");
        }
    }

    private static string Tail(string log) =>
        log.Length <= LogTailBytes ? log : log[^LogTailBytes..];

    private static string DotnetVersion(string repoDir)
    {
        var gj = Path.Combine(repoDir, "global.json");
        if (File.Exists(gj))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(gj));
                if (doc.RootElement.TryGetProperty("sdk", out var sdk) &&
                    sdk.TryGetProperty("version", out var v))
                {
                    var s = v.GetString();
                    return s is null ? "10" : s.Split('.')[0];
                }
            }
            catch (JsonException) { /* fallback LTS */ }
        }
        return "10";
    }

    private static string NodeVersion(string repoDir)
    {
        var nvmrc = Path.Combine(repoDir, ".nvmrc");
        if (File.Exists(nvmrc))
        {
            var v = File.ReadAllText(nvmrc).Trim().TrimStart('v');
            return v.Length > 0 ? v.Split('.')[0] : "22";
        }
        return "22";
    }

    private static string PythonVersion(string repoDir)
    {
        var pyproject = Path.Combine(repoDir, "pyproject.toml");
        if (File.Exists(pyproject))
        {
            var line = File.ReadLines(pyproject).FirstOrDefault(
                l => l.Contains("requires-python", StringComparison.Ordinal));
            if (line is not null)
            {
                var digits = new string(line.SkipWhile(c => !char.IsDigit(c))
                    .TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
                if (digits.Length > 0)
                {
                    return digits;
                }
            }
        }
        return "3.13";
    }

    private static string GoVersion(string repoDir)
    {
        var gomod = Path.Combine(repoDir, "go.mod");
        if (File.Exists(gomod))
        {
            var line = File.ReadLines(gomod).FirstOrDefault(
                l => l.StartsWith("go ", StringComparison.Ordinal));
            if (line is not null)
            {
                return line[3..].Trim();
            }
        }
        return "";
    }
}
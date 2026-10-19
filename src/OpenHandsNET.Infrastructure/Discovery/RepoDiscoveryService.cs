using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Discovery;
using OpenHandsNET.Application.Sandbox;

using RuntimeSandbox = OpenHandsNET.Domain.Sandbox.Sandbox;

namespace OpenHandsNET.Infrastructure.Discovery;

/// <summary>
/// SPEC-25 RF-001: probes conventional repo-context paths inside the
/// sandbox — instruction files (<c>AGENTS.md</c>…), repo skills
/// (<c>.openhands/skills/*/SKILL.md</c>…), repo config, README.
/// Best-effort: any probe failure yields an empty manifest, never fails
/// the start-task.
/// </summary>
public sealed class RepoDiscoveryService(ILogger<RepoDiscoveryService> logger)
    : IRepoDiscoveryService
{
    /// <summary>Repo-relative probe (cwd is /workspace/repo when present).</summary>
    private const string ProbeScript = """
        cd /workspace/repo 2>/dev/null || cd /workspace || exit 0
        for f in AGENTS.md CLAUDE.md CONTRIBUTING.md; do
          [ -f "$f" ] && [ $(wc -c < "$f") -le 65536 ] && echo "INS:$f"
        done
        for d in .openhands/skills .devin/skills .agents/skills .claude/skills; do
          [ -d "$d" ] || continue
          [ $(du -sb "$d" 2>/dev/null | cut -f1 || echo 0) -gt 1048576 ] && continue
          for s in "$d"/*/SKILL.md; do
            [ -f "$s" ] && echo "SKILL:$s"
          done
        done
        [ -f openhandsnet.yaml ] && echo "CFG:openhandsnet.yaml"
        [ -f .openhands/config.yaml ] && echo "CFG:.openhands/config.yaml"
        ls README* 2>/dev/null | head -1 | sed 's/^/README:/'
        """;

    /// <inheritdoc />
    public async Task<RepoContextManifest> DiscoverAsync(
        ISandboxExecutor executor, RuntimeSandbox sandbox,
        CancellationToken ct)
    {
        try
        {
            var res = await executor.ExecAsync(
                sandbox, ProbeScript, TimeSpan.FromSeconds(20), ct);
            if (res.ExitCode != 0)
            {
                return Empty();
            }

            var instructions = new List<string>();
            var skills = new List<RepoSkillRef>();
            var config = false;
            string? readme = null;
            foreach (var line in res.StdOut.Split('\n',
                StringSplitOptions.RemoveEmptyEntries))
            {
                var sep = line.IndexOf(':');
                if (sep <= 0)
                {
                    continue;
                }

                var kind = line[..sep];
                var value = line[(sep + 1)..].Trim();
                switch (kind)
                {
                    case "INS":
                        instructions.Add(value);
                        break;
                    case "SKILL":
                        var dir = Path.GetDirectoryName(value)!
                            .Replace('\\', '/');
                        var name = Path.GetFileName(dir);
                        skills.Add(new RepoSkillRef(name, value, null));
                        break;
                    case "CFG":
                        config = true;
                        break;
                    case "README":
                        readme ??= value;
                        break;
                }
            }

            // Descrição da skill vem do frontmatter (name/description) —
            // leitura lazy no RepoSkillProvider; aqui só o manifesto.
            return new RepoContextManifest(
                instructions, skills, config, readme);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "repo discovery falhou para sandbox {Id} — manifesto vazio",
                sandbox.Id);
            return Empty();
        }
    }

    private static RepoContextManifest Empty() => new([], [], false, null);
}
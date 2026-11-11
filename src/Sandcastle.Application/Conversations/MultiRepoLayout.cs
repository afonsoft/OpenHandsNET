namespace Sandcastle.Application.Conversations;

/// <summary>
/// SPEC-146 RF-002: layout de montagem multi-repo dentro do sandbox —
/// cada repo clona em <c>/workspace/repos/{short-name}/</c> e o legado
/// <c>/workspace/repo</c> aponta (symlink) para o primário, preservando
/// tools/scripts que assumem o caminho único.
/// </summary>
public static class MultiRepoLayout
{
    /// <summary>Raiz dos clones multi-repo no sandbox.</summary>
    public const string ReposRoot = "/workspace/repos";

    /// <summary>Caminho legado (primário / single-repo).</summary>
    public const string LegacyRepoPath = "/workspace/repo";

    /// <summary>
    /// Nome curto do diretório de um repo — último segmento do path
    /// (<c>owner/repo</c> → <c>repo</c>; URL → basename sem <c>.git</c>),
    /// sanitizado para <c>[A-Za-z0-9._-]</c>. Nunca vazio.
    /// </summary>
    public static string ShortName(string repository)
    {
        var s = repository.Trim().TrimEnd('/');
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^4];
        }

        var last = s.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? "repo";
        var chars = last.Select(c =>
            char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-')
            .ToArray();
        var name = new string(chars).Trim('.');
        return name.Length == 0 ? "repo" : name;
    }

    /// <summary>
    /// Nomes de diretório determinísticos por posição na lista ordenada —
    /// colisões de short-name ganham sufixo <c>-{i}</c> (i = posição 1-based).
    /// </summary>
    public static IReadOnlyList<string> DirNames(IReadOnlyList<string> repos)
    {
        var result = new List<string>(repos.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < repos.Count; i++)
        {
            var name = ShortName(repos[i]);
            if (!used.Add(name))
            {
                name = $"{name}-{i + 1}";
                used.Add(name);
            }
            result.Add(name);
        }

        return result;
    }

    /// <summary>Caminho in-sandbox de um repo pelo índice na lista ordenada.</summary>
    public static string PathAt(IReadOnlyList<string> repos, int index) =>
        $"{ReposRoot}/{DirNames(repos)[index]}";

    /// <summary>
    /// Resolve o arg <c>repo</c> das tools (nome completo ou short-name,
    /// case-insensitive) para o repo canônico da lista; null/empty →
    /// primário. Retorna null quando não casa nenhum.
    /// </summary>
    public static string? ResolveRepo(
        IReadOnlyList<string> repositories, string? repoArg)
    {
        if (repositories.Count == 0)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(repoArg))
        {
            return repositories[0];
        }

        var arg = repoArg.Trim();
        var exact = repositories.FirstOrDefault(r =>
            string.Equals(r, arg, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var dirs = DirNames(repositories);
        for (var i = 0; i < repositories.Count; i++)
        {
            if (string.Equals(dirs[i], arg, StringComparison.OrdinalIgnoreCase))
            {
                return repositories[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Caminho real in-sandbox de <paramref name="repository"/> na conversa:
    /// single-repo/legado → <see cref="LegacyRepoPath"/>; multi-repo →
    /// <c>/workspace/repos/{short}</c> (o primário incluso — o symlink
    /// <c>/workspace/repo</c> é só o alias legado). Repos desconhecidos → null.
    /// </summary>
    public static string? PathFor(
        IReadOnlyList<string> repositories, string repository)
    {
        var idx = -1;
        for (var i = 0; i < repositories.Count; i++)
        {
            if (string.Equals(
                    repositories[i], repository, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
        {
            return null;
        }
        if (repositories.Count == 1)
        {
            return LegacyRepoPath;
        }

        return PathAt(repositories, idx);
    }

    /// <summary>Extrai o host de um repo (url absoluta ou <c>owner/repo</c>).</summary>
    public static string? TryRepoHost(string? repository)
    {
        if (string.IsNullOrEmpty(repository))
        {
            return null;
        }
        if (Uri.TryCreate(repository, UriKind.Absolute, out var uri))
        {
            // file:///… e afins têm host vazio → sem credencial por host.
            return uri.Host is { Length: > 0 } h ? h : null;
        }

        return repository.Count(c => c == '/') == 1
            && !repository.StartsWith('/')
            && !repository.StartsWith("file:", StringComparison.Ordinal)
                ? "github.com" : null;
    }
}
namespace Sandcastle.Client.Services;

/// <summary>
/// Renderização de unified diff compartilhada (SPEC-120, port OWUIC
/// GitDiffRender — DiffTab e cards de files_changed/git_commit).
/// </summary>
public static class GitDiffRender
{
    /// <summary>Quebra o unified diff em seções por arquivo (chave = path do lado b).</summary>
    public static Dictionary<string, string> SplitDiff(string diff)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var sb = new System.Text.StringBuilder();
        void Flush()
        {
            if (current is not null)
            {
                map[current] = sb.ToString();
            }
            sb.Clear();
        }

        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                var bIdx = line.LastIndexOf(" b/", StringComparison.Ordinal);
                current = bIdx >= 0 ? line[(bIdx + 3)..].Trim() : null;
            }
            if (current is not null)
            {
                sb.AppendLine(line);
            }
        }
        Flush();
        return map;
    }

    /// <summary>Stats (+/−) por seção de arquivo.</summary>
    public static (int Adds, int Dels) Stats(string fileDiff)
    {
        var adds = 0;
        var dels = 0;
        foreach (var line in fileDiff.Split('\n'))
        {
            if (line.StartsWith('+') && !line.StartsWith("+++"))
            {
                adds++;
            }
            else if (line.StartsWith('-') && !line.StartsWith("---"))
            {
                dels++;
            }
        }
        return (adds, dels);
    }

    /// <summary>Classe CSS da linha do diff (usa as classes .diff-line do app.css).</summary>
    public static string LineClass(string line) => line switch
    {
        { } l when l.StartsWith("+++") || l.StartsWith("---") => "hunk",
        { } l when l.StartsWith('+') => "add",
        { } l when l.StartsWith('-') => "del",
        { } l when l.StartsWith("@@") => "hunk",
        _ => "",
    };
}
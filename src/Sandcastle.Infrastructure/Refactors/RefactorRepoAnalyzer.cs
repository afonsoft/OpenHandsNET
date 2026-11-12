using System.Text.RegularExpressions;
using System.Xml.Linq;

using Sandcastle.Application.Refactors;
using Sandcastle.Infrastructure.Indexing;

namespace Sandcastle.Infrastructure.Refactors;

/// <summary>
/// SPEC-144 RF-001: repo-checkout analysis behind the planner — file
/// enumeration (same rules as the indexer), per-language import
/// extraction (C# <c>using</c> + project refs first; TS/JS relative
/// imports; Python module paths) and <c>verify_cmd</c> inference.
/// Produces the file→deps edge map consumed by
/// <see cref="RefactorBatchPlanner"/>; unsupported languages yield no
/// edges so the planner falls back to directory frontiers.
/// </summary>
public static partial class RefactorRepoAnalyzer
{
    /// <summary>Directories never scanned for imports/batches.</summary>
    private static readonly HashSet<string> IgnoredTopDirs = new(
        StringComparer.OrdinalIgnoreCase)
    { "bin", "obj", "node_modules", ".git", ".vs", ".idea", "dist",
      "build", "packages", "vendor", ".venv", "venv", "__pycache__" };

    /// <summary>
    /// Repo-relative ('/') indexable file list — the refactor batch
    /// universe. Mirrors <see cref="Scans.ScanPartitioner"/> scope.
    /// </summary>
    public static IReadOnlyList<string> EnumerateFiles(string repoRoot)
    {
        var files = new List<string>();
        foreach (var file in RepoIndexer.EnumerateRepoFiles(repoRoot))
        {
            var rel = Path.GetRelativePath(repoRoot, file)
                .Replace('\\', '/');
            var top = rel.Contains('/', StringComparison.Ordinal)
                ? rel[..rel.IndexOf('/', StringComparison.Ordinal)]
                : null;
            if (top is not null && IgnoredTopDirs.Contains(top))
            {
                continue;
            }

            var ext = Path.GetExtension(rel);
            if (RepoIndexer.AllowedExtensions.Contains(ext)
                || ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".sln", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(rel);
            }
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>
    /// Builds the file→deps edge map for the <c>import-graph</c>
    /// strategy: C# files edge to files whose declared namespace is
    /// imported (exact or ancestor match); <c>.csproj</c> edges to every
    /// file under each <c>ProjectReference</c>'s directory; TS/JS edges
    /// to resolved relative imports; Python edges to resolved module
    /// files. Only edges between files present in
    /// <paramref name="files"/> are returned.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>>
        BuildImportGraph(string repoRoot, IReadOnlyList<string> files)
    {
        var known = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = new Dictionary<string, HashSet<string>>(
            StringComparer.OrdinalIgnoreCase);

        // --- C#: namespace declarations → file; using → edge. ---
        var nsToFiles = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);
        var csFiles = files.Where(f =>
            f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var nsOf = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var f in csFiles)
        {
            var text = ReadSafe(Path.Combine(repoRoot, f));
            if (text is null)
            {
                continue;
            }

            var ns = NamespaceDecl().Match(text) is { } m && m.Success
                ? m.Groups[1].Value : null;
            if (ns is not null)
            {
                nsOf[f] = ns;
                if (!nsToFiles.TryGetValue(ns, out var list))
                {
                    nsToFiles[ns] = list = [];
                }
                list.Add(f);
            }
        }

        foreach (var f in csFiles)
        {
            var text = ReadSafe(Path.Combine(repoRoot, f));
            if (text is null)
            {
                continue;
            }

            foreach (Match u in UsingDecl().Matches(text))
            {
                var imported = u.Groups[1].Value;
                foreach (var (ns, targets) in nsToFiles)
                {
                    // exact match or the using is an ancestor namespace.
                    if (!ns.Equals(imported, StringComparison.OrdinalIgnoreCase)
                        && !ns.StartsWith(imported + ".",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var t in targets)
                    {
                        if (!t.Equals(f, StringComparison.OrdinalIgnoreCase))
                        {
                            AddEdge(edges, f, t);
                        }
                    }
                }
            }
        }

        // --- .csproj: ProjectReference → the referencing project (the
        // .csproj file itself + every source file under its dir) depends
        // on every file under the referenced project's directory.
        var projects = files.Where(f =>
            f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var proj in projects)
        {
            var text = ReadSafe(Path.Combine(repoRoot, proj));
            if (text is null)
            {
                continue;
            }

            var projDir = DirOf(proj);
            var refs = ProjectRefs(text);
            if (refs.Count == 0)
            {
                continue;
            }

            var sources = files.Where(f =>
                    f == proj
                    || (projDir.Length > 0
                        ? f.StartsWith(projDir + "/",
                            StringComparison.OrdinalIgnoreCase)
                        : !f.Contains('/')))
                .ToList();
            foreach (var refPath in refs)
            {
                var targetDir = NormalizeDir(projDir, refPath);
                if (targetDir is null || targetDir.Length == 0)
                {
                    continue;
                }

                var targets = files.Where(f =>
                    f.StartsWith(targetDir + "/",
                        StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var s in sources)
                {
                    foreach (var t in targets)
                    {
                        if (!t.Equals(s, StringComparison.OrdinalIgnoreCase))
                        {
                            AddEdge(edges, s, t);
                        }
                    }
                }
            }
        }

        // --- TS/JS: relative import/require → resolved file. ---
        foreach (var f in files.Where(f =>
            f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase)))
        {
            var text = ReadSafe(Path.Combine(repoRoot, f));
            if (text is null)
            {
                continue;
            }

            foreach (Match im in TsImport().Matches(text))
            {
                var resolved = ResolveJsImport(DirOf(f),
                    im.Groups[1].Value, known);
                if (resolved is not null
                    && !resolved.Equals(f, StringComparison.OrdinalIgnoreCase))
                {
                    AddEdge(edges, f, resolved);
                }
            }
        }

        // --- Python: import / from-import → resolved module file. ---
        foreach (var f in files.Where(f =>
            f.EndsWith(".py", StringComparison.OrdinalIgnoreCase)))
        {
            var text = ReadSafe(Path.Combine(repoRoot, f));
            if (text is null)
            {
                continue;
            }

            foreach (Match im in PyImport().Matches(text))
            {
                foreach (var resolved in ResolvePyImport(
                    DirOf(f), im.Groups["mod"].Value,
                    im.Groups["names"].Success
                        ? im.Groups["names"].Value : null,
                    known))
                {
                    if (!resolved.Equals(f, StringComparison.OrdinalIgnoreCase))
                    {
                        AddEdge(edges, f, resolved);
                    }
                }
            }
        }

        return edges.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyCollection<string>)kv.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Infers a build+test gate for the repo (first match wins): .NET →
    /// <c>dotnet build &amp;&amp; dotnet test</c>; Node → <c>npm test</c>;
    /// Python → <c>pytest</c>. Null when nothing recognized — the worker
    /// falls back to a documented no-op gate.
    /// </summary>
    public static string? InferVerifyCommand(string repoRoot)
    {
        if (Directory.EnumerateFiles(repoRoot, "*.sln",
                SearchOption.TopDirectoryOnly).Any()
            || Directory.EnumerateFiles(repoRoot, "*.slnx",
                SearchOption.TopDirectoryOnly).Any()
            || Directory.EnumerateFiles(repoRoot, "*.csproj",
                SearchOption.AllDirectories).Any())
        {
            return "dotnet build && dotnet test";
        }
        if (File.Exists(Path.Combine(repoRoot, "package.json")))
        {
            return "npm test";
        }
        if (File.Exists(Path.Combine(repoRoot, "pyproject.toml"))
            || File.Exists(Path.Combine(repoRoot, "requirements.txt")))
        {
            return "pytest";
        }
        return null;
    }

    /// <summary>
    /// Directory-frontier fallback: groups files by top-level directory
    /// (or "." for roots) — input to the planner when the import graph
    /// is empty or <c>strategy=directory</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>>
        DirectoryFrontiers(IReadOnlyList<string> files) =>
        files.GroupBy(RefactorBatchPlanner.TopDir,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyCollection<string>)g.ToList(),
                StringComparer.OrdinalIgnoreCase);

    private static void AddEdge(
        Dictionary<string, HashSet<string>> edges, string from, string to)
    {
        if (!edges.TryGetValue(from, out var set))
        {
            edges[from] = set = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
        }
        set.Add(to);
    }

    /// <summary>Directory of a repo-relative path ("" at root).</summary>
    internal static string DirOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    /// <summary>
    /// Resolves a <c>ProjectReference</c> path (relative to the project
    /// dir) to the repo-relative directory of the referenced project.
    /// Null when the reference escapes the repo root.
    /// </summary>
    internal static string? NormalizeDir(string baseDir, string refPath)
    {
        var combined = (baseDir.Length == 0 ? "" : baseDir + "/")
            + refPath.Replace('\\', '/');
        var parts = new List<string>();
        foreach (var seg in combined.Split('/',
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".")
            {
                continue;
            }
            if (seg == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(seg);
        }
        if (parts.Count == 0)
        {
            return null;
        }
        // refPath points at the .csproj — drop the file name.
        parts.RemoveAt(parts.Count - 1);
        return string.Join('/', parts);
    }

    /// <summary>ProjectReference Include paths from a .csproj body.</summary>
    internal static IReadOnlyList<string> ProjectRefs(string csprojXml)
    {
        try
        {
            var doc = XDocument.Parse(csprojXml);
            return doc.Descendants("ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim())
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    /// <summary>Resolves a TS/JS relative import to a repo file.</summary>
    internal static string? ResolveJsImport(
        string baseDir, string specifier, HashSet<string> known)
    {
        if (!specifier.StartsWith('.'))
        {
            return null; // package import — not a repo edge
        }

        var combined = (baseDir.Length == 0 ? "" : baseDir + "/")
            + specifier;
        var parts = new List<string>();
        foreach (var seg in combined.Split('/',
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".")
            {
                continue;
            }
            if (seg == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(seg);
        }
        var stem = string.Join('/', parts);
        string[] candidates =
        [
            stem,
            stem + ".ts", stem + ".tsx", stem + ".js", stem + ".jsx",
            stem + ".d.ts",
            stem + "/index.ts", stem + "/index.tsx",
            stem + "/index.js", stem + "/index.jsx",
        ];
        return candidates.FirstOrDefault(known.Contains);
    }

    /// <summary>
    /// Resolves a Python import to repo files: <c>import a.b</c> or
    /// <c>from a.b import x</c> → <c>a/b.py</c>, <c>a/b/__init__.py</c>;
    /// relative dots (<c>from . import x</c>) resolve from the file's
    /// dir. May yield two candidates (module + package).
    /// </summary>
    internal static IEnumerable<string> ResolvePyImport(
        string baseDir, string module, string? names,
        HashSet<string> known)
    {
        if (module.Length == 0)
        {
            yield break;
        }

        var dotted = 0;
        while (dotted < module.Length && module[dotted] == '.')
        {
            dotted++;
        }

        var baseParts = baseDir.Length == 0
            ? new List<string>()
            : baseDir.Split('/').ToList();
        for (var i = 1; i < dotted && baseParts.Count > 0; i++)
        {
            baseParts.RemoveAt(baseParts.Count - 1);
        }

        var rel = module[dotted..].Replace('.', '/');
        var stem = (baseParts.Count == 0 ? "" : string.Join('/', baseParts)
            + "/") + rel;
        var candidates = new List<string>
        {
            stem + ".py",
            stem + "/__init__.py",
        };
        // `from pkg.mod import name` may import a submodule file.
        if (names is { Length: > 0 })
        {
            foreach (var n in names.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (n.Length > 0 && char.IsLetterOrDigit(n[0]))
                {
                    candidates.Add(stem + "/" + n + ".py");
                }
            }
        }

        foreach (var c in candidates)
        {
            if (known.Contains(c))
            {
                yield return c;
            }
        }
    }

    private static string? ReadSafe(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>C# namespace declaration (block or file-scoped).</summary>
    [GeneratedRegex(
        @"^\s*namespace\s+([A-Za-z_][\w.]*)",
        RegexOptions.Multiline | RegexOptions.Compiled,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamespaceDecl();

    /// <summary>C# using directive (skips aliases/static usings' type part).</summary>
    [GeneratedRegex(
        @"^\s*(?:global\s+)?using\s+(?:static\s+)?([A-Za-z_][\w.]*)\s*;",
        RegexOptions.Multiline | RegexOptions.Compiled,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex UsingDecl();

    /// <summary>TS/JS relative module specifier.</summary>
    [GeneratedRegex(
        @"(?:import|export|require)\s*(?:[^'""]*?\sfrom\s+)?['""]([^'""]+)['""]",
        RegexOptions.Compiled,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex TsImport();

    /// <summary>
    /// Python import lines — named groups <c>mod</c> (module path,
    /// leading dots = relative) and <c>names</c> (from-import list).
    /// </summary>
    [GeneratedRegex(
        @"^\s*(?:from\s+(?<mod>[.\w]+)\s+import\s+(?<names>[\w*,\s]+)|import\s+(?<mod>[.\w]+))",
        RegexOptions.Multiline | RegexOptions.Compiled,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex PyImport();
}
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Indexing;
using Sandcastle.Application.Scm;
using Sandcastle.Application.Wiki;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Agent.SystemPrompt;
using Sandcastle.Infrastructure.Indexing;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Security;

namespace Sandcastle.Infrastructure.Wiki;

/// <summary>
/// <see cref="IWikiRunner"/> (SPEC-145 RF-002): batch runner (mesmo
/// padrão do <c>ReviewRunner</c> — job próprio, não-conversa).
/// Pipeline: índice ready → lease do conteúdo → wiki.json → plano de
/// tópicos por esforço → LLM por página (JSON) → validação de citações
/// contra o índice (inválida → <c>stale</c>) → persistência + usage.
/// </summary>
public sealed class WikiRunner( // NOSONAR S107 — DI ctor (bundle)
    AppDbContext db,
    IRepoContentProvider contentProvider,
    IAgentLlmProvider llm,
    IModelRouter router,
    IOptions<WikiOptions> options,
    IUsageMeter usage,
    ILogger<WikiRunner> logger) : IWikiRunner
{
    /// <summary>Arquivos de nome suspeito de conter secrets — nunca entram na wiki.</summary>
    private static readonly Regex SecretFilePattern = new(
        @"(^|/)(\.env(\..+)?|.*\.(pem|p12|pfx|key)|id_rsa.*|.*(secret|credential)s?\.(json|ya?ml|env|ini|txt))$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>Marcadores de secret no conteúdo do arquivo.</summary>
    private static readonly Regex SecretContentPattern = new(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----|AKIA[0-9A-Z]{16}|\{\{secret:",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <inheritdoc />
    public async Task RunAsync(Guid wikiRunId, CancellationToken ct)
    {
        var run = await db.WikiRuns.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == wikiRunId, ct);
        if (run is null || run.Status != WikiRunStatuses.Queued)
        {
            return; // só queued executa — entrada duplicada/terminal é no-op
        }

        // Claim atômico queued→running: o reconcile de boot pode re-
        // enfileirar um run que também já estava no canal — só o
        // primeiro claim executa (idempotente contra double-enqueue).
        var claimed = await db.WikiRuns.IgnoreQueryFilters()
            .Where(r => r.Id == run.Id
                && r.Status == WikiRunStatuses.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, WikiRunStatuses.Running), ct);
        if (claimed == 0)
        {
            return;
        }
        run.Status = WikiRunStatuses.Running;
        try
        {
            await ExecuteAsync(run, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e,
                "wiki run {Id} failed ({Repo}@{Branch})",
                run.Id, run.Repository, run.Branch);
            await FinishAsync(run, WikiRunStatuses.Failed,
                SecretRedactor.Redact(e.Message), ct);
        }
    }

    private async Task ExecuteAsync(WikiRunRecord run, CancellationToken ct)
    {
        var o = options.Value;

        // 1) índice pronto — fonte da validação de citações.
        var index = await db.RepoIndexes.IgnoreQueryFilters()
            .Where(r => r.Repository == run.Repository
                && r.Branch == run.Branch
                && r.OrgId == run.OrgId
                && r.Status == "ready")
            .OrderByDescending(r => r.UpdatedAtTicks)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                $"repo index not ready for '{run.Repository}@{run.Branch}'");
        run.CommitSha ??= index.CommitSha;

        // 2) conteúdo do repo (clone shallow/local) + wiki.json.
        await using var lease = await contentProvider.AcquireAsync(
            new RepoContentRequest(run.Repository, run.Branch, run.OrgId), ct)
            ?? throw new InvalidOperationException(
                $"no content provider can serve '{run.Repository}'");
        run.CommitSha = lease.CommitSha ?? run.CommitSha;

        var config = await WikiRepoConfigParser.ReadAsync(lease.RootPath, ct);
        run.AutoUpdate = config.AutoUpdate;
        var effort = WikiEfforts.Normalize(
            string.IsNullOrEmpty(run.Effort) ? config.Effort : run.Effort);
        run.Effort = effort;

        // 3) arquivos candidatos — extensões permitidas + include/exclude
        //    do wiki.json + corte de arquivos com cheiro de secret.
        var files = RepoIndexer.EnumerateRepoFiles(lease.RootPath)
            .Select(f => Path.GetRelativePath(lease.RootPath, f)
                .Replace('\\', '/'))
            .Where(p => RepoIndexer.AllowedExtensions.Contains(
                Path.GetExtension(p)))
            .Where(p => !SecretFilePattern.IsMatch(p))
            .Where(p => Included(p, config))
            .Where(p => !Excluded(p, config))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        // 4) paths indexados (validação de citações é contra o ÍNDICE).
        var indexedPaths = (await db.CodeUnits.IgnoreQueryFilters()
                .Where(u => u.RepoIndexId == index.Id)
                .Select(u => u.Path).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var connection = await RepoConnectionLookup.FindCoveringAsync(
            db, run.Repository, run.OrgId, ct);

        // 5) plano de tópicos por esforço.
        var plan = PlanTopics(effort, config, files, o);
        var lineCounts = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        int? LineCountOf(string path)
        {
            if (lineCounts.TryGetValue(path, out var n))
            {
                return n;
            }
            var full = Path.Combine(lease.RootPath,
                path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                return null;
            }
            try
            {
                n = File.ReadLines(full).Count();
                lineCounts[path] = n;
                return n;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // 6) gera página por tópico.
        // SPEC-149: geração de páginas usa a role summarizer com org.
        var model = (await router.ResolveAsync(LlmRole.Summarizer,
            new LlmRouteContext(OrgId: run.OrgId,
                PreferredModel: o.Model), ct))?.Model
            ?? throw new LlmNoRouteException(
                "summarizer", "no eligible candidate");
        var pages = new List<WikiPageRecord>();
        var invalidTotal = 0;
        var promptTokens = 0L;
        var completionTokens = 0L;
        decimal? costUsd = 0m;
        var order = 0;
        foreach (var topic in plan)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            var sources = SelectSources(topic, files, config,
                o.MaxSourceFilesPerPage);
            var prompt = BuildPrompt(run, config, topic, files, sources,
                lease.RootPath, o);
            var (text, u) = await CompleteAsync(prompt, model, o, ct);
            if (u is not null)
            {
                promptTokens += u.PromptTokens;
                completionTokens += u.CompletionTokens;
                costUsd = (costUsd ?? 0) + (u.CostUsd ?? 0);
            }

            var parsed = ParsePage(text) ?? new GeneratedPage(
                TopicTitleOf(topic), text.Trim(), null);
            var declaredSources = parsed.SourceFiles is { Count: > 0 } sf
                ? sf.Where(indexedPaths.Contains).Take(30).ToList()
                : sources.Take(15).ToList();

            var (content, invalid) = WikiCitationValidator.ValidateAndRewrite(
                SecretRedactor.Redact(parsed.ContentMd),
                indexedPaths, LineCountOf, connection,
                run.Repository, run.CommitSha);
            invalidTotal += invalid.Count;

            var hash = ContentHashOf(declaredSources, lease.RootPath, content);
            pages.Add(new WikiPageRecord
            {
                Id = Guid.NewGuid(),
                OrgId = run.OrgId,
                WikiRunId = run.Id,
                Repository = run.Repository,
                Branch = run.Branch,
                Topic = topic.Topic,
                PathKey = topic.PathKey,
                Title = string.IsNullOrWhiteSpace(parsed.Title)
                    ? TopicTitleOf(topic) : parsed.Title.Trim(),
                ContentMd = content,
                SourceFilesJson = JsonSerializer.Serialize(declaredSources),
                ContentHash = hash,
                // guardrail: citação inválida → página nasce stale.
                Stale = invalid.Count > 0,
                SortOrder = order++,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        // 7) substitui as páginas do run anterior "current" (histórico
        //    mantido — leitura sempre pelo run completed mais recente).
        db.WikiPages.AddRange(pages);

        // 8) usage metering (mesma forma do review — quota do org).
        if (run.OrgId is { } orgId && promptTokens + completionTokens > 0)
        {
            await usage.RecordTokensAsync(
                orgId, promptTokens + completionTokens, costUsd, ct);
        }

        run.StatsJson = JsonSerializer.Serialize(new
        {
            pages_generated = pages.Count,
            pages_stale = pages.Count(p => p.Stale),
            invalid_citations = invalidTotal,
            source_files = files.Count,
            effort = run.Effort,
            model,
            prompt_tokens = promptTokens,
            completion_tokens = completionTokens,
        });
        run.FinishedAt = DateTimeOffset.UtcNow;
        await FinishAsync(run, WikiRunStatuses.Completed, null, ct);
    }

    /// <summary>
    /// Plano de tópicos (SPEC-145 RF-002): base fixa +
    /// <c>modules/{dir}</c> + topics_hint — preenchido até o alvo do
    /// esforço (<see cref="WikiOptions.EffortPages"/>).
    /// </summary>
    internal static List<(string Topic, string PathKey, string Title)>
        PlanTopics(string effort, WikiRepoConfig config,
            IReadOnlyList<string> files, WikiOptions o)
    {
        var target = o.EffortPages.TryGetValue(effort, out var n)
            ? Math.Max(1, n) : 15;
        var plan = new List<(string Topic, string PathKey, string Title)>
        {
            ("overview", "overview", "Overview"),
            ("architecture", "architecture", "Architecture"),
        };
        var tail = new List<(string Topic, string PathKey, string Title)>
        {
            ("data-flow", "data-flow", "Data flow"),
            ("deployment", "deployment", "Deployment"),
            ("conventions", "conventions", "Conventions"),
        };

        // Módulos = top-level dirs com ≥1 arquivo indexável.
        var moduleDirs = files
            .Where(f => f.Contains('/', StringComparison.Ordinal))
            .Select(f => f.Split('/')[0])
            .Where(d => !d.StartsWith('.')
                && d is not ("bin" or "obj" or "node_modules"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
        var middle = moduleDirs
            .Select(d => ("modules", $"modules/{d}", $"Module: {d}"))
            .ToList<(string Topic, string PathKey, string Title)>();
        // topics_hint — páginas pedidas explicitamente pelo repo: entram
        // no plano antes dos módulos e do tail (prioridade sobre o padrão).
        var seen = new HashSet<string>(
            plan.Select(p => p.PathKey).Concat(tail.Select(t => t.PathKey))
                .Concat(middle.Select(t => t.PathKey)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var hint in config.TopicsHint ?? [])
        {
            var key = hint.Trim().Trim('/');
            if (key.Length == 0 || !seen.Add(key))
            {
                continue;
            }
            plan.Add((key.Split('/')[0], key, Titleize(key)));
        }

        var middleCap = Math.Max(0, target - plan.Count - tail.Count);
        plan.AddRange(middle.Take(middleCap));
        plan.AddRange(tail.Take(Math.Max(0, target - plan.Count)));
        return plan.Take(target).ToList();
    }

    /// <summary>Seleção de arquivos-fonte por tópico (heurística de path).</summary>
    internal static List<string> SelectSources(
        (string Topic, string PathKey, string Title) topic,
        IReadOnlyList<string> files, WikiRepoConfig config, int cap)
    {
        IEnumerable<string> picked = topic.Topic switch
        {
            "overview" => files.Where(IsOverviewFile),
            "architecture" => files.Where(f =>
                IsOverviewFile(f) || IsEntrypoint(f)),
            "data-flow" => files.Where(f =>
                ContainsAny(f, "domain", "data", "model", "entit",
                    "persist", "db", "store", "record", "dto", "schema")),
            "deployment" => files.Where(f =>
                ContainsAny(f, "dockerfile", "docker-compose", "compose",
                    "deploy", "k8s", "helm", "terraform", "workflow",
                    "pipeline")),
            "conventions" => files.Where(f =>
                f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                || IsRootConfig(f)),
            "modules" => files.Where(f => f.StartsWith(
                $"{topic.PathKey["modules/".Length..]}/",
                StringComparison.OrdinalIgnoreCase)),
            _ => files.Where(f => f.StartsWith(
                $"{topic.PathKey}/", StringComparison.OrdinalIgnoreCase)),
        };
        var list = picked.Take(cap).ToList();
        if (list.Count == 0 && files.Count > 0)
        {
            list = files.Take(Math.Min(cap, 10)).ToList();
        }
        return list;
    }

    private static bool IsOverviewFile(string p) =>
        !p.Contains('/')
        && (p.StartsWith("readme", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            || p is "package.json" or "Cargo.toml" or "go.mod"
                or "pyproject.toml" or "pom.xml" or "Gemfile"
                or "composer.json")
        || p.Equals("README.md", StringComparison.OrdinalIgnoreCase);

    private static bool IsEntrypoint(string p) =>
        p.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase)
        || p.EndsWith("Startup.cs", StringComparison.OrdinalIgnoreCase)
        || p.EndsWith("/main.go", StringComparison.OrdinalIgnoreCase)
        || p.EndsWith("main.py", StringComparison.OrdinalIgnoreCase)
        || p.EndsWith("index.ts", StringComparison.OrdinalIgnoreCase)
        || p.EndsWith("main.rs", StringComparison.OrdinalIgnoreCase);

    private static bool IsRootConfig(string p) =>
        !p.Contains('/')
        && (p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(".", StringComparison.Ordinal)
            || p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string p, params string[] terms) =>
        terms.Any(t => p.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static bool Included(string p, WikiRepoConfig config) =>
        config.Include is not { Count: > 0 } inc
        || inc.Any(g => ScmGlobMatch(g, p));

    private static bool Excluded(string p, WikiRepoConfig config) =>
        config.Exclude is { Count: > 0 } exc
        && exc.Any(g => ScmGlobMatch(g, p));

    /// <summary>Glob <c>*</c>-wildcard case-insensitive (<c>ScmGlob</c>).</summary>
    internal static bool ScmGlobMatch(string pattern, string value) =>
        ScmGlob.Match(pattern, value);

    /// <summary>Prompt da página — fontes envelopadas (SPEC-91).</summary>
    private static string BuildPrompt(
        WikiRunRecord run, WikiRepoConfig config,
        (string Topic, string PathKey, string Title) topic,
        IReadOnlyList<string> allFiles,
        IReadOnlyList<string> sources, string rootPath,
        WikiOptions o)
    {
        var src = $"wiki:{run.Repository}@{run.Branch}";
        var env = new UntrustedInput.Envelope(o.MaxTreeChars);
        var sb = new StringBuilder();
        sb.Append("Repository: ").Append(run.Repository)
            .Append(" (branch ").Append(run.Branch).Append(")\n");
        sb.Append("Page topic: ").Append(topic.PathKey)
            .Append(" — ").Append(topic.Title).Append('\n');
        if (config.Language is { } lang)
        {
            sb.Append("Output language: ").Append(lang).Append('\n');
        }
        sb.Append("\nRepository file list:\n")
            .Append(env.WrapField(
                string.Join('\n', allFiles.Take(400)), src, "file_list"))
            .Append("\n\nRelevant source files:\n");
        var budget = o.MaxSourceCharsPerPage;
        foreach (var f in sources)
        {
            if (budget <= 0)
            {
                break;
            }
            var full = Path.Combine(rootPath,
                f.Replace('/', Path.DirectorySeparatorChar));
            string? content;
            try
            {
                if (!File.Exists(full))
                {
                    continue;
                }
                content = File.ReadAllText(full);
            }
            catch (Exception)
            {
                continue;
            }
            if (SecretContentPattern.IsMatch(content))
            {
                continue; // arquivo com secret detectado — skipado.
            }
            var redacted = SecretRedactor.Redact(content);
            var chunk = redacted[..Math.Min(redacted.Length, budget)];
            sb.Append('\n').Append(
                UntrustedInput.Wrap(chunk, src, $"file:{f}"))
                .Append('\n');
            budget -= chunk.Length;
        }
        return sb.ToString();
    }

    /// <summary>JSON da página gerada — tolerante a fences/prosa em volta.</summary>
    internal static GeneratedPage? ParsePage(string text)
    {
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }
        try
        {
            var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var t)
                ? t.GetString() : null;
            var content = root.TryGetProperty("content_md", out var c)
                ? c.GetString()
                : root.TryGetProperty("content", out var c2)
                    ? c2.GetString() : null;
            var sources = root.TryGetProperty("source_files", out var s)
                && s.ValueKind == JsonValueKind.Array
                    ? s.EnumerateArray()
                        .Select(e => e.GetString())
                        .Where(e => !string.IsNullOrEmpty(e))
                        .Select(e => e!).ToList()
                    : null;
            return string.IsNullOrWhiteSpace(content)
                ? null
                : new GeneratedPage(title ?? "", content, sources);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Saída JSON do modelo por página.</summary>
    internal sealed record GeneratedPage(
        string Title, string ContentMd, IReadOnlyList<string>? SourceFiles);

    /// <summary>SHA-256 dos arquivos-fonte cobertos + conteúdo (diff-aware).</summary>
    internal static string ContentHashOf(
        IReadOnlyList<string> sources, string rootPath, string contentMd)
    {
        var sb = new StringBuilder(contentMd);
        foreach (var f in sources.OrderBy(s => s, StringComparer.Ordinal))
        {
            var full = Path.Combine(rootPath,
                f.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (File.Exists(full))
                {
                    sb.Append(f).Append(':')
                        .Append(File.ReadAllText(full)).Append('\n');
                }
            }
            catch (Exception)
            {
                // arquivo ilegível — hash cobre só o nome.
                sb.Append(f).Append('\n');
            }
        }
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    internal static string TopicTitleOf(
        (string Topic, string PathKey, string Title) t) => t.Title;

    internal static string Titleize(string key) =>
        string.Join(' ', key.Split('/', '-', '_')
            .Where(p => p.Length > 0)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    /// <summary>Uma completion — texto puro + usage do CompletionEnd.</summary>
    private async Task<(string Text, LlmUsage? Usage)> CompleteAsync(
        string prompt, string model, WikiOptions o, CancellationToken ct)
    {
        var system = EmbeddedPrompts.Get("wiki-page");
        var text = new StringBuilder();
        LlmUsage? usage = null;
        await foreach (var chunk in llm.ChatStreamAsync(
            [
                new LlmMessage("system", system),
                LlmMessage.User(prompt),
            ],
            [],
            new LlmOptions(
                Model: model,
                Temperature: 0.3,
                MaxTokens: o.MaxCompletionTokens),
            ct))
        {
            switch (chunk)
            {
                case TextDelta d:
                    text.Append(d.Text);
                    break;
                case CompletionEnd end:
                    usage = end.Usage;
                    break;
            }
        }
        return (text.ToString(), usage);
    }

    private async Task FinishAsync(
        WikiRunRecord run, string status, string? error,
        CancellationToken ct)
    {
        run.Status = status;
        run.Error = error;
        run.FinishedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
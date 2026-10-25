using System.Text.Json;

namespace OpenHandsNET.Eval;

/// <summary>
/// SPEC-39 RF-002/003: runner headless — cria conversa, espera READY,
/// roda setup, aguarda terminal, avalia asserts e produz
/// <c>result.json</c>. Suporta <c>--parallel</c> e modo <c>recorded</c>
/// (replay de evidências gravadas — rerun determinístico sem LLM).
/// </summary>
public sealed class EvalRunner(IEvalBackend backend, RecordingStore? recordings = null)
{
    /// <summary>
    /// Executa as tasks com paralelismo limitado; devolve resultados na
    /// ordem de entrada.
    /// </summary>
    public async Task<IReadOnlyList<EvalResult>> RunAsync(
        IReadOnlyList<EvalTask> tasks, EvalRunOptions options,
        Action<EvalResult>? onTaskDone = null, CancellationToken ct = default)
    {
        var results = new EvalResult?[tasks.Count];
        var gate = new SemaphoreSlim(Math.Max(1, options.Parallel));
        var workers = tasks.Select(async (task, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                results[i] = await RunTaskAsync(task, options, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results[i] = new EvalResult
                {
                    Name = task.Name,
                    ExecutionStatus = "ERROR",
                    Score = 0,
                    Pass = false,
                    Mode = options.Mode,
                    Error = ex.Message,
                };
            }
            finally
            {
                gate.Release();
            }

            onTaskDone?.Invoke(results[i]!);
        });

        await Task.WhenAll(workers);
        return results!;
    }

    /// <summary>Roda uma task: live (API) ou replay de recording.</summary>
    public async Task<EvalResult> RunTaskAsync(
        EvalTask task, EvalRunOptions options, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (options.Mode == "recorded")
        {
            var rec = recordings?.Load(task) is { } r ? r : null;
            if (rec is null)
            {
                return new EvalResult
                {
                    Name = task.Name,
                    ExecutionStatus = "NO_RECORDING",
                    Score = 0,
                    Pass = false,
                    Mode = "recorded",
                    Error = "no recording for task hash — run --mode live --record first",
                };
            }

            return await Score(task, rec.Evidence, sw.ElapsedMilliseconds,
                mode: "recorded", conversationId: rec.ConversationId);
        }

        var timeout = EvalParser.ParseTimeout(task.Timeout);
        string? convId = null;
        try
        {
            var taskId = await backend.CreateConversationAsync(
                task.Task, task.Repo, options.Model, ct);
            convId = await backend.WaitReadyAsync(taskId, ct);
            if (task.Setup.Count > 0)
            {
                await backend.RunSetupAsync(convId, task.Setup, ct);
            }

            var events = await backend.WatchAsync(convId, timeout, ct);
            var evidence = CollectEvidence(task, convId, events, ct);

            if (options.Record && recordings is not null)
            {
                await recordings.SaveAsync(task, convId,
                    await evidence, ct);
            }

            return await Score(task, await evidence, sw.ElapsedMilliseconds,
                mode: "live", conversationId: convId);
        }
        catch (TaskCanceledException)
        {
            return new EvalResult
            {
                Name = task.Name,
                ConversationId = convId,
                ExecutionStatus = "TIMEOUT",
                Score = 0,
                Pass = false,
                DurationMs = sw.ElapsedMilliseconds,
            };
        }
    }

    /// <summary>
    /// Coleta evidências pós-run: exec de cada assert exit_code + leitura
    /// de arquivos file_contains + transcript textual + tokens.
    /// </summary>
    private async Task<EvalEvidence> CollectEvidence(
        EvalTask task, string convId, IReadOnlyList<JsonElement> events,
        CancellationToken ct)
    {
        var (transcript, tokens, kinds) = SummarizeEvents(events);
        var (execResults, fileContents) =
            await CollectAssertArtifacts(task, convId, transcript, ct);

        return new EvalEvidence(
            transcript.ToString(), execResults, fileContents,
            kinds, tokens);
    }

    // Transcript textual (mensagens + saídas + observações) + token totals
    // + contagem por kind.
    private static (System.Text.StringBuilder Transcript, long Tokens,
        Dictionary<string, int> Kinds) SummarizeEvents(
            IReadOnlyList<JsonElement> events)
    {
        var transcript = new System.Text.StringBuilder();
        long tokens = 0;
        var kinds = new Dictionary<string, int>();
        foreach (var e in events)
        {
            var kind = e.TryGetProperty("kind", out var k)
                ? k.GetString() ?? "" : "";
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            tokens += TokenUsageOf(e, kind);
            AppendTranscript(e, transcript);
        }
        return (transcript, tokens, kinds);
    }

    private static long TokenUsageOf(JsonElement e, string kind)
    {
        if (kind is not "TokenEvent"
            || !e.TryGetProperty("prompt_tokens", out var pt)
            || pt.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }
        var total = pt.GetInt64();
        if (e.TryGetProperty("completion_tokens", out var ctok)
            && ctok.ValueKind == JsonValueKind.Number)
        {
            total += ctok.GetInt64();
        }
        return total;
    }

    private static void AppendTranscript(
        JsonElement e, System.Text.StringBuilder transcript)
    {
        if (e.TryGetProperty("message", out var m)
            && m.ValueKind == JsonValueKind.String)
        {
            transcript.AppendLine(m.GetString());
        }

        if (e.TryGetProperty("text", out var tx)
            && tx.ValueKind == JsonValueKind.String)
        {
            transcript.AppendLine(tx.GetString());
        }
    }

    private async Task<(Dictionary<string, int> Exec,
        Dictionary<string, string?> Files)> CollectAssertArtifacts(
            EvalTask task, string convId,
            System.Text.StringBuilder transcript, CancellationToken ct)
    {
        var execResults = new Dictionary<string, int>();
        var fileContents = new Dictionary<string, string?>();
        foreach (var a in task.Assert)
        {
            if (a.Type == "exit_code" && a.Cmd is { } cmd && !execResults.ContainsKey(cmd))
            {
                var (code, stdout) = await backend.ExecAsync(convId, cmd, ct);
                execResults[cmd] = code;
                transcript.AppendLine(stdout);
            }
            else if (a.Type == "file_contains" && a.Path is { } p
                && !fileContents.ContainsKey(p))
            {
                fileContents[p] = await backend.ReadFileAsync(convId, p, ct);
            }
        }
        return (execResults, fileContents);
    }

    private async Task<EvalResult> Score(
        EvalTask task, EvalEvidence ev, long durationMs,
        string mode, string? conversationId)
    {
        var asserts = new List<AssertResult>();
        foreach (var a in task.Assert)
        {
            asserts.Add(a.Type switch
            {
                "exit_code" => new AssertResult(a.Type,
                    ev.ExecResults.GetValueOrDefault(a.Cmd!, -1)
                        == (a.Expect ?? 0),
                    false, a.Weight,
                    $"cmd `{a.Cmd}` → exit {ev.ExecResults.GetValueOrDefault(a.Cmd!, -1)} (expect {a.Expect ?? 0})"),
                "file_contains" => ev.FileContents.GetValueOrDefault(a.Path!) is { } content
                    ? FileContainsResult(a, content)
                    : new AssertResult(a.Type, false, false, a.Weight,
                        $"file not found: {a.Path}"),
                "output_contains" => new AssertResult(a.Type,
                    ev.Transcript.Contains(a.Pattern!, StringComparison.Ordinal),
                    false, a.Weight,
                    ev.Transcript.Contains(a.Pattern!, StringComparison.Ordinal)
                        ? null : "pattern not in agent output"),
                "llm_judge" => await JudgeAsync(a, ev.Transcript),
                _ => new AssertResult(a.Type, false, true, a.Weight,
                    $"unknown type {a.Type}"),
            });
        }

        var score = Scorer.TaskScore(asserts);
        return new EvalResult
        {
            Name = task.Name,
            ConversationId = conversationId,
            ExecutionStatus = "FINISHED",
            Score = score,
            Pass = asserts.All(a => a.Pass || a.Skipped),
            Asserts = asserts,
            EventsSummary = ev.Kinds,
            Tokens = ev.Tokens,
            DurationMs = durationMs,
            Mode = mode,
        };
    }

    private async Task<AssertResult> JudgeAsync(
        EvalAssert a, string transcript)
    {
        var verdict = await backend.JudgeAsync(a.Rubric!, transcript, default);
        return verdict is { } v
            ? new AssertResult(a.Type, v.Score >= 0.5, false, a.Weight,
                $"{v.Score:F2} — {v.Rationale}")
            : new AssertResult(a.Type, false, true, a.Weight,
                "no judge configured (skipped, excluded from gate)");
    }

    private static AssertResult FileContainsResult(
        EvalAssert a, string content)
    {
        var found = content.Contains(
            a.Pattern!, StringComparison.Ordinal);
        return new AssertResult(a.Type, found, false, a.Weight,
            found ? null : $"pattern not found in {a.Path}");
    }
}

/// <summary>Opções de um <c>eval run</c>.</summary>
public sealed record EvalRunOptions(
    int Parallel = 1,
    string? Model = null,
    string Mode = "live",
    bool Record = false);

/// <summary>Evidências coletadas pós-run (gravadas em recorded mode).</summary>
public sealed record EvalEvidence(
    string Transcript,
    IReadOnlyDictionary<string, int> ExecResults,
    IReadOnlyDictionary<string, string?> FileContents,
    IReadOnlyDictionary<string, int> Kinds,
    long Tokens);
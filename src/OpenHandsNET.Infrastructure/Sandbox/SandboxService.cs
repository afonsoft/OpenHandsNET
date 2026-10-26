using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Sandboxing;

using DomainSandbox = OpenHandsNET.Domain.Sandbox.Sandbox;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>
/// <see cref="ISandboxService"/> — records/specs/credentials in EF
/// (Api-owned); container lifecycle delegated to <see cref="ISandboxEngine"/>
/// (<see cref="DockerSandboxEngine"/> local | <see cref="HttpSandboxEngine"/>
/// remote, SPEC-35). Engine-agnostic: the same record logic serves both
/// providers.
/// </summary>
public sealed class SandboxService(
    AppDbContext db,
    ISandboxEngine engine,
    IOptions<SandboxOptions> options,
    ILogger<SandboxService> logger,
    Git.GitCredentialProvider? gitCredentials = null,
    Microsoft.AspNetCore.DataProtection.IDataProtectionProvider? dp = null,
    SandboxAgentClient? agentClient = null,
    Application.Contracts.ISecretStore? secretStore = null) : ISandboxService
{
    /// <summary>
    /// Serializa o acesso ao <see cref="AppDbContext"/> scoped — leitores
    /// concorrentes (polling de <see cref="GetAsync"/>/background reconcile)
    /// não podem sobrepor operações EF no mesmo contexto.
    /// </summary>
    private readonly SemaphoreSlim _dbGate = new(1, 1);

    /// <summary>Protector da session key do agente in-container (SPEC-17).</summary>
    private Microsoft.AspNetCore.DataProtection.IDataProtector? SessionProtector =>
        dp?.CreateProtector("openhandsnet.sandbox.session");

    /// <summary>Gera ULID de session key, protege no record, devolve plaintext.</summary>
    private string? NewSessionKey(SandboxRecord record)
    {
        if (SessionProtector is null)
        {
            return null;
        }

        var key = Ulid.NewUlid().ToString();
        record.SessionKeyProtected = SessionProtector.Protect(key);
        return key;
    }

    /// <inheritdoc />
    public async Task<DomainSandbox> StartAsync(
        Guid specId, Guid? conversationId, CancellationToken ct)
    {
        using var span = Telemetry.OpenNetTelemetry.StartSpan(
            "sandbox_op", conversationId);
        span?.SetTag("op", "start");
        var spec = await GetSpecAsync(specId, ct)
            ?? throw new InvalidOperationException($"Sandbox spec {specId} not found");
        spec = await ApplyProfileAsync(spec, conversationId, ct);
        spec = await ApplyBlueprintOverridesAsync(spec, conversationId, ct);

        var record = new SandboxRecord
        {
            Id = Guid.NewGuid(),
            SpecId = specId,
            ConversationId = conversationId,
            Status = SandboxStatus.Starting.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(spec.TimeoutMinutes),
        };
        db.Sandboxes.Add(record);
        await db.SaveChangesAsync(ct);

        try
        {
            // Warm-pool claim: só para spec default (specId vazio) — specs
            // customizadas têm imagem/recursos próprios e não cabem no pool.
            // SPEC-19 RF-005: em modo allowlist o claim é impossível — o
            // engine devolve null → cold start.
            var name = $"ohn-sandbox-{record.Id:N}";
            var claim = specId == Guid.Empty && spec.MountWorkspace
                && conversationId is not null
                ? await engine.ClaimWarmAsync(
                    new WarmClaimRequest(spec.Image, name, conversationId), ct)
                : null;
            if (claim is not null)
            {
                record.ContainerId = claim.ContainerId;
                record.Pool = "warm";
                record.PoolKey = spec.Image;
                // SPEC-17 RF-007: container warm nasce dormant — claim ativa
                // via POST /api/init (init key veio na claim).
                await ActivateWarmAsync(record, claim, ct);
                record.Status = SandboxStatus.Running.ToString();
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "Sandbox {SandboxId} attached warm container {ContainerId}",
                    record.Id, claim.ContainerId[..12]);
                return record.ToDomain();
            }

            var env = new List<string>(
                spec.Env?.Select(kv => $"{kv.Key}={kv.Value}") ?? []);
            env.AddRange(await ResolveCredentialEnvAsync(conversationId, ct));

            // SPEC-17: imagem-agente não leva Cmd (entrypoint é o agente);
            // session key vai por env e fica protegida no record.
            var isAgentImage = options.Value.IsAgentImage(spec.Image);
            if (isAgentImage)
            {
                var sessionKey = NewSessionKey(record);
                if (sessionKey is not null)
                {
                    env.Add($"OHN_SESSION_KEY={sessionKey}");
                    env.Add("OHN_DEFERRED_INIT=false");
                    env.Add("OHN_WORKSPACE=/workspace");

                    // SPEC-77 RF-001: VS Code web (code-server) sobe com o
                    // container por default — instalador lazy sob demanda.
                    // Blueprint pode desligar via env SANDBOX_VSCODE=0.
                    if (!env.Any(e => e.StartsWith("SANDBOX_VSCODE=",
                            StringComparison.Ordinal)))
                    {
                        env.Add("SANDBOX_VSCODE=1");
                    }

                    // SPEC-113 RF-002: desktop ao vivo (noVNC :6080) por
                    // default — instalador lazy sob demanda. Blueprint
                    // pode desligar via env SANDBOX_DESKTOP=0.
                    if (!env.Any(e => e.StartsWith("SANDBOX_DESKTOP=",
                            StringComparison.Ordinal)))
                    {
                        env.Add("SANDBOX_DESKTOP=1");
                    }
                }
            }

            var labels = new Dictionary<string, string>
            {
                [EngineLabels.SandboxId] = record.Id.ToString(),
            };
            if (conversationId is not null)
            {
                labels[EngineLabels.ConversationId] =
                    conversationId.Value.ToString();
            }

            var info = await engine.CreateAndStartAsync(new EngineCreateRequest(
                Name: name,
                Image: spec.Image,
                Env: env,
                Binds: [],
                Labels: labels,
                MemoryBytes: spec.MemoryMb * 1024L * 1024L,
                NanoCpus: (long)(spec.CpuLimit * 1_000_000_000L),
                PidsLimit: spec.PidsLimit,
                Cmd: isAgentImage ? null : ["sleep", "infinity"],
                WorkingDir: Domain.Sandbox.SandboxSpec.WorkspacePath,
                IsAgentImage: isAgentImage,
                WorkspaceConversationId: spec.MountWorkspace
                    ? conversationId : null), ct);

            record.ContainerId = info.ContainerId;
            record.AgentUrl = info.AgentUrl?.ToString();
            record.Status = SandboxStatus.Running.ToString();
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Sandbox {SandboxId} running (container {ContainerId}, image {Image}, profile {Profile})",
                record.Id, info.ContainerId[..12], spec.Image,
                spec.Profile ?? "custom");
        }
        catch (Exception ex)
        {
            record.Status = SandboxStatus.Error.ToString();
            record.LastError = ex.Message;
            await db.SaveChangesAsync(ct);
            throw;
        }

        return record.ToDomain();
    }

    /// <inheritdoc />
    public async Task<DomainSandbox> PauseAsync(Guid sandboxId, CancellationToken ct)
    {
        var record = await RequireAsync(sandboxId, ct);
        if (record.ContainerId is not null)
        {
            try
            {
                await engine.PauseAsync(record.ContainerId, ct);
                record.Status = SandboxStatus.Paused.ToString();
            }
            catch (SandboxEngineException ex)
            {
                // SPEC-16 NFR: cgroup pause indisponível (rootless/v1) →
                // fallback stop preserva fs/workspace (processos morrem).
                logger.LogWarning(ex,
                    "engine pause failed for {ContainerId} — falling back to stop",
                    record.ContainerId);
                await engine.StopAsync(record.ContainerId, ct);
                record.Status = SandboxStatus.Stopped.ToString();
            }
        }
        else
        {
            record.Status = SandboxStatus.Paused.ToString();
        }

        Telemetry.OpenNetTelemetry.SandboxSleeps.Inc();
        await db.SaveChangesAsync(ct);
        return record.ToDomain();
    }

    /// <inheritdoc />
    public async Task<DomainSandbox> StopAsync(Guid sandboxId, CancellationToken ct)
    {
        var record = await RequireAsync(sandboxId, ct);
        if (record.ContainerId is not null &&
            record.Status is nameof(SandboxStatus.Running) or nameof(SandboxStatus.Paused))
        {
            try
            {
                if (record.Status == nameof(SandboxStatus.Paused))
                {
                    await engine.UnpauseAsync(record.ContainerId, ct);
                }

                await engine.StopAsync(record.ContainerId, ct);
                record.Status = SandboxStatus.Stopped.ToString();
                Telemetry.OpenNetTelemetry.SandboxSleeps.Inc();
            }
            catch (SandboxEngineException ex) when (ex.NotFound)
            {
                record.Status = SandboxStatus.Missing.ToString();
            }
        }

        await db.SaveChangesAsync(ct);
        return record.ToDomain();
    }

    /// <inheritdoc />
    public async Task<DomainSandbox> ResumeAsync(Guid sandboxId, CancellationToken ct)
    {
        var record = await RequireAsync(sandboxId, ct);
        var wakeFrom = record.Status == nameof(SandboxStatus.Stopped)
            ? "stop" : "pause";
        if (record.ContainerId is null)
        {
            record.Status = SandboxStatus.Missing.ToString();
        }
        else if (record.Status == nameof(SandboxStatus.Stopped))
        {
            // SPEC-16 RF-003: start + probe antes de declarar Running.
            try
            {
                await engine.StartAsync(record.ContainerId, ct);
                var info = await engine.InspectAsync(record.ContainerId, ct)
                    ?? throw new SandboxEngineException(
                        "container gone after start", notFound: true);
                record.Status = info.State == "running"
                    ? SandboxStatus.Running.ToString()
                    : SandboxStatus.Error.ToString();
                if (info.AgentUrl is not null)
                {
                    record.AgentUrl = info.AgentUrl.ToString();
                }
            }
            catch (SandboxEngineException ex) when (ex.NotFound)
            {
                record.Status = SandboxStatus.Missing.ToString();
            }
            catch (SandboxEngineException ex)
            {
                record.Status = SandboxStatus.Error.ToString();
                record.LastError = ex.Message;
            }
        }
        else
        {
            try
            {
                await engine.UnpauseAsync(record.ContainerId, ct);
                record.Status = SandboxStatus.Running.ToString();
            }
            catch (SandboxEngineException ex) when (ex.NotFound)
            {
                record.Status = SandboxStatus.Missing.ToString();
            }
        }

        // SPEC-16 RF-004: wake é atividade — renova o lease do timeout watcher.
        if (record.Status == nameof(SandboxStatus.Running))
        {
            Telemetry.OpenNetTelemetry.SandboxWakes.WithLabels(wakeFrom).Inc();
            var spec = await GetSpecAsync(record.SpecId, ct);
            record.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(
                spec?.TimeoutMinutes ?? 60);
        }

        await db.SaveChangesAsync(ct);
        return record.ToDomain();
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid sandboxId, CancellationToken ct)
    {
        var record = await db.Sandboxes.FindAsync([sandboxId], ct);
        if (record is null)
        {
            return;
        }

        if (record.ContainerId is not null)
        {
            // SPEC-88: convId explícito — engines com workspace externo
            // (PVC k8s) liberam o volume junto com o sandbox.
            await engine.RemoveAsync(
                record.ContainerId, ct, record.ConversationId);
        }

        Telemetry.OpenNetTelemetry.SandboxSeconds
            .Inc((DateTimeOffset.UtcNow - record.CreatedAt).TotalSeconds);
        db.Sandboxes.Remove(record);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<DomainSandbox?> GetAsync(Guid sandboxId, CancellationToken ct)
    {
        await _dbGate.WaitAsync(ct);
        try
        {
            var record = await db.Sandboxes.FindAsync([sandboxId], ct);
            if (record is null)
            {
                return null;
            }

            await ReconcileAsync(record, ct);
            return record.ToDomain() with
            {
                // SPEC-38: service_urls emitidas conforme RoutingMode.
                ServiceUrls = SandboxUrlBuilder.Build(
                    record.ToDomain(), await GetSpecAsync(record.SpecId, ct),
                    options.Value),
            };
        }
        finally
        {
            _dbGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DomainSandbox>> SearchAsync(
        SandboxStatus? status, Guid? conversationId, CancellationToken ct)
    {
        var q = db.Sandboxes.AsQueryable();
        if (status is not null)
        {
            var s = status.Value.ToString();
            q = q.Where(r => r.Status == s);
        }

        if (conversationId is not null)
        {
            q = q.Where(r => r.ConversationId == conversationId);
        }

        await _dbGate.WaitAsync(ct);
        try
        {
            var rows = await q.OrderByDescending(r => r.CreatedAtTicks)
                .ToListAsync(ct);
            var result = new List<DomainSandbox>(rows.Count);
            foreach (var row in rows)
            {
                await ReconcileAsync(row, ct);
                result.Add(row.ToDomain() with
                {
                    ServiceUrls = SandboxUrlBuilder.Build(
                        row.ToDomain(), await GetSpecAsync(row.SpecId, ct),
                        options.Value),
                });
            }

            return result;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Domain.Sandbox.SandboxSpec> UpsertSpecAsync(
        Domain.Sandbox.SandboxSpec spec, CancellationToken ct)
    {
        var existing = await db.SandboxSpecs.FindAsync([spec.Id], ct);
        if (existing is null)
        {
            db.SandboxSpecs.Add(SandboxSpecRecord.From(spec));
        }
        else
        {
            var updated = SandboxSpecRecord.From(spec);
            db.Entry(existing).CurrentValues.SetValues(updated);
        }

        await db.SaveChangesAsync(ct);
        return spec;
    }

    /// <inheritdoc />
    public async Task<Domain.Sandbox.SandboxSpec?> GetSpecAsync(
        Guid specId, CancellationToken ct)
    {
        if (specId == Guid.Empty)
        {
            return new Domain.Sandbox.SandboxSpec(
                Guid.Empty, "default", options.Value.DefaultImage);
        }

        var record = await db.SandboxSpecs.FindAsync([specId], ct);
        return record?.ToDomain();
    }

    /// <inheritdoc />
    public async Task SweepOrphansAsync(CancellationToken ct)
    {
        var managed = await engine.ListManagedAsync(ct);
        var known = (await db.Sandboxes
            .Select(r => r.ContainerId)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var containerId in managed
            .Where(c => !known.Contains(c.ContainerId))
            .Select(c => c.ContainerId))
        {
            logger.LogWarning(
                "Removing orphan sandbox container {ContainerId}",
                containerId[..12]);
            await engine.RemoveAsync(containerId, ct);
        }
    }

    /// <summary>
    /// Profile resolution (SPEC-14 RF-001): spec.Profile → conversation →
    /// org settings → global default (default spec only). A resolved profile
    /// overrides the resource fields; custom specs keep their explicit
    /// values unless a profile is explicitly requested.
    /// </summary>
    private async Task<Domain.Sandbox.SandboxSpec> ApplyProfileAsync(
        Domain.Sandbox.SandboxSpec spec, Guid? conversationId, CancellationToken ct)
    {
        var name = spec.Profile;
        if (name is null && conversationId is not null)
        {
            var conv = await db.Conversations
                .AsNoTracking()
                .Where(c => c.Id == conversationId)
                .Select(c => new { c.SandboxProfile, c.OrgId })
                .FirstOrDefaultAsync(ct);
            name = conv?.SandboxProfile;
            if (name is null && conv?.OrgId is not null)
            {
                var orgJson = await db.Organizations
                    .AsNoTracking()
                    .Where(o => o.Id == conv.OrgId)
                    .Select(o => o.SettingsJson)
                    .FirstOrDefaultAsync(ct);
                name = ReadOrgSandboxProfile(orgJson);
            }
        }
        // Global default only shapes the built-in spec — a custom spec's
        // explicit resources are intentional and stay untouched.
        name ??= spec.Id == Guid.Empty ? options.Value.Profile : null;
        if (name is null)
        {
            return spec;
        }

        var profile = SandboxProfiles.Resolve(name);
        if (profile is null)
        {
            logger.LogWarning("Unknown sandbox profile '{Profile}' — keeping spec resources", name);
            return spec;
        }

        return spec with
        {
            MemoryMb = profile.MemoryMb,
            CpuLimit = profile.CpuLimit,
            PidsLimit = profile.PidsLimit,
            Profile = name,
        };
    }

    private static string? ReadOrgSandboxProfile(string? settingsJson)
    {
        if (settingsJson is null)
        {
            return null;
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(settingsJson);
            return doc.RootElement.TryGetProperty("sandbox_profile", out var p)
                && p.ValueKind == System.Text.Json.JsonValueKind.String
                    ? p.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// SPEC-17 RF-004: aplica <c>image</c>/<c>env</c> resolvidos pelo blueprint
    /// de um sandbox anterior da mesma conversa (repo persistido no workspace).
    /// </summary>
    private async Task<Domain.Sandbox.SandboxSpec> ApplyBlueprintOverridesAsync(
        Domain.Sandbox.SandboxSpec spec, Guid? conversationId,
        CancellationToken ct)
    {
        if (conversationId is null)
        {
            return spec;
        }

        var prior = await db.Sandboxes
            .Where(s => s.ConversationId == conversationId
                && (s.BlueprintImage != null || s.BlueprintEnvJson != null))
            .OrderByDescending(s => s.CreatedAtTicks)
            .Select(s => new { s.BlueprintImage, s.BlueprintEnvJson })
            .FirstOrDefaultAsync(ct);
        if (prior is null)
        {
            return spec;
        }

        var env = spec.Env is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(spec.Env);
        if (prior.BlueprintEnvJson is not null)
        {
            try
            {
                foreach (var kv in System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, string>>(
                        prior.BlueprintEnvJson) ?? [])
                {
                    env[kv.Key] = kv.Value;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // blueprint env corrompido — ignora
            }
        }

        return spec with
        {
            Image = prior.BlueprintImage ?? spec.Image,
            Env = env,
        };
    }

    /// <summary>
    /// SPEC-17 RF-007: ativa um container warm dormant — init key veio na
    /// claim, gera session key nova e chama <c>POST /api/init</c>.
    /// Falha/legado sem agente → segue em exec-fallback.
    /// </summary>
    private async Task ActivateWarmAsync(
        SandboxRecord record, EngineWarmClaim claim, CancellationToken ct)
    {
        if (agentClient is null || claim.InitKey is null)
        {
            return; // imagem legada no pool — sem agente, segue exec
        }

        try
        {
            var baseUrl = claim.AgentUrl;
            if (baseUrl is null && record.ContainerId is not null)
            {
                baseUrl = (await engine.InspectAsync(
                    record.ContainerId, ct))?.AgentUrl;
            }

            if (baseUrl is null)
            {
                return;
            }

            var sessionKey = NewSessionKey(record);
            if (sessionKey is null)
            {
                return;
            }

            var ok = await agentClient.InitAsync(baseUrl, claim.InitKey,
                sessionKey, Domain.Sandbox.SandboxSpec.WorkspacePath, null, ct);
            if (ok)
            {
                record.AgentUrl = baseUrl.ToString();
            }
            else
            {
                logger.LogWarning(
                    "warm container {ContainerId} /api/init failed — exec fallback",
                    record.ContainerId![..12]);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e,
                "warm activation failed for {ContainerId} — exec fallback",
                record.ContainerId?[..12]);
        }
    }

    /// <summary>
    /// SPEC-15 RF-004: injeta <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> (github.com) ou
    /// <c>GH_ENTERPRISE_TOKEN</c>+<c>GH_HOST</c> (GHES) no container quando o
    /// dono da conversa (ou o escopo <c>system</c>) tem credencial git para o
    /// host. Env do container = mesmo domínio de confiança do askpass.
    /// </summary>
    private async Task<List<string>> ResolveCredentialEnvAsync(
        Guid? conversationId, CancellationToken ct)
    {
        if (conversationId is null
            || (gitCredentials is null && secretStore is null))
        {
            return [];
        }

        var conv = await db.Conversations.AsNoTracking()
            .Where(c => c.Id == conversationId.Value)
            .Select(c => new ConvSecrets(
                c.CreatedByUserId,
                c.SelectedRepository,
                c.SecretNamesJson))
            .FirstOrDefaultAsync(ct);
        if (conv is null)
        {
            return [];
        }

        var env = new List<string>();
        await AppendSecretEnvAsync(env, conv, conversationId, ct);
        await AppendGitEnvAsync(env, conv, ct);
        return env;
    }

    private sealed record ConvSecrets(
        string? CreatedByUserId, string? SelectedRepository,
        string? SecretNamesJson);

    // SPEC-33 RF-003: secrets por referência — conv.secret_names_json
    // guarda NOMES; o valor só existe aqui (env do container).
    private async Task AppendSecretEnvAsync(
        List<string> env, ConvSecrets conv, Guid? conversationId,
        CancellationToken ct)
    {
        if (secretStore is null
            || string.IsNullOrEmpty(conv.SecretNamesJson)
            || string.IsNullOrEmpty(conv.CreatedByUserId))
        {
            return;
        }
        try
        {
            var names = System.Text.Json.JsonSerializer
                .Deserialize<List<string>>(conv.SecretNamesJson) ?? [];
            foreach (var name in names)
            {
                var value = await secretStore.ResolveAsync(
                    "user", conv.CreatedByUserId, name, ct);
                if (value is not null)
                {
                    env.Add($"{name}={value}");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Secret env resolution failed for conversation {Id}",
                conversationId);
        }
    }

    private async Task AppendGitEnvAsync(
        List<string> env, ConvSecrets conv, CancellationToken ct)
    {
        if (gitCredentials is null)
        {
            return;
        }

        var ghToken = await gitCredentials.ResolveTokenAsync(
            "github.com", conv.CreatedByUserId, ct);
        if (ghToken is not null)
        {
            env.Add($"GH_TOKEN={ghToken}");
            env.Add($"GITHUB_TOKEN={ghToken}");
        }

        var repoHost = TryRepoHost(conv.SelectedRepository);
        if (repoHost is not null &&
            !repoHost.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var entToken = await gitCredentials.ResolveTokenAsync(
                repoHost, conv.CreatedByUserId, ct);
            if (entToken is not null)
            {
                env.Add($"GH_ENTERPRISE_TOKEN={entToken}");
                env.Add($"GH_HOST={repoHost}");
            }
        }
    }

    /// <summary>Extrai o host de <c>selected_repository</c> (url ou owner/repo).</summary>
    private static string? TryRepoHost(string? selectedRepository)
    {
        if (string.IsNullOrEmpty(selectedRepository))
        {
            return null;
        }

        if (Uri.TryCreate(selectedRepository, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return selectedRepository.Count(c => c == '/') == 1
            && !selectedRepository.StartsWith('/')
            && !selectedRepository.StartsWith("file:", StringComparison.Ordinal)
            ? "github.com"
            : null;
    }

    private async Task<SandboxRecord> RequireAsync(Guid id, CancellationToken ct) =>
        await db.Sandboxes.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Sandbox {id} not found");

    private async Task ReconcileAsync(SandboxRecord record, CancellationToken ct)
    {
        if (record.Status is nameof(SandboxStatus.Error) or nameof(SandboxStatus.Missing)
            || record.ContainerId is null)
        {
            return;
        }

        var info = await engine.InspectAsync(record.ContainerId, ct);
        if (info is null)
        {
            record.Status = SandboxStatus.Missing.ToString();
            await db.SaveChangesAsync(ct);
            return;
        }

        // SPEC-16 RF-005: "exited"/"created" é o estado *esperado* de um
        // record Stopped (stop preserva o container); só é Error quando o
        // record dizia que estava vivo.
        var status = info.State switch
        {
            "running" => SandboxStatus.Running,
            "paused" => SandboxStatus.Paused,
            "created" => record.Status == nameof(SandboxStatus.Starting)
                ? SandboxStatus.Starting : SandboxStatus.Stopped,
            "exited" or "dead" => record.Status == nameof(SandboxStatus.Stopped)
                ? SandboxStatus.Stopped : SandboxStatus.Error,
            _ => SandboxStatus.Error,
        };
        if (status.ToString() != record.Status)
        {
            record.Status = status.ToString();
            if (status is SandboxStatus.Error)
            {
                record.LastError = info.Error;
            }

            await db.SaveChangesAsync(ct);
        }
    }
}
# SPEC — 15 · Mobile UX + Smart Autonomy + Credential Forwarding

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `ui-mobile-and-smart-autonomy` |
| Type | `Feature / UX / Security` |
| Stack | `.NET 10 · Blazor WASM · Tailwind v4 · Docker.DotNet · gh CLI` |
| Repository | `afonsoft/OpenHandsNET` (+ superfícies em `afonsoft/open-webui`) |
| Status | `Done` |
| Ticket | [#33](https://github.com/afonsoft/OpenHandsNET/issues/33) · afonsoft/open-webui#344 (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `03_runtime_sandbox_spec`, `04_rest_and_signalr_api_spec`, `09_agent_engine_spec`, `12_git_scm_integration_spec` |
| Consumed by | `05` (UI), `06` (CLI), `08` (secrets/RBAC) |

## 1. User Story

**As a** usuário do OpenHandsNET/open-webui no celular
**I want** acessar o painel de workspace por overlay sobre o chat, ver o nome do
repositório sem o owner, e que o agente siga autonomamente nos modos
smart/always-allow usando minhas credenciais GitHub dentro do sandbox
**So that** a experiência mobile seja usável de ponta a ponta e o agente execute
o fluxo completo (clone → código → `gh`/git → PR) sem ficar pedindo o próximo
passo nem parar por falta de credencial.

**Problem context:**
- No mobile o ícone do painel de workspace some (o header lota de chips e o
  botão sai de vista / área de toque insuficiente).
- O chip de repositório exibe `owner/repo`; em viewport `< sm` o owner consome o
  `max-w-16` e o nome do repo fica ilegível.
- Nos modos `smart` / `always-allow` o agente ainda interrompe o fluxo
  perguntando o próximo passo mesmo quando existe uma opção recomendada óbvia.
- O agente consegue clonar (askpass efêmero do servidor) mas **não consegue**
  `git push`/`gh pr create`/`gh` em geral dentro do sandbox: nenhuma credencial
  persistente é provisionada — caso real: commit+merge local feitos e o push
  falhou com `could not read Username for 'https://github.com'`.

## 2. Scope

**In scope:**

- **RF-001 — Ícone do workspace sempre acessível no mobile**
  (`open-webui`: `src/OpenWebUI.Client/Components/ChatView.razor`)
  - O botão do painel (`chat.panel_title`) permanece visível e alcançável em
    qualquer viewport: área de toque ≥ 40×40 px, nunca empurrado para fora da
    tela pelos chips de repo/branch (chips secundários colapsam antes).
  - Ao abrir no mobile (`< lg`), o painel renderiza como **overlay sobre o
    chat**: `fixed inset-0 z-40`, backdrop `bg-black/40`, sheet à direita
    `w-80 max-w-[85vw]` (já é o padrão do drawer existente — garantir que o
    botão o dispare em todo breakpoint).
  - Fechar (backdrop, botão X, `Escape`) recolhe o drawer e devolve o foco ao
    chat (focus-trap atual via `openwebui.trapFocus`/`releaseFocus` mantido).
- **RF-002 — Header de repositório sem owner no mobile**
  (`ChatView.razor`, chip `@repoSlug`)
  - `< sm`: renderiza só o nome do repo (`slug.Split('/')[^1]`); `≥ sm` mantém
    `owner/repo`. `href`, `title` e `aria-label` continuam com o slug completo.
  - Ícone GitHub do chip vira o botão de ação principal no mobile (toque abre
    o picker se `_repo` nulo; abre o repo externo quando setado).
- **RF-003 — Autonomia nos modos smart / always-allow**
  (`open-webui`: presets `_approvalPreset` + `RunQuestionAskedEvent`/
  `RunApprovalAskedEvent`; `OpenHandsNET`: `AgentRunner` +
  `conv.ConfirmationMode`)
  - Quando o preset for `smart` ou `always-allow` (equivale a
    `confirmation_mode=false` no OpenHandsNET), ao receber `question_asked` com
    opções ou `approval_asked` rotineiro, o cliente/engine seleciona
    automaticamente a **opção recomendada** (`IsRecommended=true`, senão índice
    0) e prossegue sem pausar.
  - A decisão automática é auditável: evento `decision_auto` no stream /
    `EventStream` V1 (`kind:"decision_auto"`, `source:"agent"`, payload com
    `asked_id`, `chosen`, `reason:"recommended_option"`).
  - **Guardrails**: nunca auto-aprova — operações destrutivas fora do sandbox,
    uso de credenciais, `allow_host_mcp`, escrita fora de `/workspace`,
    `git push --force`. Essas continuam exigindo resposta humana.
- **RF-004 — Repasse de credencial GitHub para o ambiente interno**
  (`OpenHandsNET`)
  - `GitCredentialProvider` vira **per-user**: segredos `git_credential`
    gravados com `OwnerId = userId` do `ICurrentUserAccessor`; resolução faz
    fallback `owner → "system"` (credencial compartilhada continua valendo).
    `PUT /api/v1/git/credentials/{host}` grava no usuário autenticado; `GET`
    lista as do usuário + as `system` (campo `owner`); `DELETE` só as próprias.
  - **Injeção de env na criação do sandbox** (`DockerSandboxService.StartAsync`):
    resolve o `CreatedByUserId` da conversa → token de `github.com` (ou do host
    do `selected_repository` quando for GHES) → injeta `GH_TOKEN` e
    `GITHUB_TOKEN` no `Env` do container (GHES: `GH_ENTERPRISE_TOKEN` +
    `GH_HOST`). Sem token → nenhum env, comportamento atual.
  - **Provisionamento pós-start** (`ConversationStartWorker` →
    `IGitService.ProvisionGhAuthAsync`): escreve
    `~/.config/gh/hosts.yml` (`oauth_token`, `git_protocol: https`, `0600`) e
    `git config --global credential.helper '!gh auth git-credential'` — assim
    `gh` autentica mesmo sem env e `git push/fetch` passa a usar o token.
    Executa tanto com `selected_repository` quanto sem ("inicializar uma
    conversa").
  - **Clone modo host** (`WorkspaceProvisioner.CloneRepositoryAsync`): recebe
    o token resolvido e passa `http.extraHeader` via `GIT_CONFIG_*` env vars do
    processo (nunca em argv/`ps`), com `GIT_TERMINAL_PROMPT=0`.
  - Pré-requisito de imagem: `git` + `gh` instalados na imagem de sandbox
    (usuário já provisionou na imagem custom; a spec `03` passa a listar isso
    como requisito recomendado). Se `gh` ausente, `credential.helper` aponta
    para script shim que lê `$GH_TOKEN`.

**Out of scope:** OAuth device-flow do GitHub dentro do app (tokens entram via
`PUT /git/credentials` ou integração futura), GitHub App/installations, PR
automation server-side, auto-aprovação de operações destrutivas, seleção
inteligente por LLM das opções (a escolha é determinística: recomendada → 0).

## 3. Technical Context

**Estado atual (evidência no código):**

- `ChatView.razor:669-683` — drawer mobile já existe (`fixed inset-0 z-40
  lg:hidden` + backdrop + sheet), mas o toggle (`:116`) compete com chips
  (`_parentChatId`, `_git`, `_repo`) que ocupam o header em `< sm`.
- `ChatView.razor:75` — `<span class="max-w-16 sm:max-w-44 truncate font-mono">
  @repoSlug</span>` exibe `owner/repo` inteiro; em `< sm` o `max-w-16` corta.
- `GitCredentialProvider.cs:36` — segredos sempre `OwnerId="system"`;
  `ResolveTokenAsync(host)` ignora usuário.
- `GitService.cs:44-53` — askpass efêmero cobre **só o clone**; após o `finally`
  nada autentica dentro do sandbox (gap do `git push`/`gh`).
- `DockerSandboxService.cs:73` — `Env` vem só de `spec.Env` (template), sem
  credenciais por usuário.
- `AgentRunner.cs:143` — `confirmationMode` booleano; `_confirms` TCS +
  `waiting_for_confirmation`; open-webui tem presets (`approve-mutations`…)
  e eventos `approval_asked`/`question_asked` com opções.

## 4. Design

### 4.1 Mobile drawer + header (open-webui)

```
header chips (< sm):  [≡menu] [title]            [⚙workspace-icon]
                      └─ repo chip = "repo" (sem owner, max-w menor)
                      └─ parent/git chips → hidden (já são sm:)
_toggle → _showPanel=true → overlay fixed inset-0 (chat continua montado)
_close  → _showPanel=false → releaseFocus → foco volta ao composer/chat
```

### 4.2 Autonomy ladder

```
preset           question_asked(options)   approval_asked(risky?)   default
─────────────────────────────────────────────────────────────────────────
ask-everything   pergunta                  pergunta                 pergunta
smart            auto: recommended|idx0    pergunta                 auto safe
always-allow     auto: recommended|idx0    auto (exceto destructive) auto
destructive*     —                         pergunta SEMPRE           —
* destrutivo: fora do sandbox, credenciais, force-push, rm -rf-like tools
```

### 4.3 Credential forwarding

```
POST /app-conversations ──► StartTask → worker
   PUT /git/credentials/{host}  (owner = userId; fallback "system")

worker: StartAsync(specId, convId)
  └─ DockerSandboxService: conv.CreatedByUserId → resolve github.com
     token? → Env += GH_TOKEN=… GITHUB_TOKEN=… (GHES: GH_ENTERPRISE_TOKEN + GH_HOST)
  └─ sandbox Running → ProvisionGhAuthAsync(sb, ownerId)
     ├─ ~/.config/gh/hosts.yml (oauth_token, 0600)   → gh auth
     └─ git config --global credential.helper '!gh auth git-credential' → git https
  └─ PREPARING_REPOSITORY → CloneAsync (container: askpass como hoje;
     host: http.extraHeader via GIT_CONFIG_* env)
```

Token nunca vai para argv, logs ou `.git/config`; vive em env do container e em
`hosts.yml` 0600 — sandbox é single-tenant por conversa (mesmo domínio de
confiança do askpass atual). `docker inspect` expõe env no host — aceito e
documentado (host daemon já é trust-boundary).

## 5. API Contract

- `PUT /api/v1/git/credentials/{host}` — body inalterado (`{token|ssh_key,
  kind}`); **semântica nova**: grava `OwnerId=<userId>` em vez de `system`.
- `GET /api/v1/git/credentials` — resposta passa a incluir `owner`
  (`"user" | "system"`).
- `DELETE /api/v1/git/credentials/{host}` — 404 se a credencial pertencer a
  outro usuário (ou for `system` sem permissão admin).
- `IGitService`:
  - `CloneAsync(sb, url, branch, ownerId, ct)` — novo parâmetro `ownerId`.
  - `ProvisionGhAuthAsync(sb, ownerId, ct)` — novo método.
- `ISandboxService.StartAsync` — assinatura inalterada (env resolvido
  internamente via `conversationId`).
- `decision_auto` (EventStream V1, `source:"agent"`) — divergência
  documentada: o modelo de pergunta do OpenHandsNET é booleano
  (`waiting_for_confirmation`), sem lista de opções; portanto
  `chosen` carrega `"approve"` e `reason` carrega `"always_allow"` em vez
  de `"recommended_option"`. Payload: `asked_id` (ActionEvent id),
  `chosen`, `reason`.

## 6. NFR

- **Segurança**: token nunca em logs (`SecretRedactor` mantido), nunca em argv
  persistente; `hosts.yml` 0600; env var padrão de ferramentas (`GH_TOKEN` é o
  nome oficial do gh CLI).
- **Compat**: credenciais `system` existentes continuam resolvendo (fallback).
- **A11y mobile**: drawer mantém `role="dialog"`, `aria-modal`, focus-trap,
  `Escape` fecha; ícone ≥ 40px.

## 7. Task Plan

1. `GitCredentialProvider`: owner-scoped put/resolve/delete/list + fallback
   `system`; endpoint usa `ICurrentUserAccessor`.
2. `DockerSandboxService`: injetar `GitCredentialProvider`, resolver owner da
   conversa, env `GH_TOKEN`/`GITHUB_TOKEN` (+GHES) no `CreateContainerParameters`.
3. `GitService`/`IGitService`: `CloneAsync` com `ownerId` + novo
   `ProvisionGhAuthAsync` (hosts.yml + credential.helper).
4. `ConversationStartWorker`: chamar provision pós-start e passar
   `conv.CreatedByUserId` ao clone; resolver token para host-mode clone.
5. `WorkspaceProvisioner`: `CloneRepositoryAsync` aceita token → `http.extraHeader`
   via `GIT_CONFIG_*` env.
6. (open-webui) `ChatView.razor`: RF-001/RF-002 — icon mobile + overlay +
   repo sem owner.
7. (open-webui + OpenHandsNET) RF-003 — auto-pick recommended + `decision_auto`.
8. Testes: provider owner-scope (unit), env injection (sandbox integration),
   provision idempotente, host-clone header, bUnit mobile drawer, a11y.

## 8. Deliverables

- `GitCredentialProvider` per-user + endpoints atualizados.
- Sandbox com `GH_TOKEN`/`GITHUB_TOKEN` e `gh`+git autenticados.
- Worker provisionando credenciais em toda conversa.
- UI mobile: ícone do painel acessível, overlay funcional, repo sem owner.
- Autonomia: `decision_auto` + auto-pick em smart/always-allow.
- SPEC-15 (este documento) + índice.

## 9. Referências

- SPEC-12 (`12_git_scm_integration_spec.md`) — credenciais/askpass atual.
- SPEC-14 (`14_enterprise_operations_spec.md`) — perfil sandbox/DinD.
- gh CLI docs — `GH_TOKEN`, `gh auth git-credential`, `~/.config/gh/hosts.yml`.
- Caso-raiz: agente interno completou spec+commit+merge local e falhou no
  `git push` por ausência de credencial GitHub no ambiente.

## 10. Guardrails

- Token GitHub nunca em logs/argv persistente/`docker history`.
- Destrutivo (fora-sandbox, credenciais, force-push) **sempre** pergunta.
- Fallback `system` preservado — sem quebra de credenciais existentes.
- `Env` do container com token só quando a conversa tem dono resolvido e token
  cadastrado — caso contrário comportamento idêntico ao atual.

## 11. DoD

- [ ] `PUT /git/credentials` grava por usuário; `GET` separa `user|system`;
      `DELETE` respeita owner.
- [ ] Sandbox nova (com token cadastrado) tem `GH_TOKEN`+`GITHUB_TOKEN` e
      `gh auth status` OK dentro do container; `git push`/`gh pr create`
      funcionam no repo clonado.
- [ ] Host-mode clone de repo privado autentica via `http.extraHeader`.
- [ ] Mobile (< sm): ícone do workspace visível/tocável; drawer overlay abre
      e fecha voltando ao chat; chip de repo mostra só o nome.
- [ ] Smart/always-allow: `question_asked` com opção recomendada resolve sem
      prompt e emite `decision_auto`; destrutivo continua perguntando.
- [ ] `dotnet build` + `dotnet test` verdes; a11y/i18n guards do open-webui OK.

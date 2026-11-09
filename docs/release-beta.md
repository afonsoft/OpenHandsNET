# Release checklist — `v0.1.0-beta.1`

Checklist executável para cortar a primeira tag beta (Epic #365 · SPECs
132–137). English abaixo de cada bloco quando o item é público.

## 1. Pré-requisitos (specs implementadas)

- [x] SPEC-132 auth-ux (#366) merged — `/auth` + user menu + sign-out
- [x] SPEC-133 web-search-tool (#367) merged — `web_search` builtin
- [x] SPEC-134 beta-byok (#368) merged — keys de LLM por usuário
- [x] SPEC-135 conversation-ux (#369) merged — fork + undo + hunk revert
- [x] SPEC-136 nav-visual-polish (#370) merged — nav órfã + fontes + ícones
- [x] SPEC-137 housekeeping (#371) merged — este checklist + dead code + docs

## 2. Qualidade (gates obrigatórios)

- [x] `dotnet format --verify-no-changes` limpo em `main`
- [x] `dotnet build OpenHandsNET.slnx -c Release` — 0 warnings / 0 errors
- [x] `dotnet test` — suítes verdes (Unit 742/742 · Client 133/133;
      integração gate por CI); flakes conhecidos documentados no
      corpo da tag (SPEC-78 quarantine list)
- [x] Golden OpenAPI (`Spec51OpenApiContractTests`) byte-idêntico —
      regerado na SPEC-135 (#384: fork/undo/revert-hunks)
- [x] SonarCloud gate verde na main — fix #387 (0 bugs/vulnerabilities novos)
- [x] Drift guards client: `tools/check-*.py` todos passando
- [x] `gitleaks`/GitGuardian limpos (fix #378 na main)

## 3. Quickstart em ambiente limpo (gate manual)

Em uma VM/runner sem estado dev (`git clone` fresco):

- [x] `cp .env.exemplo .env` + preencher só `OPENHANDSNET_AgentLlm__*`
      (provider/model/key) — verificado com gateway OmniRoute (Gemini)
- [x] `docker compose -f docker-compose.beta.yaml up -d --build` →
      app saudável em `:3032` (`/api/v1/ready` = 200)
- [x] Primeira conversa: criar → enviar mensagem → agente executa tool
      calls → `finish` (verificado: `hello.txt` criado+lido no workspace,
      2 conversas FINISHED com LLM real)
- [x] Desktop tab abre noVNC (verificado: xterm live no X do sandbox —
      exige `Sandbox__DefaultImage=openhandsnet/runtime:local`,
      `Sandbox__ControlNetwork=<rede do compose>` e `group_add` do gid docker)
- [ ] `Auth:Provider=none` → boot direto sem login (single-user local);
      quando `oidc` configurado → `/auth` + SSO + sign-out funcionam
      (none verificado; OIDC não exercitado — exige IdP)
- [x] PWA instalável (manifest + service worker ativos — verificado 200)

## 4. Segurança (defaults auditados)

- [x] `Auth:Provider=none` documentado como **single-user local** —
      callout "não exponha à internet sem `oidc`" em `.env.exemplo` +
      `docs/{,en/}installation/environment-variables.md`
- [x] `Auth:Provider=none` + exposto → IP rules default-on
      (SPEC-59, `EdgeAccessMiddleware` no pipeline) + rate limit ligado
      no `docker-compose.beta.yaml` (120/min)
- [x] Egress: `unrestricted` default documentado + link
      `docs/deployment/egress-allowlist.md` no `.env.exemplo`
- [x] `.env.exemplo` sem secrets reais; `POSTGRES_PASSWORD=CHANGE_ME`
- [x] `docker-compose.beta.yaml` comentário do trade-off socket mount
      visível (já existe — manter)
- [x] `sk-*`/keys mascarados em todas as respostas (`SecretNamesJson`)

## 5. Artefatos

- [x] `CHANGELOG.md` — seção `v0.1.0-beta.1` com highlights (audit
      summary: ~130 SPECs entregues, OWUIC design-system, 45+ tools,
      enterprise ops) + breaking notes (remoção `/api/v1/pending-messages`)
- [x] Bump de versão se houver campo — N/A: nenhum `.csproj`/
      `Directory.Build.props` declara `VersionPrefix` (sem campo a bater)
- [ ] `git tag -a v0.1.0-beta.1 -m "first public beta"` + push da tag
- [ ] GitHub Release: notas a partir do CHANGELOG + screenshots +
      quickstart snippet (compose beta)
- [ ] Imagem Docker etiquetada `openhandsnet:0.1.0-beta.1` (+ `latest`
      se for o canal) — build via `docker build .` ou CI `docker image`
- [x] README: badge de versão/status "beta" no cabeçalho

## 6. Pós-tag

- [ ] Issue de feedback beta pinada (canal de report)
- [ ] Rename do projeto (decisão pendente — família "fusão dos 4";
      recomendado executar **antes** do anúncio público para não
      rebatizar depois do lançamento)
- [ ] Anúncio: descrição honesta do estado (ver §7)

## 7. Nota de escopo para o anúncio (honestidade de produto)

Incluído no beta: loop do agente + streaming, sandbox Docker +
surfaces (terminal/VSCode/preview/desktop noVNC), 45+ builtin tools +
MCP/ACP, permissões + plan mode + advisor, checkpoints + fork/undo,
enterprise ops (orgs/SCIM/OIDC/quotas/audit), i18n 8 locales, PWA.

Limitações conhecidas (documentar): pending messages são in-memory por
réplica (fila durável = roadmap); `Auth:Provider=none` é single-user
local; multi-repo workspace não existe; resume exige sandbox RUNNING
(sem wake-in-fresh-sandbox); billing = metering apenas (sem PSP);
k8s multi-host em SPEC-49 (parcial).

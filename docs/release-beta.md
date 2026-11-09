# Release checklist — `v0.1.0-beta.1`

Checklist executável para cortar a primeira tag beta (Epic #365 · SPECs
132–137). English abaixo de cada bloco quando o item é público.

## 1. Pré-requisitos (specs implementadas)

- [x] SPEC-132 auth-ux (#366) merged — `/auth` + user menu + sign-out
- [x] SPEC-133 web-search-tool (#367) merged — `web_search` builtin
- [ ] SPEC-134 beta-byok (#368) merged — keys de LLM por usuário
- [ ] SPEC-135 conversation-ux (#369) merged — fork + undo + hunk revert
- [x] SPEC-136 nav-visual-polish (#370) merged — nav órfã + fontes + ícones
- [x] SPEC-137 housekeeping (#371) merged — este checklist + dead code + docs

## 2. Qualidade (gates obrigatórios)

- [ ] `dotnet format --verify-no-changes` limpo em `main`
- [ ] `dotnet build OpenHandsNET.slnx -c Release` — 0 warnings / 0 errors
- [ ] `dotnet test` — suítes verdes; flakes conhecidos documentados no
      corpo da tag (SPEC-78 quarantine list)
- [ ] Golden OpenAPI (`Spec51OpenApiContractTests`) byte-idêntico ou
      regerado intencionalmente com nota de breaking-change
- [ ] SonarCloud gate verde na main (0 bugs/vulnerabilities novos)
- [ ] Drift guards client: `tools/check-*.py` todos passando
- [ ] `gitleaks`/GitGuardian limpos

## 3. Quickstart em ambiente limpo (gate manual)

Em uma VM/runner sem estado dev (`git clone` fresco):

- [ ] `cp .env.exemplo .env` + preencher só `OPENHANDSNET_AgentLlm__*`
      (provider/model/key)
- [ ] `docker compose -f docker-compose.beta.yaml up -d --build` →
      app saudável em `:3032` (`/api/v1/ready` = 200)
- [ ] Primeira conversa: criar → enviar mensagem → agente executa tool
      calls → `finish` (ou `ask_user`/confirmation conforme config)
- [ ] Desktop tab abre noVNC (SPEC-113, `SANDBOX_DESKTOP=1` default)
- [ ] `Auth:Provider=none` → boot direto sem login (single-user local);
      quando `oidc` configurado → `/auth` + SSO + sign-out funcionam
- [ ] PWA instalável (manifest + service worker ativos)

## 4. Segurança (defaults auditados)

- [ ] `Auth:Provider=none` documentado como **single-user local** —
      callout "não exponha à internet sem `oidc`" em README + env docs
- [ ] `Auth:Provider=none` + exposto → rate-limit e IP rules ativos
      (SPEC-59 já default-on; verificar no compose beta)
- [ ] Egress: `unrestricted` default documentado + link
      `docs/deployment/egress-allowlist.md` no `.env.exemplo`
- [ ] `.env.exemplo` sem secrets reais; `POSTGRES_PASSWORD=CHANGE_ME`
- [ ] `docker-compose.beta.yaml` comentário do trade-off socket mount
      visível (já existe — manter)
- [ ] `sk-*`/keys mascarados em todas as respostas (`SecretNamesJson`)

## 5. Artefatos

- [ ] `CHANGELOG.md` — seção `v0.1.0-beta.1` com highlights (audit
      summary: ~130 SPECs entregues, OWUIC design-system, 45+ tools,
      enterprise ops) + breaking notes (remoção `/api/v1/pending-messages`)
- [ ] Bump de versão se houver campo (`.csproj` `VersionPrefix` ou
      `Directory.Build.props`) → `0.1.0-beta.1`
- [ ] `git tag -a v0.1.0-beta.1 -m "first public beta"` + push da tag
- [ ] GitHub Release: notas a partir do CHANGELOG + screenshots +
      quickstart snippet (compose beta)
- [ ] Imagem Docker etiquetada `openhandsnet:0.1.0-beta.1` (+ `latest`
      se for o canal) — build via `docker build .` ou CI `docker image`
- [ ] README: badge de versão/status "beta" no cabeçalho

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

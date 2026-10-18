# Segurança de deployment

## docker.sock

O app cria/gerencia containers de sandbox via Docker API. Montar
`/var/run/docker.sock` dá ao processo controle total do daemon — efetivamente
root no host.

**Mitigações, em ordem de preferência:**

1. **Docker rootless** — daemon por usuário; socket em
   `unix:///run/user/<uid>/docker.sock` (`DOCKER_HOST`).
2. **`DOCKER_HOST=tcp://`** com TLS mútuo para um daemon dedicado/VM.
3. **Socket proxy** (ex.: `tecnativa/docker-socket-proxy`) expondo só as
   APIs necessárias (`containers`, `images`, `exec`) — POST limitado.
4. Nunca montar o sock em exemplos rotulados "produção" sem as mitigações
   acima.

## Secrets

- Nenhum valor sensível em `.env` commitado — `.env.exemplo` só tem placeholders.
- Secrets da aplicação ficam cifrados (DataProtection) na tabela `secrets`.
- Tokens git entram no container via `GIT_ASKPASS` shim — nunca na URL,
  em args ou logs (`SecretRedactor` cobre saídas).
- `sk-*` de API keys armazenados como hash SHA-256 (SPEC-08).

## Imagem

- Runtime final `aspnet:10.0`, usuário `openhandsnet` (non-root), sem SDK.
- `HEALTHCHECK` em `/api/v1/ready` (falha ≠ restart automático — o compose
  também tem healthcheck para orquestração).

## Métricas e logs

- `/metrics` expõe só contadores (nunca prompts, conteúdo de arquivos ou
  secrets — apenas metadados como token counts e paths relativos).
- Logs carregam `trace_id`, `conversation_id`, `service` — nunca credenciais.
- Se necessário, proteja `/metrics` atrás de reverse proxy com auth.

## Políticas de segurança por org (SPEC-48)

Cada organização tem uma `security_policy` (tabela `security_policies`,
uma linha por org) que governa o gate de confirmação do agente:

- **`preset`**: `permissive` | `balanced` (default) | `strict`.
  - `balanced`: analyzer `pattern`, confirmação só em risco `high`,
    `mcp__*` passa pelo piso do guardrail (fronteira de credenciais).
  - `strict`: analyzer LLM (com fallback para `pattern` + warning se o
    provider não estiver disponível), confirmação em `medium`+,
    `mcp__*` sempre pede confirmação.
  - `permissive`: analyzer off, confirmação só em `high` — **opt-out
    explícito** que também relaxa o piso de MCP.
- **Overrides por campo** (vencem o preset): `analyzer_override`
  (`pattern`/`llm`/`off`), `ask_mcp_tools`, `min_confirm_risk`
  (`low`/`medium`/`high`), `resume_strict`.

API: `GET|PUT /api/v1/organizations/{id}/security-policy` (leitura por
membros; escrita exige `perm:edit_org_settings`; `PUT` com body nulo
reseta para `balanced`). Toda escrita gera audit
`security_policy_updated`. A política é resolvida uma vez por run e
cacheada por 60s via `IDistributedCache`.

Diagnósticos `security_risk` carregam `policy=<preset>, min_confirm=<…>`
para observabilidade da decisão.

### Resume fail-closed

- `AgentLlm:ResumeEnforcement` = `open` (default) | `strict`
  (`AgentLlm:StrictResume=true` é o alias legado), e/ou
  `resume_strict=true` na policy da org (o org override vence o global).
- Em `strict`: retomar um run cujo contrato de tools perdeu ferramentas
  emite `agent_diagnostic` `code=resume_drift`, `severity=error` e o run
  termina `Error` — fail-closed. Tools **adicionadas** não bloqueiam.
- Em `open` (default): o mesmo drift emite `resume_drift` com
  `severity=warning` e segue (comportamento pré-SPEC-48).

## Edge security (SPEC-59)

### IP access rules (`ip_access_rules`)

`EdgeAccessMiddleware` é o primeiro middleware do pipeline. Regras com
`scope` `global` ou `org:{id}` (extraído de `/api/v1/orgs/{id}/...`),
`cidr` IPv4/IPv6, `action` `allow|deny`, `priority`. Avaliação:
deny vence sempre; se existir ao menos uma regra `allow` para o escopo,
o IP precisa casar; caso contrário, allow por default. Cache de 30s
(`IMemoryCache`). `X-Forwarded-For` só é honrado com
`Proxy:TrustForwardedHeaders=true`. Rotas em `Edge:ExemptPaths`
(default `/api/v1/health`, `/api/v1/ready`, `/scim/v2`) ignoram.

Admin: `GET|POST|DELETE /api/v1/ip-rules` (`perm:manage_super_admins`)
e `/api/v1/orgs/{org}/ip-rules` (`perm:edit_org_settings`), ou
Settings → Security (`/admin/security`).

### Input guard (`IInputGuard`)

Todo texto que vira prompt passa por `ScoreAsync(text, channel, orgId)`:
`send-message`, `initial_message` de `POST /app-conversations` e
webhooks de chat/tickets/SCM. `PatternInputGuard` (sempre on) cobre
prompt-injection (`ignore previous`, `system prompt`, markup de controle
`<|im_start|>`, `<<SYS>>`, `[INST]`), exfiltração (`reveal secrets`,
`curl/wget` exfil) e act-as/base64 longo (warn). Veredito `block` →
403 `input_blocked` + resposta educada no canal + audit
`guard.input_blocked` (só categorias + hash do texto — nunca conteúdo)
+ evento `input_blocked`. Modo por org via `security_policies.
input_guard` = `off|pattern|llm`; `llm` usa `InputGuard:LlmEndpoint`
(OmniRoute), timeout 2s, `InputGuard:FailMode=closed` torna fail-closed.

Diagnóstico: `POST /api/v1/edge/check` `{ip?, message?}` →
`{ip: allow|block, guard: {verdict, categories}}` sem efeitos.

### Rate limit por ator

Chave `ratelimit:{actor_kind}:{actor_id}:{bucket}:{minuto}` —
`actor_kind`/`actor_id` vêm dos claims de auth (service user `svc_*`,
key-hash ou IP como fallback). Buckets por rota:
`read` (GET), `write` (POST/PUT/PATCH/DELETE), `webhook`
(`/chatops/`, `/scm/webhooks/`, `/tickets/webhooks/`), `expensive`
(`/app-conversations`, `send-message`, `/sandboxes`). Limite excedido →
429 + `Retry-After` + `X-RateLimit-Limit/Remaining/Reset`. Limites:
`RateLimit:Read/Write/Webhook/ExpensivePerMinute`.

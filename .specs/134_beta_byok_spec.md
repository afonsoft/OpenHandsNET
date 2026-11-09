# SPEC — 134 · BYOK por usuário (provider keys no Settings)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `beta-byok` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | #368 · Epic #365 |
| Scope | `src/` + EF migration + `Client/` + `tests/` + `docs/` |

## 1. User Story

**As a** usuário self-hosted, **I want** colar minha chave de provider
(OpenAI, Anthropic, Gemini ou endpoint OpenAI-compat) nas Settings e ter
minhas conversas usando essa chave, **so that** eu não precise levantar
um LiteLLM só para usar minha própria quota — o fluxo de 30 segundos que
todo produto de agente oferece.

## 2. Contexto / escopo

Hoje o routing de LLM resolve por esta ordem (`ModelRouter`,
`OmniRouteModelCatalog`, SPEC-23/50):

1. BYOR LiteLLM (`/api/v1/keys/llm/byor` — virtual keys; exige infra
   admin rodando um proxy LiteLLM — formato enterprise).
2. Config global (`Llm__*` env: provider/model/key do servidor inteiro).

Não existe caminho "cada usuário traz sua key" — o BYOR atual é
**infra-shaped** (admin deploya proxy), não **user-shaped** (usuário
cola key). Para beta self-hosted, o segundo é o caso de uso real.

Decisões de design:

- Entidade `UserLlmKey` (EF): `{Id, UserId, Provider, EncryptedKey,
  BaseUrl?, CreatedAt, UpdatedAt}` — unique `(UserId, Provider)`;
  criptografia em repouso reusa o mecanismo de secrets existente
  (`SecretStore`/IDataProtection — o que SPEC-33 já usa).
- Endpoints sob o grupo Keys existente:
  `GET /api/v1/keys/llm` (lista providers configurados do usuário —
  **masked**: `sk-…ultimos4`), `PUT /api/v1/keys/llm/{provider}`
  (upsert `{key, base_url?}`), `DELETE /api/v1/keys/llm/{provider}`,
  `GET /api/v1/keys/llm/providers` (catálogo estático suportado).
  Providers: `openai`, `anthropic`, `gemini`, `openai_compat`
  (este exige `base_url`).
- Resolução no `ModelRouter`/`OpenAiCompatLlmProvider`: nova precedência
  **key do usuário (match do provider do modelo pedido) → BYOR →
  config global**. O runner já conhece o owner da conversa — a key é
  resolvida por `user_id` no início do run e injetada no provider
  client (por-run, nunca cacheada em singleton com a chave em claro).
- `POST …/test` por provider (ping: `GET /models` ou 1-token
  completion) para o botão "testar" do Settings — retorna
  `{ok, detail?}` sem vazar a key.
- Settings UI: nova seção "LLM keys" na tab agent&LLM do
  `SettingsContent.razor` — select de provider, input password +
  base_url condicional, lista masked, botões Salvar/Testar/Remover.
- Segurança: serialização sempre mascarada; `SecretRedactor` ganha o
  valor como padrão a redigir (mesmo padrão `SecretNamesJson`); audit
  `keys.llm.upsert|delete`; rate-limit no `test` (anti-oráculo);
  isolamento por `user_id` (owner check em todo handler).

Fora de escopo: keys por org (org-level fica para depois via
`IOrgScoped`), OAuth por provider (login-with-OpenAI), rotação automática,
quota por provider, BYOK criptografada client-side.

## 3. Requisitos

- RF-001: entidade + migration `UserLlmKey` (unique user+provider,
  coluna key criptografada via mecanismo de secrets existente).
- RF-002: `ILlmKeyResolver` — `ResolveAsync(userId, modelProvider, ct)`
  → `{key, baseUrl?} | null`; ordem user-key → BYOR → global, com
  fonte registrada no ledger (`llm_route` event já emite rota — adicionar
  campo `key_source: user|byor|global`).
- RF-003: CRUD endpoints (tabela §4) — owner-scoped, masked output,
  audit, validation (provider ∈ catálogo, `base_url` https e exigido
  para `openai_compat`).
- RF-004: `POST /api/v1/keys/llm/{provider}/test` → ping real com a key
  salva (timeout 10 s, sem logar resposta crua), `{ok, models?|detail?}`.
- RF-005: UI "LLM keys" no `SettingsContent` — CRUD + masked + testar,
  i18n ×8.
- RF-006: run usa a key do dono — verificado em integração: run de user
  A com key própria chama provider com ela (stub LLM captura header);
  user sem key cai para BYOR/global.
- RF-007: testes — unit (resolver precedence, masking, validator),
  integration (CRUD isolamento user A≠B, test endpoint, run usa user
  key via stub), migration round-trip.

## 4. API Contract

| Método | Rota | Auth | Notas |
|---|---|---|---|
| GET | `/api/v1/keys/llm` | sessão | providers do usuário, masked |
| PUT | `/api/v1/keys/llm/{provider}` | sessão | upsert `{key, base_url?}` → masked |
| DELETE | `/api/v1/keys/llm/{provider}` | sessão | remove |
| GET | `/api/v1/keys/llm/providers` | sessão | catálogo estático |
| POST | `/api/v1/keys/llm/{provider}/test` | sessão | ping; rate-limited |

`provider` ∈ `openai | anthropic | gemini | openai_compat`.
Resposta sempre `{provider, masked_key, base_url?, updated_at}` — a key
crua nunca sai.

## 5. Critérios de aceite

- [ ] Usuário cola key no Settings → run seguinte usa ela (header
      `Authorization` correto no stub/provider).
- [ ] `GET /keys/llm` nunca retorna key crua; DB persiste criptografada.
- [ ] User B não vê/usa key de A; `openai_compat` exige `base_url` https.
- [ ] Sem key de usuário, resolução cai para BYOR→global (comportamento
      atual inalterado).
- [ ] `dotnet format` clean, build 0 warnings, testes verdes; golden
      OpenAPI regerado com as novas rotas.

## 6. Task Plan

1. Entidade + migration + criptografia (reuso secrets store).
2. `ILlmKeyResolver` + wiring no `ModelRouter`/provider + `key_source`
   no ledger.
3. Endpoints CRUD + test + validação + audit.
4. UI Settings + i18n.
5. Testes + golden OpenAPI regen + docs env/secrets.

## 7. Deliverables

- `Domain/Entities/UserLlmKey.cs` + migration,
  `Infrastructure/…/LlmKeyResolver.cs`, handlers em `KeysEndpoints.cs`,
  seção em `SettingsContent.razor`, i18n, testes, golden regen, docs.

## 8. Referências

- `src/OpenHandsNET.Api/Endpoints/KeysEndpoints.cs:52-67` — grupo Keys e
  endpoints BYOR existentes (mesmo grupo, mesmas tags).
- `src/OpenHandsNET.Infrastructure/Routing/ModelRouter.cs` +
  `Agent/OpenAiCompatLlmProvider.cs` — pontos de injeção da resolução.
- SPEC-33 (`SecretNamesJson`/`SecretRedactor`), SPEC-50 (BYOR),
  SPEC-23 (role-models + `llm_route`).

## 9. Guardrails

- Key crua nunca serializa/loga/persiste sem criptografia; masked =
  `sk-…{last4}`.
- `base_url` somente https (sem http loopback em produção; permitir
  http apenas `localhost`/`127.0.0.1` para dev self-hosted).
- `test` rate-limited e sem eco de payload do provider.
- Sem breaking change: quem usa só config global/BYOR não nota nada.
- Golden OpenAPI: rotas novas → regen obrigatório no PR.

## 10. DoD

- CRUD + resolução por usuário funcionais e isolados; UI operacional;
  run de ponta a ponta usando key do usuário; testes/aceite verdes;
  audit + redaction cobrindo a nova superfície.

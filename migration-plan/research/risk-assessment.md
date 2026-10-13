# Risk Assessment — OpenHandsNET

## Riscos de licenciamento (crítico)

| Risco | Severidade | Mitigação |
|---|---|---|
| `enterprise/` e `OpenHands-Cloud/` são **Polyform Free Trial** — não open source | **ALTA** | SPECs reproduzem apenas **conceitos/contratos públicos** e o código MIT (SDK, Agent Server, Canvas, CLI). Implementação .NET original. Nunca copiar arquivos das árvores Polyform. Registrar em `NOTICE`. |
| Derivative-work ambíguo ao portar routers SaaS | MÉDIA | Reimplementar endpoints a partir de comportamento/observação da API pública (OpenAPI publicado é documentação), com nomes/estruturas próprios onde não houver contrato externo. |

## Riscos técnicos

| Risco | Severidade | Mitigação |
|---|---|---|
| **Sandbox Docker em .NET** — execução isolada, streaming stdout/stderr, filesystem do workspace | ALTA | `Docker.DotNet` + Testcontainers nos testes; SPEC 03 define `ISandboxService` com provas de conceito nos critérios de aceite; warm-pool fica para tier cloud (K8s) fora do MVP. |
| **Streaming realtime** — upstream usa WS ao agent-server + webhooks + SSE; SignalR muda o contrato | MÉDIA | REST V1 mantido idêntico; SignalR apenas no edge UI (adapter). Documentado como divergência consciente em SPEC 04. Fallback SSE via `stream-start`. |
| **Modelo de eventos V1** (19 kinds, árvore `parent_id`, ULID) vs doc do usuário (V0 int/action/args) | MÉDIA | SPEC 02 usa modelo V1 real; doc do usuário é corrigido e versionado em `docs/reference/`. |
| **LLM provider abstraction** — LiteLLM no upstream vs Semantic Kernel no .NET | MÉDIA | `IAgentLlmProvider` abstrato; SK como impl primária + gateway OpenAI-compatible (permite LiteLLM proxy / `afonsoft/LLMRouter`). |
| **Confirmation mode / segurança de comandos** | ALTA | `ConversationStateUpdateEvent` com `waiting_for_confirmation`; command allowlist/analyzer; auditoria de todo `ActionEvent` executado. |
| **Persistência de eventos** em SQLite sob alto throughput | MÉDIA | Event store append-only com índice por `conversation_id+timestamp`; páginação cursored (`page_id`); tier enterprise → PostgreSQL. |
| **Secrets nunca ao cliente** | ALTA | `SecretValue` mascarado na serialização (padrão `pydantic_secrets` do upstream → equivalente `JsonConverter` custom); resolução só no sandbox. |
| **Multi-tenant isolation** | MÉDIA | Começar single-tenant/OSS; multi-tenancy por `OrgId` + EF query filters só no tier enterprise (SPEC 08). |

## Riscos de escopo

| Risco | Mitigação |
|---|---|
| Superfície gigante (14 routers app_server + ~30 routers agent_server + ~30 rotas SaaS) | Corte por fases no roadmap: MVP = conversações+eventos+sandbox+chat UI; fase 2 = enterprise/CLI/MCP; fase 3 = automações/ACP/extensões. |
| Manter paridade com upstream em movimento | Versionar "baseline upstream" (SHA dos clones) em `docs/reference/upstream-baseline.md`; re-auditoria periódica via `gap-analysis`. |

## Security debt a não portar

- Não replicar nenhum padrão observado em código Polyform além de conceitos.
- Auth: JWT + `sk-*` API keys (padrão open-webui-net) para OSS; OIDC/Keycloak opcional enterprise.
- `confirmation_mode` e `security_analyzer` são campos de `ConversationSettings` upstream — manter semântica, implementar própria.

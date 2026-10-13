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

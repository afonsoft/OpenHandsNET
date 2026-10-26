# SPEC — 107 · Desktop surface (live noVNC + proxy interno)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `desktop-surface` |
| Type | `Feature` |
| Status | `Approved` |
| Ticket | roadmap de paridade Devin Web (docs/comparison.md — item "live desktop/streaming") |
| Scope | `runtime/ + src/ + tests/ + docs/` |

## 1. User Story

**As a** usuário do OpenHandsNET, **I want** abrir uma aba "Desktop" na
conversa e ver/controlar a tela do sandbox ao vivo (mouse + teclado),
**so that** tenho a mesma superfície de observabilidade do Devin web
(Desktop tab) sem sair do app.

## 2. Contexto / escopo

Hoje a UX mostra apenas artefatos: `TerminalTab` = replay de buffer,
`BrowserTab` = filmstrip de screenshots emitidos pelo agente. Não há
visão viva nem interativa do sandbox — maior gap de paridade com o
Devin web.

O que já existe e é reutilizado (não reinventar):

- `SurfaceRegistry`/`SurfaceSupervisor` no sandbox-agent (SPEC-40):
  surface = processo supervisionado opt-in por env flag, portas
  expostas por `GET /surfaces` do agente.
- `SandboxHostRouter`: proxy HTTP **e WebSocket** para
  `{container}:{port}` com auth de sessão ou preview token — mas só
  atua quando `Sandbox__RoutingMode` = `host` ou `path`; no default
  `internal` (OSS/docker-compose) ele é no-op e as surfaces ficam
  inalcançáveis pela UI.
- `SandboxService` já injeta `SANDBOX_VSCODE=1` por default em imagens
  agente (blueprint desliga com `=0`) — mesmo padrão para o desktop.

Decisões de design:

- Stack gráfica **dentro do sandbox** (paridade com a abordagem upstream
  de imagens custom): `Xvfb` + `openbox` + `x11vnc` + `noVNC`/`websockify`
  + `chromium`. Um único launcher `/opt/ohn/sdks/desktop.sh` orquestra os
  filhos e é supervisionado pelo `SurfaceSupervisor` existente (respawn
  com backoff já cobre crash).
- noVNC serve `vnc.html` (HTTP) e `websockify` (WS) **na mesma porta**
  6080 → uma única surface `http` basta; o proxy WS existente cobre o
  canal RFB. Interação completa (clique/tecla) vem de graça do protocolo
  VNC — não é stream somente-leitura.
- Alcance no modo `internal`: endpoint same-origin
  `/api/v1/sandboxes/{id}/surfaces/{port}/{**path}` (+ upgrade WS) que
  reusa a lógica de proxy do `SandboxHostRouter` extraída para um
  serviço compartilhado. Auth = sessão (FallbackPolicy já exige
  autenticação) ou `?token=` preview token (SPEC-40 RF-004) para iframes
  compartilháveis.

Fora de escopo: gravação/replay do desktop, multi-viewer, clipboard
sync, resize dinâmico de resolução (noVNC `resize=scale` cobre o visual),
WebRTC/RFB direto (justificativa: noVNC já entrega o caso de uso com a
porta/WS que a infra já proxya).

## 3. Requisitos

- RF-001: `runtime/Dockerfile` instala `xvfb x11vnc openbox novnc
  websockify chromium-browser fonts-*` e copia
  `/opt/ohn/sdks/desktop.sh` (launcher: `Xvfb :0` → `openbox` → `x11vnc`
  → `websockify --web=/usr/share/novnc 6080 localhost:5900`; espera
  filhos, propaga exit). Imagem base sem a flag continua válida — o
  launcher só roda quando a surface está habilitada.
- RF-002: `SurfaceRegistry` registra `"desktop"` — env `SANDBOX_DESKTOP`,
  porta 6080, protocolo `http`, binário `/opt/ohn/sdks/desktop.sh`.
  `SandboxService` injeta `SANDBOX_DESKTOP=1` por default em imagens
  agente (mesmo guard de `SANDBOX_VSCODE`): blueprint desliga com `=0`.
- RF-003: extrair proxy HTTP/WS do `SandboxHostRouter` para
  `SandboxSurfaceProxy` (Application/Infra) e expor
  `GET /api/v1/sandboxes/{id:guid}/surfaces/{port:int}/{**rest}`
  same-origin em qualquer `RoutingMode`. Mesmo enforcement: sessão
  autenticada OU `?token=` preview token válido → 401 caso contrário;
  404 se o sandbox não existir/não estiver em execução. Suporta upgrade
  WebSocket (canal `websockify`).
- RF-004: `DesktopTab.razor` + entrada `("desktop", "conv.tab_desktop")`
  na tab-bar da `ConversationPage`. Iframe para
  `{surfaceBase}/vnc.html?autoconnect=true&resize=scale` com o base
  resolvido pelo sandbox da conversa; empty-state quando a surface
  desktop não está ativa (hint `SANDBOX_DESKTOP=1`). Chaves i18n novas
  em todos os locales do baseline (check-i18n-parity).
- RF-005: testes — unit (registro/flag `SANDBOX_DESKTOP`, resolução da
  URL de surface, default env no `SandboxService`), bUnit (tab render +
  empty-state), integration (proxy: 401 sem auth, 404 sandbox inexistente,
  rota registrada em `internal`). Sem NSubstitute (hand stubs).
- RF-006: docs — `environment-variables.md` (en+pt) com `SANDBOX_DESKTOP`;
  `comparison.md` marca o item do roadmap como entregue.

## 4. Acceptance

- [ ] `docker compose` (ou sandbox criado pela API) com `SANDBOX_DESKTOP=1`
      responde `vnc.html` via `/api/v1/sandboxes/{id}/surfaces/6080/…`
      autenticado; WS do websockify conecta e a aba Desktop renderiza a
      tela (verificado em browser real, screenshot anexado).
- [ ] Sem a flag: nenhum processo gráfico roda, `/surfaces` não lista
      `desktop`, aba mostra empty-state.
- [ ] 401 sem sessão/preview-token; 404 com sandbox inexistente.
- [ ] `dotnet format` clean, build 0 warnings, suítes verdes
      (unit+client+integration), drift guards do client passando.

## 5. API Contract

| Método | Rota | Auth | Notas |
|---|---|---|---|
| GET | `/api/v1/sandboxes/{id}/surfaces/{port}/{**rest}` | sessão ou `?token=` | proxy same-origin p/ surfaces; WS upgrade suportado |

Sandbox-agent (dentro do container): surface `desktop` em `GET /surfaces`
→ `{ name: "desktop", port: 6080, protocol: "http" }` quando ativa.

## 6. Riscos / notas

- Imagem runtime engorda (~400 MB com chromium+novnc) — mitigado por
  opt-out `SANDBOX_DESKTOP=0` e superfícies permanecerem opt-in por env.
- x11vnc/websockify sem credencial própria — igual às surfaces atuais:
  a fronteira de auth é o proxy (sessão/preview token); portas só
  alcançáveis pela rede interna + proxy autenticado.
- `vnc.html` assume websockify same-origin — `--web` do novnc já
  referencia `/websockify`; o strip de prefixo do proxy preserva o path.

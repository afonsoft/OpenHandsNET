# syntax=docker/dockerfile:1
# SPEC-17 RF-001: imagem runtime do Sandcastle.
#
# ubuntu:24.04 + toolchain base + sandcastle-sandbox-agent (PID 1 sob tini).
# O agente é publicado no próprio build (multi-stage) — sem binário commitado.
#
# Regras (paridade upstream custom-sandbox-images):
# - nunca sobrescreva o ENTRYPOINT — estenda com RUN/ENV;
# - nunca embuta secrets — credenciais chegam via env no start/init;
# - setup de repo pertence ao blueprint (sandcastle.yaml/.openhands/setup.sh),
#   não à imagem.

# ---- stage 1: publish do sandbox-agent (self-contained single-file) ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS agent-build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Sandcastle.SandboxAgent ./src/Sandcastle.SandboxAgent
ARG TARGETARCH
RUN dotnet publish src/Sandcastle.SandboxAgent -c Release \
    -r linux-$( [ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64 ) \
    --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false \
    -o /out

# ---- stage 2: runtime ----
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive \
    TZ=UTC \
    SC_WORKSPACE=/workspace

# NOSONAR S7031 — RUNs separados por propósito preservam cache de camada
# e legibilidade (apt-base, gh cli, node, ttyd, usuário).
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential ca-certificates curl file git git-lfs gnupg \
        jq less libssl-dev locales openssh-client pkg-config procps \
        python3 python3-pip python3-venv ripgrep sudo tar tini unzip \
        vim-tiny wget xz-utils zip \
    && rm -rf /var/lib/apt/lists/*

# GitHub CLI (repo oficial, pin de chave do apt)
RUN mkdir -p -m 755 /etc/apt/keyrings \
    && curl -fsSL --proto '=https' --tlsv1.2 https://cli.github.com/packages/githubcli-archive-keyring.gpg \
        -o /etc/apt/keyrings/githubcli-archive-keyring.gpg \
    && chmod go+r /etc/apt/keyrings/githubcli-archive-keyring.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
        > /etc/apt/sources.list.d/github-cli.list \
    && apt-get update && apt-get install -y --no-install-recommends gh \
    && rm -rf /var/lib/apt/lists/*

# Node LTS (22.x) via NodeSource — agente precisa para tools web/npx.
ARG NODESOURCE_SETUP_SHA256=575583bbac2fccc0b5edd0dbc03e222d9f9dc8d724da996d22754d6411104fd1
# NOSONAR S7026 — curl+sha256sum no mesmo RUN verifica o script antes de
# executar; ADD deixaria camada não-verificada no cache.
RUN curl -fsSL --proto '=https' --tlsv1.2 https://deb.nodesource.com/setup_22.x \
        -o /tmp/nodesource-setup.sh \
    && echo "${NODESOURCE_SETUP_SHA256}  /tmp/nodesource-setup.sh" | sha256sum -c - \
    && bash /tmp/nodesource-setup.sh \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

# SPEC-131 RF-001: Playwright + chrome-devtools-mcp — browsers
# pré-instalados num path compartilhado para npx, MCPs e scripts do
# agente; --with-deps resolve as libs de sistema do Chromium.
ARG PLAYWRIGHT_NPM_VERSION=1.64.1
ARG CHROME_DEVTOOLS_MCP_VERSION=1.12.0
ENV PLAYWRIGHT_BROWSERS_PATH=/opt/sc/ms-playwright \
    PUPPETEER_SKIP_DOWNLOAD=1
RUN npm install -g --ignore-scripts "playwright@${PLAYWRIGHT_NPM_VERSION}" \
        "chrome-devtools-mcp@${CHROME_DEVTOOLS_MCP_VERSION}" \
    && playwright install --with-deps chromium \
    && chrome_bin="$(find "$PLAYWRIGHT_BROWSERS_PATH" -name chrome -type f | sort | head -1)" \
    && [ -n "$chrome_bin" ] && ln -sf "$chrome_bin" /usr/local/bin/chromium \
    && chmod -R a+rX "$PLAYWRIGHT_BROWSERS_PATH" \
    && rm -rf /var/lib/apt/lists/*
ENV CHROME_PATH=/usr/local/bin/chromium \
    PUPPETEER_EXECUTABLE_PATH=/usr/local/bin/chromium

# SPEC-40 RF-002: ttyd (pinned) — terminal web opt-in via
# SANDBOX_TERMINAL=1; o sandbox-agent supervisiona o processo e expõe
# /surfaces. Binário estático multi-arch do release oficial.
ARG TTYD_VERSION=1.7.7
# NOSONAR S7031 — ver comentário acima: RUN por propósito, não mergeado.
RUN arch="$(dpkg --print-architecture)" \
    && case "$arch" in \
        amd64) ttyd_arch=x86_64 ;; \
        arm64) ttyd_arch=aarch64 ;; \
        *) echo "unsupported arch: $arch" >&2; exit 1 ;; \
    esac \
    && curl -fsSL --proto '=https' --tlsv1.2 "https://github.com/tsl0922/ttyd/releases/download/${TTYD_VERSION}/ttyd.${ttyd_arch}" \
        -o /usr/local/bin/ttyd \
    && chmod +x /usr/local/bin/ttyd

# Usuário não-root + workspace. sudo restrito a apt/installers de SDK
# (blueprint roda como openhands; installers conhecidos podem usar sudo).
RUN useradd -m -u 10001 -s /bin/bash openhands \
    && mkdir -p /workspace /opt/sc/sdks \
    && chown -R openhands:openhands /workspace /opt/sc \
    && echo 'openhands ALL=(root) NOPASSWD: /usr/bin/apt-get, /usr/bin/apt, /opt/sc/sdks/*.sh' \
        > /etc/sudoers.d/sc-sdk \
    && chmod 0440 /etc/sudoers.d/sc-sdk

COPY runtime/sdks/ /opt/sc/sdks/
RUN chmod +x /opt/sc/sdks/*.sh

COPY --from=agent-build /out/sandcastle-sandbox-agent /usr/local/bin/
RUN chmod +x /usr/local/bin/sandcastle-sandbox-agent

USER openhands
WORKDIR /workspace
EXPOSE 8000

# tini como init (reaping/forward de sinais) → agente como PID supervisionado.
ENTRYPOINT ["tini", "--", "/usr/local/bin/sandcastle-sandbox-agent"]

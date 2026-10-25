# syntax=docker/dockerfile:1
# SPEC-17 RF-001: imagem runtime do OpenHandsNET.
#
# ubuntu:24.04 + toolchain base + openhandsnet-sandbox-agent (PID 1 sob tini).
# O agente é publicado no próprio build (multi-stage) — sem binário commitado.
#
# Regras (paridade upstream custom-sandbox-images):
# - nunca sobrescreva o ENTRYPOINT — estenda com RUN/ENV;
# - nunca embuta secrets — credenciais chegam via env no start/init;
# - setup de repo pertence ao blueprint (openhandsnet.yaml/.openhands/setup.sh),
#   não à imagem.

# ---- stage 1: publish do sandbox-agent (self-contained single-file) ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS agent-build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/OpenHandsNET.SandboxAgent ./src/OpenHandsNET.SandboxAgent
ARG TARGETARCH
RUN dotnet publish src/OpenHandsNET.SandboxAgent -c Release \
    -r linux-$( [ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64 ) \
    --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false \
    -o /out

# ---- stage 2: runtime ----
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive \
    TZ=UTC \
    OHN_WORKSPACE=/workspace

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
RUN curl -fsSL --proto '=https' --tlsv1.2 https://deb.nodesource.com/setup_22.x \
        -o /tmp/nodesource-setup.sh \
    && echo "${NODESOURCE_SETUP_SHA256}  /tmp/nodesource-setup.sh" | sha256sum -c - \
    && bash /tmp/nodesource-setup.sh \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

# SPEC-40 RF-002: ttyd (pinned) — terminal web opt-in via
# SANDBOX_TERMINAL=1; o sandbox-agent supervisiona o processo e expõe
# /surfaces. Binário estático multi-arch do release oficial.
ARG TTYD_VERSION=1.7.7
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
    && mkdir -p /workspace /opt/ohn/sdks \
    && chown -R openhands:openhands /workspace /opt/ohn \
    && echo 'openhands ALL=(root) NOPASSWD: /usr/bin/apt-get, /usr/bin/apt, /opt/ohn/sdks/*.sh' \
        > /etc/sudoers.d/ohn-sdk \
    && chmod 0440 /etc/sudoers.d/ohn-sdk

COPY runtime/sdks/ /opt/ohn/sdks/
RUN chmod +x /opt/ohn/sdks/*.sh

COPY --from=agent-build /out/openhandsnet-sandbox-agent /usr/local/bin/
RUN chmod +x /usr/local/bin/openhandsnet-sandbox-agent

USER openhands
WORKDIR /workspace
EXPOSE 8000

# tini como init (reaping/forward de sinais) → agente como PID supervisionado.
ENTRYPOINT ["tini", "--", "/usr/local/bin/openhandsnet-sandbox-agent"]

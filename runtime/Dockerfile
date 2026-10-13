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
        ca-certificates curl wget gnupg jq unzip zip tar xz-utils \
        git git-lfs openssh-client \
        python3 python3-pip python3-venv \
        tini sudo locales procps file less vim-tiny \
        build-essential pkg-config libssl-dev \
    && rm -rf /var/lib/apt/lists/*

# GitHub CLI (repo oficial, pin de chave do apt)
RUN mkdir -p -m 755 /etc/apt/keyrings \
    && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
        -o /etc/apt/keyrings/githubcli-archive-keyring.gpg \
    && chmod go+r /etc/apt/keyrings/githubcli-archive-keyring.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
        > /etc/apt/sources.list.d/github-cli.list \
    && apt-get update && apt-get install -y --no-install-recommends gh \
    && rm -rf /var/lib/apt/lists/*

# Node LTS (22.x) via NodeSource — agente precisa para tools web/npx.
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

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

#!/usr/bin/env bash
# Sandcastle host installer / updater / uninstaller.
#
#   sudo ./install.sh            install (or reinstall) on this host
#   sudo ./install.sh update     git pull origin/<branch>; redeploy if changed
#   sudo ./install.sh uninstall  remove service + binaries (keep data/secrets)
#   sudo ./install.sh uninstall --purge   also remove data, env and user
#
# Flags: --prefix DIR --data-dir DIR --port N --branch NAME
#        --force (update redeploys even without changes) --no-start -h|--help
#
# The app runs as a systemd service published to --prefix (default
# /opt/sandcastle) as user `sandcastle`. Sandboxes still need Docker
# (Sandbox:Provider=local): the daemon is installed/enabled and the
# service user joins the `docker` group.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DEBIAN_FRONTEND=noninteractive

# ---- defaults ---------------------------------------------------------------
PREFIX=/opt/sandcastle
DATA_DIR=/var/lib/sandcastle
ENV_FILE=/etc/sandcastle/env
UNIT_FILE=/etc/systemd/system/sandcastle.service
RUNTIME_IMAGE=ghcr.io/sandcastle/runtime:latest
SERVICE_USER=sandcastle
SERVICE_NAME=sandcastle
PORT=8080
BRANCH=main
DOTNET_DIR=/usr/local/lib/dotnet
DOTNET_CHANNEL=10.0
DOTNET_BIN=dotnet
FORCE=0
NO_START=0
PURGE=0
MODE=install

log()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mWARN:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }

usage() { sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }

# ---- arg parsing ------------------------------------------------------------
while [ $# -gt 0 ]; do
  case "$1" in
    install|update|uninstall) MODE="$1" ;;
    --prefix)    PREFIX="${2:?--prefix needs a value}"; shift ;;
    --data-dir)  DATA_DIR="${2:?--data-dir needs a value}"; shift ;;
    --port)      PORT="${2:?--port needs a value}"; shift ;;
    --branch)    BRANCH="${2:?--branch needs a value}"; shift ;;
    --force)     FORCE=1 ;;
    --no-start)  NO_START=1 ;;
    --purge)     PURGE=1 ;;
    -h|--help)   usage 0 ;;
    *) die "unknown argument: $1 (try --help)" ;;
  esac
  shift
done

SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

[ "$(id -u)" -eq 0 ] || die "run as root: sudo $0 $MODE"
[ -f "$SRC_DIR/Sandcastle.slnx" ] || die "$0 must live in the repo root (Sandcastle.slnx not found)"

# ---- helpers ----------------------------------------------------------------
have() { command -v "$1" >/dev/null 2>&1; }

pkg_install() {
  # Distro-agnostic package install; $@ = package names.
  if   have apt-get; then DEBIAN_FRONTEND=noninteractive apt-get install -y "$@"
  elif have dnf;     then dnf install -y "$@"
  elif have yum;     then yum install -y "$@"
  elif have pacman;  then pacman -Sy --noconfirm "$@"
  elif have zypper;  then zypper -n install "$@"
  else die "no supported package manager (apt/dnf/yum/pacman/zypper)"; fi
}

pkg_update() {
  if   have apt-get; then apt-get update -qq
  elif have dnf;     then dnf makecache -q || true
  elif have yum;     then yum makecache -q || true
  elif have zypper;  then zypper -n refresh || true
  fi
}

ensure_prereqs() {
  local missing=()
  have curl || missing+=(curl)
  have git  || missing+=(git)
  [ -d /etc/ssl/certs ] || missing+=(ca-certificates)
  if [ "${#missing[@]}" -gt 0 ]; then
    log "Installing prerequisites: ${missing[*]}"
    pkg_update; pkg_install "${missing[@]}"
  fi
}

ensure_dotnet() {
  if have dotnet && dotnet --list-sdks 2>/dev/null | grep -q "^${DOTNET_CHANNEL%%.*}\."; then
    log ".NET SDK $DOTNET_CHANNEL already present: $(dotnet --version)"
    return
  fi
  log "Installing .NET SDK $DOTNET_CHANNEL (dotnet-install script -> $DOTNET_DIR)"
  local tmp; tmp="$(mktemp)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tmp"
  bash "$tmp" --channel "$DOTNET_CHANNEL" --install-dir "$DOTNET_DIR"
  rm -f "$tmp"
  ln -sf "$DOTNET_DIR/dotnet" /usr/local/bin/dotnet
  hash -r
  have dotnet || die "dotnet install failed"
  log ".NET SDK ready: $(dotnet --version)"
}

resolve_dotnet() {
  ensure_dotnet
  DOTNET_BIN="$(command -v dotnet)"
}

ensure_docker() {
  if ! have docker; then
    log "Installing Docker Engine"
    pkg_update
    if   have apt-get; then pkg_install docker.io
    elif have dnf || have yum; then pkg_install docker
    elif have pacman;  then pkg_install docker
    elif have zypper;  then pkg_install docker
    else
      warn "no distro package matched; using get.docker.com convenience script"
      curl -fsSL https://get.docker.com | sh
    fi
  fi
  if have systemctl; then
    systemctl enable --now docker >/dev/null 2>&1 || true
  fi
  docker info >/dev/null 2>&1 || die "docker daemon not reachable — start it and re-run"
  log "Docker ready: $(docker version --format '{{.Server.Version}}' 2>/dev/null || echo ok)"
}

setup_user_dirs() {
  if ! id "$SERVICE_USER" >/dev/null 2>&1; then
    log "Creating service user '$SERVICE_USER'"
    useradd -r -s /usr/sbin/nologin -d "$DATA_DIR" "$SERVICE_USER" \
      || useradd -r -s /usr/sbin/nologin "$SERVICE_USER"
  fi
  usermod -aG docker "$SERVICE_USER" 2>/dev/null \
    || warn "could not add '$SERVICE_USER' to docker group — check 'docker' group exists"
  mkdir -p "$DATA_DIR/data" "$DATA_DIR/workspaces" "$DATA_DIR/ms-playwright" \
           "$(dirname "$ENV_FILE")" "$PREFIX"
  chown -R "$SERVICE_USER:$SERVICE_USER" "$DATA_DIR"
}

publish_app() {
  log "Publishing app -> $PREFIX (Release)"
  local stage; stage="$(mktemp -d)"
  "$DOTNET_BIN" publish "$SRC_DIR/src/Sandcastle.Api" \
    -c Release -o "$stage" --nologo
  mkdir -p "$PREFIX"
  cp -a "$stage"/. "$PREFIX"/
  rm -rf "$stage"
  chown -R "$SERVICE_USER:$SERVICE_USER" "$PREFIX"
}

install_playwright() {
  # Browser tool: the publish output bundles the Playwright Node driver;
  # install chromium + OS deps like the Dockerfile does.
  local cli node
  cli="$(find "$PREFIX/.playwright/package" -name cli.js -print -quit 2>/dev/null || true)"
  node="$(find "$PREFIX/.playwright/node" -name node -type f -print -quit 2>/dev/null || true)"
  if [ -z "$cli" ] || [ -z "$node" ]; then
    warn "playwright driver not found in publish output — browser tool may not work"
    return
  fi
  log "Installing Playwright chromium + OS deps -> $DATA_DIR/ms-playwright"
  PLAYWRIGHT_BROWSERS_PATH="$DATA_DIR/ms-playwright" \
    "$node" "$cli" install --with-deps chromium
  chown -R "$SERVICE_USER:$SERVICE_USER" "$DATA_DIR/ms-playwright"
}

write_env() {
  if [ -f "$ENV_FILE" ]; then
    log "Keeping existing $ENV_FILE"
    return
  fi
  log "Writing $ENV_FILE skeleton (chmod 600)"
  cat > "$ENV_FILE" <<'EOF'
# Sandcastle service environment — secrets live here (0600).
# LLM do agente (obrigatorio p/ runs reais; sem isso so o mock funciona):
# SANDCASTLE_AgentLlm__BaseUrl=https://your-llm-gateway.example.com/v1
# SANDCASTLE_AgentLlm__ApiKey=sk-...
# SANDCASTLE_AgentLlm__Model=my-model
# Opcionais: SANDCASTLE_Sandbox__Provider=local
#            SANDCASTLE_Sandbox__DefaultImage=ghcr.io/sandcastle/runtime:latest
EOF
  chmod 640 "$ENV_FILE"
  chown root:"$SERVICE_USER" "$ENV_FILE" 2>/dev/null || true
}

write_unit() {
  log "Writing $UNIT_FILE"
  cat > "$UNIT_FILE" <<EOF
[Unit]
Description=Sandcastle
After=network-online.target docker.service
Requires=docker.service

[Service]
User=$SERVICE_USER
Group=docker
WorkingDirectory=$PREFIX
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:$PORT
Environment=DOTNET_ROOT=$DOTNET_DIR
Environment=PLAYWRIGHT_BROWSERS_PATH=$DATA_DIR/ms-playwright
Environment=SANDCASTLE_Database__ConnectionString=Data Source=$DATA_DIR/data/sandcastle.db
Environment=SANDCASTLE_Sandbox__WorkspaceRoot=$DATA_DIR/workspaces
EnvironmentFile=-$ENV_FILE
ExecStart=$PREFIX/Sandcastle.Api
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
}

ensure_runtime_image() {
  log "Ensuring sandbox runtime image: $RUNTIME_IMAGE"
  if docker pull "$RUNTIME_IMAGE" 2>/dev/null; then
    return
  fi
  warn "pull failed — building runtime image locally from runtime/"
  docker build -t "$RUNTIME_IMAGE" "$SRC_DIR/runtime"
}

health_check() {
  [ "$NO_START" -eq 0 ] || return 0
  log "Waiting for http://127.0.0.1:$PORT/api/v1/ready"
  local i
  for i in $(seq 1 30); do
    if curl -fsS "http://127.0.0.1:$PORT/api/v1/ready" >/dev/null 2>&1; then
      log "Service is READY — http://localhost:$PORT"
      return 0
    fi
    sleep 2
  done
  warn "service did not become ready in 60s — check: journalctl -u $SERVICE_NAME -e"
  return 1
}

enable_start() {
  systemctl daemon-reload
  if [ "$NO_START" -eq 1 ]; then
    log "--no-start: unit written, not started"
    return
  fi
  systemctl enable --now "$SERVICE_NAME"
  health_check
}

# ---- modes ------------------------------------------------------------------
do_install() {
  have systemctl || die "host install requires systemd (use docker compose otherwise)"
  ensure_prereqs
  resolve_dotnet
  ensure_docker
  setup_user_dirs
  publish_app
  install_playwright
  ensure_runtime_image
  write_env
  write_unit
  enable_start
  cat <<EOF

Installed.
  service : systemctl status $SERVICE_NAME
  logs    : journalctl -u $SERVICE_NAME -f
  url     : http://localhost:$PORT
  secrets : $ENV_FILE (configure AgentLlm__* before real runs)
  data    : $DATA_DIR
  update  : sudo $0 update
EOF
}

do_update() {
  have git || die "git is required for update mode"
  if [ ! -f "$UNIT_FILE" ]; then
    warn "no existing install found ($UNIT_FILE missing) — running full install"
    do_install
    return
  fi
  git config --global --add safe.directory "$SRC_DIR" 2>/dev/null || true
  log "Fetching origin/$BRANCH"
  git -C "$SRC_DIR" fetch origin "$BRANCH"
  local local_rev remote_rev
  local_rev="$(git -C "$SRC_DIR" rev-parse "$BRANCH" 2>/dev/null || echo none)"
  remote_rev="$(git -C "$SRC_DIR" rev-parse FETCH_HEAD)"
  local current_branch
  current_branch="$(git -C "$SRC_DIR" rev-parse --abbrev-ref HEAD)"
  if [ "$local_rev" = "$remote_rev" ] && [ "$current_branch" = "$BRANCH" ] && [ "$FORCE" -eq 0 ]; then
    log "Already up to date ($(git -C "$SRC_DIR" rev-parse --short HEAD)) — nothing to redeploy"
    return 0
  fi
  if [ -n "$(git -C "$SRC_DIR" status --porcelain)" ]; then
    die "working tree has uncommitted changes — commit/stash them before updating"
  fi
  if [ "$current_branch" != "$BRANCH" ]; then
    log "Switching deployment checkout $current_branch -> $BRANCH"
    git -C "$SRC_DIR" checkout "$BRANCH"
  fi
  log "Updating $local_rev -> $remote_rev"
  git -C "$SRC_DIR" pull --ff-only origin "$BRANCH"
  resolve_dotnet
  ensure_docker
  publish_app
  install_playwright
  ensure_runtime_image
  if [ "$NO_START" -eq 0 ]; then
    systemctl restart "$SERVICE_NAME"
    health_check
  fi
  log "Update complete: $(git -C "$SRC_DIR" rev-parse --short HEAD)"
}

do_uninstall() {
  if [ -f "$UNIT_FILE" ] || systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
    log "Stopping and disabling $SERVICE_NAME"
    systemctl disable --now "$SERVICE_NAME" 2>/dev/null || true
  fi
  rm -f "$UNIT_FILE"
  systemctl daemon-reload 2>/dev/null || true
  rm -rf "$PREFIX"
  if [ "$PURGE" -eq 1 ]; then
    warn "purging $DATA_DIR, $ENV_FILE and user '$SERVICE_USER'"
    rm -rf "$DATA_DIR" "$(dirname "$ENV_FILE")"
    userdel "$SERVICE_USER" 2>/dev/null || true
  else
    cat <<EOF
Removed service + binaries. Kept:
  data    : $DATA_DIR   (delete manually or re-run with --purge)
  secrets : $ENV_FILE
  user    : $SERVICE_USER
EOF
  fi
}

case "$MODE" in
  install)   do_install ;;
  update)    do_update ;;
  uninstall) do_uninstall ;;
esac

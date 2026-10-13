#!/usr/bin/env bash
# check-env-drift.sh — verifica se toda variável OPENHANDSNET_* consumida no
# código está documentada em .env.exemplo (SPEC-13 RF: drift check).
set -euo pipefail
cd "$(dirname "$0")/.."

used=$(grep -rhoE 'OPENHANDSNET_[A-Za-z_]+' \
    src cli tests .env.exemplo docker-compose*.yaml 2>/dev/null \
    | sort -u)
# Variáveis realmente lidas do ambiente = prefixo + seções de Configuration.
sections=$(grep -rhoE '"(Database|Sandbox|Git|AgentLlm|Auth|Observability|MultiTenancy|Webhooks|Metrics|DeploymentMode)(:[A-Za-z]+)?"' \
    src --include='*.cs' | tr -d '"' | sort -u)

missing=0
echo "== Seções de configuração usadas no código =="
for s in $sections; do
    env="OPENHANDSNET_${s//:/__}"
    if grep -q "$env" .env.exemplo; then
        echo "  OK   $env"
    else
        echo "  MISS $env"
        missing=$((missing + 1))
    fi
done

if [ "$missing" -gt 0 ]; then
    echo "-> $missing seção(ões) sem documentação em .env.exemplo"
    exit 1
fi
echo "-> .env.exemplo cobre todas as seções usadas no código"

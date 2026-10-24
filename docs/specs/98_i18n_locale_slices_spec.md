# SPEC — 98 · i18n locale slices — preencher chaves faltantes

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `i18n-locale-slices` |
| Type | `Quality / i18n` |
| Status | `Completed` |
| Ticket | #244 (fatia restante) |
| Depends on | SPEC-92 (drift guards) entregue |
| Touches | `src/OpenHandsNET.Client/i18n/*.json`, baseline ratchet |

## 1. User Story

**As a** usuário não-en, **I want** todas as chaves traduzidas no meu
locale, **so that** a UI não cai em inglês por fallback.

## 2. Contexto

PR #245 zerou placeholders/CSS/a11y; restam ~1720 chaves ausentes por
locale (maioria `zh-CN`). Runtime já faz fallback `en-US→pt-BR` — sem
quebra, mas UX incompleta.

## 3. Requisitos

- RF-001: uma PR por locale — ordem por déficit (zh-CN primeiro).
- RF-002: `check-i18n-parity.py --write-baseline` reduz a cada PR até 0.
- RF-003: sem tradução literal de placeholders `{{x}}`/HTML — copiar
  estrutura do en-US.
- RF-004: keys novas em en-US só via PR com tradução nos 8 locales.

## 4. Acceptance

- [ ] i18n-parity baseline = 0; ratchet removido.
- [ ] Todos os 8 locales com 100% das chaves en-US.

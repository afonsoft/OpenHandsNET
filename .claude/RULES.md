# RULES.md — Guardrails summary

Índice das regras. Detalhe normativo em `CLAUDE.md` (hard/soft rules) e
`.claude/rules/`.

## Precedência

1. `CLAUDE.md` hard rules + `.claude/rules/global-rules.md` (always-on)
2. `.claude/rules/{domain}.md` (path-scoped)
3. `.claude/settings.json` `permissions.deny` (computational — não ignorable)
4. `.specs/` ativas (contrato do trabalho em curso)

## Gates que exigem confirmação humana

- Qualquer mudança em `.github/workflows/` (hard rule 6)
- Schema/migrations EF Core que alterem dados existentes
- Auth, permissões, secrets, exposição pública
- Operações destrutivas (force-push, rm de dados, drop tables)
- Push para `main` (sempre via PR + verde)

## Gates automáticos (CI)

`dotnet format --verify-no-changes` → `dotnet build -c Release` →
`dotnet test` (unit+client+integration) → golden fixtures → env drift →
docker build + compose config → sonarcloud (quando `SONAR_ENABLED`).

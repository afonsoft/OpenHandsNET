# SPEC — 108 · SonarCloud VULNERABILITY + BUG triage

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `sonar-vuln-bug-triage` |
| Type | `Fix` |
| Status | `Approved` |
| Ticket | gap-analysis 2026-10-24 · GAP-sonar-vuln-bug-triage |
| Scope | repo-wide VULNERABILITY (15) + BUG (18) abertos |

## 1. User Story

**As a** mantenedor, **I want** os findings de segurança/confiabilidade
triados e corrigidos, **so that** vulnerabilidades e bugs reais não ficam
escondidos no backlog de smells.

## 2. Contexto / escopo

Live 2026-10-24 (resolved=false):

**VULNERABILITY ×15:**
- `csharpsquid:S5332` ×8 — cleartext `http://` URIs (`Sandboxing/
  DockerSandboxEngine.cs:431,448`, `KubernetesSandboxEngine.cs:280,631`,
  `Routing/SandboxHostRouter.cs:77`, `Scm/GitHubScmProvider.cs:251`,
  `Client/Components/ChatComposer.razor:375`)
- `githubactions` ×4 — scorecard.yml:11, security-scan.yml:66×2,68
  (perms/versions)
- `csharpsquid:S5693` ×1 · `S5542` ×1 · `kubernetes:S6865` ×1
  (`charts/openhandsnet/templates/api.yaml:22`)

**BUG ×18:**
- `csharpsquid:S8949` ×11 — Task.Run/fire-and-forget sem ct
  (`cli/{ParityCommands,TuiCommand,ScmCommands}`, `Observability/
  SupportBundleService.cs:199,207`, `Sharing/TrajectoryExporter.cs:85`)
- `csharpsquid:S1751` ×1 · `css:S9424` ×4 · `Web:*` ×2 (docs artifact)

## 3. Requisitos

- RF-001: triar cada finding (real vs falso-positivo vs won't-fix
  documentado); workflow YAML changes pedem revisão humana.
- RF-002: corrigir os reais sem mudar comportamento observável;
  S5332 internos (sandbox host http) → justificar ou parametrizar.
- RF-003: testes de regressão para fixes em `src|cli` (pr-test-policy).
- RF-004: `dotnet format` + build 0 warnings + suite verde.

## 4. Acceptance

- [ ] VULNERABILITY + BUG abertos ≤ justificados documentados na SPEC.
- [ ] Findings em `docs/architecture/*.html` cobertos pela resolução
  do GAP-sonar-exclusion (won't-fix bulk ou exclusão efetiva).

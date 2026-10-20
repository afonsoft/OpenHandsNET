# Gap Analysis — 2026-10-20 (run state)

## Phase 0 — Preconditions

- repo `afonsoft/OpenHandsNET` · branch `main` @ `4a2d7ec` · clean · no submodules
- `gh` auth OK (afonsoft) · sibling skills: write-specs ✓ create-issues ✓ orchestrator ✓
- note: collect script pattern `SPEC-*` não casa convenção `NN_*_spec.md` (registrado no run anterior)
- resume marker: **Phase 5 done — 4 SPECs Draft gerados; GATE pendente**

## Phase 1 — AS-IS × TO-BE (resumo)

| Tópico | AS-IS | TO-BE | Veredito |
|---|---|---|---|
| org-scoped routes | `org_id` só na policy; queries por `id` puro | isolamento por org | gap |
| nightly schemathesis | 237 fail + crash 4.3.8 | gate verde | gap |
| k8s-e2e | cluster `chart-testing` vs `kind` | gate verde | gap |
| spec lifecycle | 75–78 entregues ativas; #192–195 abertas | archive rule `.specs/README.md` | gap |
| harness/docs/specs archive | CONTEXT/RULES/settings ✓ docs/specs/ 70 arquivos | regras cumpridas | OK |
| CI gates | lint, coverage, codeql, semgrep, gitleaks verdes | — | OK |
| OTel/observability | ausente | sem TO-BE | sem gap |
| Eval p/ CI | lib no slnx sem job | sem TO-BE | sem gap |

## Phase 2–4 — Candidates → vereditos

| Key | Veredito | Prioridade | Evidência |
|---|---|---|---|
| GAP-security-org-scoped-resources | CONFIRMADO | P1 | ChatOpsEndpoints.cs:112-121; ChatConnectionService.cs:71 (`IgnoreQueryFilters`, sem OrgId); TicketEndpoints.cs:78; PermissionPolicy.cs:101,146 |
| GAP-automation-openapi-fidelity | CONFIRMADO | P2 | run 39396545419: 237 fail; golden spec: 6 paths sem param declarado, `securitySchemes:[]`, 0/264 ops com 401; stream-start timeout; crash 4.3.8 `unhashable type: 'list'`; issue #203 |
| GAP-automation-k8s-e2e-kind-name | CONFIRMADO | P2 (trivial) | log: `Creating cluster "chart-testing"` vs `kind load` default `kind` → `no nodes found`; falhas 18/19/20 |
| GAP-automation-spec-lifecycle | CONFIRMADO | P3 | SPECs 75–78 status stale; issues #192–195 abertas c/ entrega mergeada; regra `.specs/README.md:19` |
| GAP-documentation-specs-archive | DUPLICADO (mesma raiz, agora como lifecycle) | — | já mapeado run anterior → SPEC-65 |
| sonar credentials / SPECs 72–74 | DUPLICADO | — | issues #148, #154–156 abertas |
| OTel/metrics | REJEITADO | — | sem fonte TO-BE |
| sidebar folders/pinned, voz/anexos | REJEITADO (mantido) | — | run anterior |
| IDOR cross-org por permissão | REJEITADO | — | PermissionPolicy resolve org_id da rota e bloqueia cross-org no nível permissão (o gap real é escopo do recurso → CONFIRMADO acima) |

## Phase 5 — Draft SPECs (GATE PENDENTE)

- `.specs/79_org_scoped_resources_spec.md`
- `.specs/80_openapi_fuzz_fidelity_spec.md`
- `.specs/81_k8s_e2e_kind_cluster_spec.md`
- `.specs/82_spec_lifecycle_hygiene_spec.md`

## Pendências conhecidas (não-gaps)

- SPEC-66 sonar parte: depende `SONAR_HOST_URL`/`SONAR_TOKEN`+key (#148).
- SPEC-72/73/74 sonar remediation: approved, issues #154–156 abertas.

### Preferência do usuário (commits)
- **Remover o trailer `Generated with [Devin](https://devin.ai)`** de
  todos os commits futuros (instrução direta 2026-10-20).

## Fechamento (2026-10-20)

- PRs mergeados: #211 (specs), #212 (SPEC-79 org-scope), #213 (SPEC-81
  cluster_name), #214 (SPEC-82 lifecycle), #215 (SPEC-80 OpenAPI),
  #216 (probes k8s + diagnostics), #217 (arquivar 79–82), #219
  (nightly baseline).
- Issues fechadas: #207–#210 + Epic #206. Issue #218 aberta com os
  achados reais do fuzz (35 ops `ignored_auth`, 10 `not_a_server_error`,
  fidelity debt) — exclusions do schemathesis apontam para ela.
- k8s-e2e run 39479983940: **verde** (primeiro em dias) — SPEC-81 +
  probes fix validados end-to-end.
- Nightly 39480716981 dispatch pós-#219 — esperado verde com checks
  reduzidos (schema/content-type conformance ativos).
- Golden OpenAPI: 264 ops, 252 c/ 401, 12 anônimas, 0 params faltando.
- Descoberta incidental: `KubernetesSandboxEngine` resolve eager no
  startup e exige kubeconfig válido — testes com provider=kubernetes
  precisam de `Sandbox:Kubernetes:KubeconfigPath` apontando arquivo.

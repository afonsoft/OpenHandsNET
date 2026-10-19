# docs/architecture — índice

Documentação arquitetural do OpenHandsNET. Governada pela skill `architecture`:
ADRs em `AD-NNNN-<slug>.md`, fontes Mermaid em `<name>_<type>_<title>.mmd`,
artefatos interativos via Archify.

| Artefato | Tipo | Descrição |
|---|---|---|
| [overview.md](overview.md) | design doc | Visão de camadas, dependências, persistência e tempo real |
| [system_arch_overview.mmd](system_arch_overview.mmd) | Mermaid (fonte) | Diagrama de sistema — fonte canônica do grafo em `overview.md` |
| [system_arch_overview.svg](system_arch_overview.svg) | export | Render estático do `.mmd` |
| [runtime-architecture.json](runtime-architecture.json) | Archify (spec) | Candidato validado do diagrama de runtime |
| [runtime-architecture.html](runtime-architecture.html) | Archify (HTML) | Diagrama interativo: views, evidence links, export |
| [AD-0001-harness-structure.md](AD-0001-harness-structure.md) | ADR | Estrutura do harness de agentes (SPEC-67 / issue #149) |

## Convenções

- ADRs: `AD-NNNN-<slug>.md`, sequência contínua.
- Mermaid: `<name>_<type>_<title>.mmd`; commits incluem fonte + export.
- Archify: spec `.json` + `.html` gerado por
  `archify deliver architecture <spec>.json <out>.html --repo-root .`.
- Distinto de `.specs/` (SDD ativo), `docs/specs/` (arquivo) e
  `.claude/memory/` (histórico de sessões).

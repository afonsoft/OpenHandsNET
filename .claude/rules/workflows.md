---
paths: [".github/workflows/**", ".github/**"]
---

# GitHub Workflows Rules — OpenHandsNET

- **Hard rule 6**: não modificar `.github/workflows/` sem revisão humana
  explícita — sempre sinalizar no PR body.
- Actions pinadas por SHA com comentário de versão (`uses: x@<sha> # vN.N.N`).
  Obter SHA via `gh api repos/{owner}/{action}/git/refs/tags/v{N}`.
- `ci.yml` é o gate: restore → lint (`dotnet format --verify-no-changes`) →
  build Release → unit+client → integration → env drift; `contracts` (golden);
  `docker` (imagem + compose config); `sonarcloud` gated por
  `vars.SONAR_ENABLED == 'true'` + secret `SONAR_TOKEN`.
- Não introduzir steps que exijam secrets não documentados no README/deploy.

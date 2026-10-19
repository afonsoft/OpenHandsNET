# Global Rules — OpenHandsNET

Always-on. Fonte: `CLAUDE.md` (não duplicar detalhes — este arquivo resume).

## Hard Rules (bloqueio imediato)

1. **Modelo de eventos V1**: discriminador `kind`, `id` ULID/UUID, `source`,
   `parent_id` — nunca o formato V0 (`id` int + `action`/`args`).
   Evidência: `src/OpenHandsNET.Domain/Events/EventBase.cs`.
2. **Start-task assíncrono**: `POST /app-conversations` retorna task
   (`WORKING→…→READY`), nunca `conversation_id` direto; campo é
   `selected_repository`.
3. **Licença**: upstream `enterprise/` e `OpenHands-Cloud/` são Polyform —
   implementar conceitos próprios, nunca portar código.
4. **Secrets**: nunca commitar; `sk-*`/`svc_*` só com hash SHA-256
   (`Infrastructure/Identity/ApiKeyService.cs`); segredos serializam mascarados.
5. **`main` protegida** — merge via PR com testes verdes; squash merge.
6. **`.github/workflows/`** — não modificar sem revisão humana explícita.
7. **NuGet ≥7 days** — nenhuma dependência publicada há menos de 7 dias.

## Soft Rules (aviso + confirmação)

- Docs/comentários em pt-BR; código, commits (Conventional Commits) e APIs
  em inglês (JSON `snake_case`).
- `async/await` + sufixo `Async` + `CancellationToken`; `record` imutável.
- Mudança de schema → EF migration **dupla** (SQLite + Postgres) +
  `DatabaseMigrator` no startup. Nunca editar model snapshot na mão
  (sessão 2026-10-18).
- SQLite não ordena `DateTimeOffset` → coluna companion `*Ticks` (convenção).
- Divergência de contrato vs upstream → documentar na SPEC (seção API Contract).
- SPECs entregues → `docs/specs/` (arquivo morto); `.specs/` só ativas.

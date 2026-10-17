# External / managed PostgreSQL

For production and Kubernetes deployments, point the app at a managed
PostgreSQL (RDS, CloudSQL, Azure Database, self-hosted cluster) instead
of the compose container.

## Configuration

```bash
OPENHANDSNET_Database__Provider=PostgreSql
OPENHANDSNET_Database__ConnectionString="Host=db.example.com;Port=5432;Database=openhandsnet;Username=openhandsnet;Password=***;SSL Mode=Require"
```

## Behaviour

- Migrations apply automatically on boot (`DatabaseMigrator` →
  `MigrateAsync`) — the app owns the schema; the DBA owns the server.
- `checks.db` on `/api/v1/ready` reports connectivity + migration state.
- Both providers share the same EF model (separate migration sets —
  `SqliteMigrations/` and `PostgresMigrations/`); a SQLite→PG switch is
  a start-clean operation, not an in-place conversion.

## Requirements

- PostgreSQL 16+ recommended (17 tested).
- A dedicated role; DDL rights are needed for migrations.
- TLS (`SSL Mode=Require`) on any non-localhost deployment.
- Keep the connection string out of `.env` in production — use
  `envFrom`/`existingSecret` on K8s or your orchestrator's secret store.

## Notes

- `DateTimeOffset` ordering uses `*Ticks` shadow columns for provider
  parity — don't query raw `*_at` for ordering-sensitive reports.
- Pool sizing: default Npgsql pooling is fine; the app is not
  connection-hungry (~5 concurrent under normal load).

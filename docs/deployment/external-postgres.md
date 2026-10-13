# External PostgreSQL (SPEC-14 RF-003)

Production deployments should point OpenHandsNET at an **existing**
PostgreSQL instance instead of the bundled container. Everything is driven
by the standard EF Core connection options — no code changes required.

## Connection

```bash
OPENHANDSNET_Database__Provider=PostgreSql
OPENHANDSNET_ConnectionStrings__Default=\
"Host=pg.internal.corp;Port=5432;Database=openhandsnet;\
Username=openhandsnet_app;Password=***;\
SSL Mode=Require;Trust Server Certificate=false;\
Pooling=true;Minimum Pool Size=2;Maximum Pool Size=50;\
Timeout=15;Command Timeout=30;Keepalive=30"
```

| Parameter | Recommended | Why |
|---|---|---|
| `SSL Mode` | `Require` (or `VerifyFull`) | never plaintext over the wire |
| `Pooling` | `true` | Npgsql pool; app is scoped-DbContext per request |
| `Maximum Pool Size` | `50` | one per concurrent request burst |
| `Command Timeout` | `30` | abort runaway queries; migrator overrides longer |
| `Keepalive` | `30` | detect dead TCP behind NAT/LB |
| `Application Name` | `openhandsnet` | `pg_stat_activity` observability |

Full reference: <https://www.npgsql.org/doc/connection-string-parameters.html>

## Roles & schema ownership

Least privilege — two roles:

```sql
-- Migration/DDL role (used once, or by CI):
CREATE ROLE openhandsnet_ddl LOGIN PASSWORD '***';
GRANT CREATE ON DATABASE openhandsnet TO openhandsnet_ddl;

-- Runtime role (the app):
CREATE ROLE openhandsnet_app LOGIN PASSWORD '***';
GRANT CONNECT ON DATABASE openhandsnet TO openhandsnet_app;
GRANT USAGE ON SCHEMA public TO openhandsnet_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public
  TO openhandsnet_app;
ALTER DEFAULT PRIVILEGES FOR ROLE openhandsnet_ddl IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO openhandsnet_app;
```

The `DatabaseMigrator` runs `MigrateAsync` at boot — run migrations as the
DDL role (one-off job) or grant `openhandsnet_app` `CREATE` temporarily.
All objects live in the `public` schema; `search_path` needs no override.

## Extensions

None required. Optional:

- `pg_stat_statements` — query analytics
- `citext` — only if you plan case-insensitive email lookups manually

## Readiness

`/api/v1/readiness` runs a real `SELECT 1` via EF (`database` check) and
`openhandsnet doctor` surfaces it client-side. A 503 there means the app
cannot reach or authenticate to PG — check `logs/openhandsnet-*.log`.

Upstream reference: <https://docs.openhands.dev/enterprise/external-postgres>

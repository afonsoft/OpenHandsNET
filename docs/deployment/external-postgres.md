# External PostgreSQL (SPEC-14 RF-003)

Production deployments should point Sandcastle at an **existing**
PostgreSQL instance instead of the bundled container. Everything is driven
by the standard EF Core connection options — no code changes required.

## Requisitos

- **PostgreSQL ≥ 16.4** (17 suportado) — paridade com o mínimo upstream;
  EF Core 10/Npgsql cobrem 16 e 17.
- **Encoding UTF8 em todos os databases** — LATIN1/outros quebram
  migrations no install/upgrade. Verifique e force na criação:

```sql
SELECT datname, pg_encoding_to_char(encoding) AS encoding FROM pg_database;
CREATE DATABASE sandcastle WITH ENCODING 'UTF8';
```

- TLS (`SSL Mode=Require`/`VerifyFull`) em qualquer deploy não-localhost.

> **Divergência vs upstream** (hard rule 4): o upstream usa 5 databases —
> `openhands`, `bitnami_keycloak`, `litellm`, `runtime_api_db`,
> `automations`. O Sandcastle usa **um** database de aplicação
> (`sandcastle`) + `litellm` opcional criado por
> `deploy/postgres-init/00-litellm-db.sh` no compose enterprise. Em k8s,
> crie o db `litellm` manualmente se o chart habilitar o proxy.

## Connection

```bash
SANDCASTLE_Database__Provider=PostgreSql
SANDCASTLE_ConnectionStrings__Default=\
"Host=pg.internal.corp;Port=5432;Database=sandcastle;\
Username=sandcastle_app;Password=***;\
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
| `Application Name` | `sandcastle` | `pg_stat_activity` observability |

Full reference: <https://www.npgsql.org/doc/connection-string-parameters.html>

## Roles & schema ownership

Least privilege — two roles:

```sql
-- Migration/DDL role (used once, or by CI):
CREATE ROLE sandcastle_ddl LOGIN PASSWORD '***';
GRANT CREATE ON DATABASE sandcastle TO sandcastle_ddl;

-- Runtime role (the app):
CREATE ROLE sandcastle_app LOGIN PASSWORD '***';
GRANT CONNECT ON DATABASE sandcastle TO sandcastle_app;
GRANT USAGE ON SCHEMA public TO sandcastle_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public
  TO sandcastle_app;
ALTER DEFAULT PRIVILEGES FOR ROLE sandcastle_ddl IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO sandcastle_app;
```

The `DatabaseMigrator` runs `MigrateAsync` at boot — run migrations as the
DDL role (one-off job) or grant `sandcastle_app` `CREATE` temporarily.
All objects live in the `public` schema; `search_path` needs no override.

## Extensions

None required. Optional:

- `pg_stat_statements` — query analytics
- `citext` — only if you plan case-insensitive email lookups manually

## Readiness

`/api/v1/readiness` runs a real `SELECT 1` via EF (`database` check) and
`sandcastle doctor` surfaces it client-side. A 503 there means the app
cannot reach or authenticate to PG — check `logs/sandcastle-*.log`.

Upstream reference: <https://docs.openhands.dev/enterprise/external-postgres>

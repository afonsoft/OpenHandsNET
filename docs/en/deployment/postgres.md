# Deploy with PostgreSQL

Recommended self-hosted tier: app + PostgreSQL 17, migrations applied
automatically on boot (`DatabaseMigrator` → `MigrateAsync`).

## Bring it up

```bash
cp .env.exemplo .env
# set POSTGRES_PASSWORD in .env
docker compose -f docker-compose.yaml -f docker-compose.postgres.yaml up -d --build
```

`docker-compose.postgres.yaml`:

1. Starts `postgres:17-alpine` with `POSTGRES_DB=openhandsnet` and a
   healthcheck.
2. Repoints the app to `Database__Provider=PostgreSql` +
   `Host=postgres` in the connection string.
3. `depends_on: service_healthy` ensures PG is ready before the app.

## Verification

```bash
docker compose -f docker-compose.yaml -f docker-compose.postgres.yaml ps
docker logs openhandsnet | grep -i "migration"
curl -fsS http://localhost:3032/api/v1/readiness | jq .checks.db
```

## Migrating from SQLite

Export the data or accept starting clean: the schema is identical on
both providers (same EF model, separate migration sets in
`SqliteMigrations/` and `PostgresMigrations/`). There is no built-in
data converter — a provider switch starts a fresh schema.

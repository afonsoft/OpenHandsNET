# Deploy com PostgreSQL

Tier recomendado para self-hosted: app + PostgreSQL 17, migrations automáticas
no boot (`DatabaseMigrator` → `MigrateAsync`).

## Subir

```bash
cp .env.exemplo .env
# defina POSTGRES_PASSWORD no .env
docker compose -f docker-compose.yaml -f docker-compose.postgres.yaml up -d --build
```

O arquivo `docker-compose.postgres.yaml`:

1. Sobe `postgres:17-alpine` com `POSTGRES_DB=openhandsnet` e healthcheck.
2. Reaponta o app para `Database__Provider=PostgreSql` +
   `Host=postgres` no connection string.
3. `depends_on: service_healthy` garante PG pronto antes do app.

## Verificação

```bash
docker compose -f docker-compose.yaml -f docker-compose.postgres.yaml ps
docker logs openhandsnet | grep -i "migration"
curl -fsS http://localhost:3032/api/v1/readiness | jq .checks.db
```

## Migrando do SQLite

Exporte os dados ou aceite começar limpo: o schema é idêntico nos dois
providers (mesmo modelo EF, migration sets separadas em
`Persistence/Migrations/Postgres`). Não há ferramenta de migração de dados
entre providers na v1.

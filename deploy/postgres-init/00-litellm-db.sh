#!/bin/bash
# SPEC-50: cria o database `litellm` para o proxy persistir virtual
# keys/budgets (roda uma vez no primeiro boot do volume postgres).
set -e
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" <<-SQL
    SELECT 'CREATE DATABASE litellm OWNER $POSTGRES_USER'
    WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'litellm')\gexec
SQL

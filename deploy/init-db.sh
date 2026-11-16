#!/bin/sh
# The forms database's roles — the part MorphDB cannot create for itself. The service logs in as `morph`, which owns the
# database and is neither a superuser nor BYPASSRLS (PostgreSQL applies no row-level security to either, so MorphDB refuses
# to start on one); `morphdb_bypass` is the role a master-secret request is switched to. Formbase keeps its own database.
# The passwords come from the environment file the install step wrote — none is fixed in this repository.
set -eu
psql -v ON_ERROR_STOP=1 --username postgres --dbname "$POSTGRES_DB" <<SQL
CREATE ROLE morph LOGIN PASSWORD '${MORPH_PASSWORD}' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
DO \$\$ BEGIN EXECUTE format('ALTER DATABASE %I OWNER TO morph', current_database()); END \$\$;
CREATE ROLE morphdb_bypass NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
GRANT morphdb_bypass TO morph;
CREATE ROLE formbase LOGIN PASSWORD '${FORMBASE_PASSWORD}';
CREATE DATABASE formbase OWNER formbase;
SQL

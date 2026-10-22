-- The forms source's database: the one role MorphDB cannot create for itself. The service logs in as
-- morph, which owns the database and is neither a superuser nor BYPASSRLS -- PostgreSQL applies no
-- row-level security to either, so MorphDB refuses to start on one. morphdb_bypass is the role a
-- master-secret request is switched to. Local-only password.
CREATE ROLE morph LOGIN PASSWORD 'rig-morph-local-only' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
DO $$ BEGIN EXECUTE format('ALTER DATABASE %I OWNER TO morph', current_database()); END $$;
CREATE ROLE morphdb_bypass NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
GRANT morphdb_bypass TO morph;

-- Formbase's own database, for a Formbase host projecting into the forms source: the raw stream,
-- projection state and declarations live here, the projected tables in MorphDB.
CREATE ROLE formbase LOGIN PASSWORD 'rig-formbase-local-only';
CREATE DATABASE formbase OWNER formbase;

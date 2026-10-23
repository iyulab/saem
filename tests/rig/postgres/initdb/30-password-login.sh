#!/bin/sh
# Runs once, after 10-oauth.sh and 20-mes.sql, and only in the password-login profile (compose.password-login.yaml).
#
# Interim, and not what the rig proves: .NET has no client for PostgreSQL's OAuth login yet (Npgsql
# #6156), so saem's own source adapter cannot log in with a person's token. Until it can, this profile
# gives the people the rig acts for a password login of their own, so the adapter's path --
# a session opened as the person, the source's row rules deciding what it sees -- can run end to end.
# It shows the session is the person's; it does not show the person's token reached the source.
# The default rig keeps tokens as the only credential, and the people's own roles keep them here too.
set -eu

[ "${RIG_PASSWORD_LOGIN:-off}" = "on" ] || exit 0

# A password login of its own for each person -- alice_pw, bob_pw -- a member of that person's role, so
# the person's row rules and grants (written TO alice, TO bob) are what the login reads under. The
# person's own role keeps the token as its only credential, and the token probes run unchanged.
# Fifty synthetic people, u01..u50, are measured as many users at once: the odd ones see what alice
# sees, the even ones what bob sees, so the two visibilities stay apart.
psql -v ON_ERROR_STOP=1 --username postgres --dbname postgres <<'SQL'
CREATE ROLE rig_password_login NOLOGIN;
CREATE ROLE alice_pw LOGIN PASSWORD 'rig-alice-local-only' IN ROLE alice, rig_password_login;
CREATE ROLE bob_pw LOGIN PASSWORD 'rig-bob-local-only' IN ROLE bob, rig_password_login;
ALTER ROLE alice_pw SET default_transaction_read_only = on;
ALTER ROLE bob_pw SET default_transaction_read_only = on;
-- The groups' accounts, which materialization reads as (saem D-22): one login per group, a member of
-- the group's role, so the group's row rules are what a pass reads under.
CREATE ROLE g_plant_pw LOGIN PASSWORD 'rig-plant-local-only' IN ROLE g_plant, rig_password_login;
CREATE ROLE g_office_pw LOGIN PASSWORD 'rig-office-local-only' IN ROLE g_office, rig_password_login;
ALTER ROLE g_plant_pw SET default_transaction_read_only = on;
ALTER ROLE g_office_pw SET default_transaction_read_only = on;
DO $$
BEGIN
    FOR i IN 1..50 LOOP
        EXECUTE format('CREATE ROLE %I LOGIN PASSWORD %L IN ROLE %I, rig_password_login',
            'u' || lpad(i::text, 2, '0'), 'rig-synthetic-local-only', CASE WHEN i % 2 = 1 THEN 'alice' ELSE 'bob' END);
        EXECUTE format('ALTER ROLE %I SET default_transaction_read_only = on', 'u' || lpad(i::text, 2, '0'));
    END LOOP;
END
$$;
SQL

# A password crosses the wire, so these logins take TLS only: a self-signed certificate made here, for
# this rig and nothing else.
openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=mes" \
    -keyout "$PGDATA/server.key" -out "$PGDATA/server.crt" 2>/dev/null
chmod 600 "$PGDATA/server.key"
echo "ssl = on" >> "$PGDATA/postgresql.conf"

# Password logins for the members of rig_password_login only, over TLS, ahead of the token line; every
# other login needs a token.
{
    head -n 2 "$PGDATA/pg_hba.conf"
    echo "hostssl all       +rig_password_login  all  scram-sha-256"
    tail -n +3 "$PGDATA/pg_hba.conf"
} > "$PGDATA/pg_hba.conf.new"
mv "$PGDATA/pg_hba.conf.new" "$PGDATA/pg_hba.conf"

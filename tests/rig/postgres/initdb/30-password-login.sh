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
psql -v ON_ERROR_STOP=1 --username postgres --dbname postgres <<'SQL'
CREATE ROLE alice_pw LOGIN PASSWORD 'rig-alice-local-only' IN ROLE alice;
CREATE ROLE bob_pw LOGIN PASSWORD 'rig-bob-local-only' IN ROLE bob;
ALTER ROLE alice_pw SET default_transaction_read_only = on;
ALTER ROLE bob_pw SET default_transaction_read_only = on;
SQL

# Password logins for those two roles only, ahead of the token line; every other login needs a token.
{
    head -n 2 "$PGDATA/pg_hba.conf"
    echo "host    all       alice_pw,bob_pw  all  scram-sha-256"
    tail -n +3 "$PGDATA/pg_hba.conf"
} > "$PGDATA/pg_hba.conf.new"
mv "$PGDATA/pg_hba.conf.new" "$PGDATA/pg_hba.conf"

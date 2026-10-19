#!/bin/sh
# Runs once, when the entrypoint initialises the data directory.
#
# Who may log in how:
#   - the superuser over the local socket only (fixture setup and inspection);
#   - everyone else over TCP with an OAuth bearer token from the rig's issuer, carrying the scope
#     this database requires; the token's subject is mapped to a login role by pg_ident.
# There is no password login for the people the rig acts for: a token is the only credential.
set -eu

cat >> "$PGDATA/postgresql.conf" <<'EOF'
shared_preload_libraries = 'pg_oidc_validator'
oauth_validator_libraries = 'pg_oidc_validator'
pg_oidc_validator.authn_field = 'sub'
log_connections = 'authentication,authorization'
# saem names each session after the lookup it reads for (application_name), so a lookup in saem's log
# and its login here can be matched: the 'connection authorized' line carries user= and
# application_name=, and every later line of the session carries both in its prefix.
log_line_prefix = '%m [%p] user=%u app=%a '
EOF

cat > "$PGDATA/pg_hba.conf" <<EOF
# TYPE  DATABASE  USER  ADDRESS  METHOD
local   all       all            trust
host    all       all   all      oauth  scope="${RIG_OAUTH_SCOPE}",issuer=${RIG_OAUTH_ISSUER},map=rig
EOF

cat > "$PGDATA/pg_ident.conf" <<'EOF'
# MAPNAME  SYSTEM-USERNAME (token sub)  DATABASE-USERNAME
rig        alice                        alice
rig        bob                          bob
EOF

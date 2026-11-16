#!/bin/sh
# Writes deploy/.env: the ports and every value that authorizes something, generated here and kept only in that file.
# Needs nothing but a POSIX shell. Run it once; run it again only to start over (it refuses to overwrite).
set -eu
cd "$(dirname "$0")"
if [ -e .env ]; then
  echo "deploy/.env already exists — remove the installation first (docker compose down -v) and delete it to generate new values." >&2
  exit 1
fi
secret() { od -An -tx1 -N24 /dev/urandom | tr -d ' \n'; }
hex16() { od -An -tx1 -N16 /dev/urandom | tr -d ' \n'; }
guid() { printf '%s' "$1" | sed 's/\(........\)\(....\)\(....\)\(....\)\(............\)/\1-\2-\3-\4-\5/'; }
source_id=$(hex16); plant_id=$(hex16); office_id=$(hex16)
umask 077
cat > .env <<ENV
# Written by install.sh. Holds secrets: keep it out of version control and out of backups you do not trust.
SAEM_ISSUER_PORT=58080
SAEM_CONSOLE_PORT=58081
SAEM_MCP_PORT=58082
SAEM_PG_PASSWORD=$(secret)
SAEM_MORPH_DB_PASSWORD=$(secret)
SAEM_FORMBASE_DB_PASSWORD=$(secret)
SAEM_FORMS_MASTER_SECRET=$(secret)
SAEM_SOURCE_PROJECT_ID=$(guid "$source_id")
SAEM_SOURCE_SCHEMA=fb_$source_id
SAEM_G_PLANT_PROJECT_ID=$(guid "$plant_id")
SAEM_G_PLANT_SCHEMA=fb_$plant_id
SAEM_G_OFFICE_PROJECT_ID=$(guid "$office_id")
SAEM_G_OFFICE_SCHEMA=fb_$office_id
SAEM_CONSOLE_CLIENT_SECRET=$(secret)
SAEM_EXCHANGE_CLIENT_SECRET=$(secret)
SAEM_G_PLANT_ACCESS_KEY=$(secret)
SAEM_G_OFFICE_ACCESS_KEY=$(secret)
SAEM_G_PLANT_EXECUTOR_SECRET=$(secret)
SAEM_G_OFFICE_EXECUTOR_SECRET=$(secret)
ENV
echo "Wrote deploy/.env"

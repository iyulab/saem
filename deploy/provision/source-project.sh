#!/bin/sh
# Creates the forms source's MorphDB project under the id the environment file names. Safe to run again: a project that is
# already there answers 409 and is left as it is.
set -eu
forms="http://forms:8080"
/provision/wait-http.sh "$forms/health"
code=$(curl -sS -o /tmp/created -w '%{http_code}' -X POST "$forms/api/projects" \
  -H "Authorization: Bearer $SAEM_FORMS_MASTER_SECRET" -H 'Content-Type: application/json' \
  -d "{\"name\":\"forms-source\",\"projectId\":\"$SAEM_SOURCE_PROJECT_ID\"}")
case "$code" in
  200|201) echo "source project created" ;;
  409) echo "source project already there" ;;
  *) echo "creating the source project failed ($code): $(cat /tmp/created)" >&2; exit 1 ;;
esac

#!/bin/sh
# Each group gets, on the forms source, an account of its own (a secret confined to the source project whose role is the group's
# — so the source's row rules decide what the group may see) and a store of its own (a MorphDB project, under the id the
# environment file names, and a secret confined to it). MorphDB issues the secrets and shows each once, so they are kept in
# /secrets/<group>.env for the services that need them and for nobody else. A group whose file is there is left as it is.
set -eu
forms="http://forms:8080"
/provision/wait-http.sh "$forms/health"
master="Authorization: Bearer $SAEM_FORMS_MASTER_SECRET"

issue() { # name role project -> the plaintext secret
  curl -fsS -X POST "$forms/api/security/secrets" -H "$master" -H 'Content-Type: application/json' \
    -d "$(jq -nc --arg n "$1" --arg r "$2" --arg p "$3" '{name:$n,role:$r,projectId:$p}')" | jq -r .secret
}

group() { # group store-project-id
  g="$1"; store="$2"; role=$(printf '%s' "$g" | tr '-' '_'); file="/secrets/$g.env"
  [ -e "$file" ] && { echo "$g already set up"; return; }
  code=$(curl -sS -o /tmp/created -w '%{http_code}' -X POST "$forms/api/projects" -H "$master" -H 'Content-Type: application/json' \
    -d "$(jq -nc --arg id "$store" --arg n "$g-store" '{name:$n,projectId:$id}')")
  case "$code" in 200|201|409) ;; *) echo "creating the store project of $g failed ($code): $(cat /tmp/created)" >&2; exit 1 ;; esac
  source_secret=$(issue "$g-source" "$role" "$SAEM_SOURCE_PROJECT_ID")
  store_secret=$(issue "$g-store" "$role" "$store")
  umask 077
  { printf "export SAEM_SOURCE_SECRET_FORMS='%s'\n" "$source_secret"; printf "export Formbase__MorphDb__Secret='%s'\n" "$store_secret"; } > "$file.part"
  mv "$file.part" "$file"
  echo "$g set up"
}

group g-plant "$SAEM_G_PLANT_PROJECT_ID"
group g-office "$SAEM_G_OFFICE_PROJECT_ID"

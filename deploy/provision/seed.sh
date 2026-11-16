#!/bin/sh
# The sample the install is tried on: two form types (items, work orders) with documents filed into the source Formbase, the
# source's row rules (who sees which row), the groups' files, and the sample catalog waiting as a draft for a person to
# confirm in the console. A rig on synthetic rows, not data: the CANARY values are marks that must reach no one who may not read
# their row. Safe to run again: once the sample is in, a marker says so and nothing is filed twice.
set -eu
fb="http://formbase:8080"; forms="http://forms:8080"; data=/data
[ -e "$data/seed/done" ] && { echo "sample already in"; exit 0; }
/provision/wait-http.sh "$fb/health"
master="Authorization: Bearer $SAEM_FORMS_MASTER_SECRET"; project="X-Project-Id: $SAEM_SOURCE_PROJECT_ID"
mkdir -p "$data/seed/pre" "$data/groups/g-plant" "$data/groups/g-office"

post() { curl -fsS -X "$1" "$2" -H 'Content-Type: application/json' ${4:+-H "$4"} ${3:+-d "$3"}; }
declare_type() { post PUT "$fb/formtypes/$1/declaration" "$2" >/dev/null; }
declare_type items '{"tableName":"items","declarationVersion":1,"fields":[{"name":"item_code","type":"text","nullable":false},{"name":"item_name","type":"text","nullable":false},{"name":"owner","type":"text","nullable":false}],"relations":[]}'
declare_type work_orders '{"tableName":"work_orders","declarationVersion":1,"fields":[{"name":"wo_no","type":"text","nullable":false},{"name":"item_code","type":"text","nullable":false},{"name":"qty","type":"integer","nullable":false},{"name":"owner","type":"text","nullable":false}],"relations":[{"name":"item","kind":"reference","target":"items","keyField":"item_code"}]}'

ids='{}'
file_item() { # code name owner [keyed]
  id=$(post POST "$fb/formtypes/items/documents${4:+?recordKey=$1}" "$(jq -nc --arg c "$1" --arg n "$2" --arg o "$3" '{item_code:$c,item_name:$n,owner:$o}')" | jq -r .documentId)
  ids=$(printf '%s' "$ids" | jq -c --arg k "$1" --arg v "$id" '.[$k]=$v')
}
file_wo() { # wo item qty owner [keyed]
  id=$(post POST "$fb/formtypes/work_orders/documents${5:+?recordKey=$1}" "$(jq -nc --arg w "$1" --arg i "$2" --argjson q "$3" --arg o "$4" '{wo_no:$w,item_code:$i,qty:$q,owner:$o}')" | jq -r .documentId)
  ids=$(printf '%s' "$ids" | jq -c --arg k "$1" --arg v "$id" '.[$k]=$v')
}
# Alice files the plant's first items and orders (the canary item is one only she may read); bob files rows of his own, so a
# refusal for him can be told from a blanket denial.
file_item I-100 CANARY-5e02-hex-bolt alice
file_item I-200 "Hex nut M8" alice
file_item I-300 "Ball bearing 6204" alice
file_wo WO-2401 I-100 500 alice
file_wo WO-2402 I-300 40 alice
file_wo WO-2403 I-200 500 alice
file_wo WO-2404 I-300 12 alice
file_item I-400 "Washer M6" bob
file_wo WO-2501 I-100 7 bob
file_wo WO-2502 I-400 90 bob
# Olive files for the office; pat holds an order only a plant manager may read (the second canary: its key is the mark); the
# person in no group has a row of their own.
file_item I-500 "Gasket 40mm" olive keyed
file_item I-600 "Spacer M5" nobody keyed
file_wo WO-2701 I-300 25 olive keyed
file_wo WO-2702 I-500 60 olive keyed
file_wo CANARY-8c3d-wo-restricted I-300 777 pat keyed
file_wo WO-3001 I-600 5 nobody keyed

for t in items work_orders; do post POST "$fb/formtypes/$t/projection" >/dev/null; done

# Who reads which row, one rule for each table: a person sees what they filed; a group's account is its role; pat sees all;
# quinn all but pat's rows and the canary item's; olive and the office only olive's rows, and not the canary item's.
rule="owner = {{user_id}} OR {{role}} = 'g_plant' OR {{user_id}} = 'pat' OR ({{user_id}} = 'quinn' AND owner <> 'pat' AND item_code <> 'I-100') OR ({{role}} = 'g_office' AND owner = 'olive' AND item_code <> 'I-100')"
for t in items work_orders; do
  curl -fsS -X POST "$forms/api/security/policies" -H "$master" -H "$project" -H 'Content-Type: application/json' \
    -d "$(jq -nc --arg t "$t" --arg e "$rule" '{name:("who_reads_"+$t),tableName:$t,policyType:"Select",expression:$e}')" >/dev/null
done

# What the sources say (cited by the form document), the catalog in force (empty until a person confirms the draft), and the
# sample catalog as that draft.
printf '[]' > "$data/catalog.json"
cp /provision/sample/draft.json "$data/draft.json"
for g in g-plant g-office; do
  jq -nc --arg p "$SAEM_SOURCE_PROJECT_ID" '{forms:{morphdb:"http://forms:8080",projectId:$p}}' > "$data/groups/$g/sources.json"
done
# The documents as filed, byte for byte: a check reads them again and they must not have changed.
printf '%s' "$ids" > "$data/seed/documents.json"
for id in $(printf '%s' "$ids" | jq -r '.[]'); do curl -fsS "$fb/documents/$id" > "$data/seed/pre/$id.json"; done
touch "$data/seed/done"
echo "sample in: $(printf '%s' "$ids" | jq 'length') documents"

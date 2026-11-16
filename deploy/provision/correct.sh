#!/bin/sh
# Files a correction of one work order in the source — WO-2701 moves to item I-500 (an append under the same record key) — and
# projects it, so a group's "Only what changed" has something to read. The documents already filed are not touched.
set -eu
fb="http://formbase:8080"
/provision/wait-http.sh "$fb/health"
curl -fsS -X POST "$fb/formtypes/work_orders/documents?recordKey=WO-2701" -H 'Content-Type: application/json' \
  -d '{"wo_no":"WO-2701","item_code":"I-500","qty":25,"owner":"olive"}' >/dev/null
curl -fsS -X POST "$fb/formtypes/work_orders/projection" >/dev/null
echo "WO-2701 now points at item I-500"

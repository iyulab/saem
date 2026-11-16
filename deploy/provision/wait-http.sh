#!/bin/sh
# wait-http.sh <url> [seconds]: returns when the address answers with a success status, fails when it does not in time.
url="$1"; limit="${2:-120}"; waited=0
until curl -fsS -o /dev/null "$url"; do
  waited=$((waited + 2)); [ "$waited" -lt "$limit" ] || { echo "$url did not answer within ${limit}s" >&2; exit 1; }
  sleep 2
done

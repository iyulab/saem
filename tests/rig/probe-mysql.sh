#!/bin/sh
# Q1 on MySQL (Percona Server's OpenID Connect plugin), server side: the same questions as
# probe-postgres.sh, asked of a different vendor's source. Each case asks the issuer for a token
# inside the rig's network, writes it to a file (the only way the mysql client takes one), logs in
# over TLS and runs one query. Prints one line per case; exits non-zero when any case is not as
# expected.
#
# Run from the repository root with the rig up (see compose.yaml).
set -u

compose="docker compose -f tests/rig/compose.yaml"
failures=0

# A source still starting refuses every login, which reads exactly like the refusals tested here.
health=$($compose ps --format '{{.Health}}' erp 2>/dev/null)
[ "$health" = "healthy" ] || { echo "NOT RUN  erp is not healthy ($health) -- wait for compose up --wait"; exit 2; }

# token <issuer-id> <client> <audience> -> prints the access token
token() {
    $compose exec -T erp curl -sf -X POST "http://issuer:8080/$1/token" \
        -d grant_type=client_credentials -d "client_id=$2" -d client_secret=unused \
        -d scope=openid -d "audience=$3" | sed -n 's/.*"access_token" *: *"\([^"]*\)".*/\1/p'
}

# login <user> <token> <sql> -> prints the result, or the error; exit status is the client's
login() {
    $compose exec -T erp sh -c '
        f=$(mktemp); printf "%s" "$2" > "$f"
        mysql -h erp --ssl-mode=REQUIRED -u "$1" -N -B \
              --authentication-openid-connect-client-id-token-file="$f" -e "$3" 2>&1
        s=$?; rm -f "$f"; exit $s' sh "$1" "$2" "$3"
}

# expect <case> <want: ok|refused> <output-pattern> <user> <token> <sql>
expect() {
    case_name=$1; want=$2; pattern=$3
    out=$(login "$4" "$5" "$6"); status=$?
    got=refused; [ $status -eq 0 ] && got=ok
    if [ "$got" = "$want" ] && printf '%s' "$out" | grep -Eq -- "$pattern"; then
        echo "ok    $case_name -- $out"
    else
        echo "FAIL  $case_name -- wanted $want matching /$pattern/, got $got: $out"
        failures=$((failures + 1))
    fi
}

alice=$(token rig alice saem-rig-erp)
bob=$(token rig bob saem-rig-erp)
[ -n "$alice" ] && [ -n "$bob" ] || { echo "FAIL  the issuer did not hand out tokens"; exit 1; }

expect "alice's token logs in as alice"           ok      '^alice@%$' alice "$alice" "select current_user()"
expect "bob's token logs in as bob"               ok      '^bob@%$'   bob   "$bob"   "select current_user()"
expect "alice sees both suppliers"                ok      '^2$'       alice "$alice" "select count(*) from erp.visible_suppliers"
expect "bob sees only S-1"                        ok      '^1$'       bob   "$bob"   "select count(*) from erp.visible_suppliers"
# The canary must be visible to the one person allowed it, or bob's 0 below proves nothing.
expect "alice sees the canary"                    ok      '^1$'       alice "$alice" "select count(*) from erp.visible_suppliers where supplier_name like 'CANARY%'"
expect "the canary is not visible to bob"         ok      '^0$'       bob   "$bob"   "select count(*) from erp.visible_suppliers where supplier_name like 'CANARY%'"
expect "the tables themselves are not readable"   refused 'denied'    alice "$alice" "select count(*) from erp.suppliers"
expect "a write is refused by the source"         refused 'denied'    alice "$alice" "delete from erp.visible_suppliers"
expect "bob's token cannot log in as alice"       refused 'denied|fail' alice "$bob" "select 1"
expect "a token for another audience is refused"  refused 'denied|fail' alice "$(token rig alice saem-rig-mes)" "select 1"
expect "a token from another issuer is refused"   refused 'denied|fail' alice "$(token elsewhere alice saem-rig-erp)" "select 1"

echo "waiting out the 20 s token lifetime ..."
sleep 25
expect "an expired token is refused"              refused 'denied|fail' alice "$alice" "select 1"

# The client sees "Access denied" for every refusal; the plugin's notes in the server log say why.
log=$($compose logs --no-log-prefix erp 2>&1)
reason() {
    if printf '%s' "$log" | grep -q -- "$2"; then echo "ok    refused for the right reason: $1"
    else echo "FAIL  no server log line for: $1 (/$2/)"; failures=$((failures + 1)); fi
}
reason "bob's token as alice -> the account's subject"  "auth_openid_connect reported: 'claim value does not match expected value'"
reason "another audience -> audience"                   "auth_openid_connect reported: 'audience not authorized'"
reason "another issuer -> issuer"                       "auth_openid_connect reported: 'Token not issued by http://issuer:8080/rig'"
reason "an expired token -> expiry"                     "auth_openid_connect reported: 'token expired'"

[ $failures -eq 0 ] && echo "all cases as expected" || echo "$failures case(s) not as expected"
exit $failures

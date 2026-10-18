#!/bin/sh
# Q1 on PostgreSQL, server side: does the source itself accept a token as a person's login, map it to
# that person's role, enforce its own row rules for that role -- and refuse the tokens it should?
# Each case asks the issuer for a token inside the rig's network, then logs in with it through the
# validator's libpq client (oauth_conn_test) and runs one query. Prints one line per case and exits
# non-zero when any case does not come out as expected.
#
# Run from the repository root with the rig up (see compose.yaml).
set -u

compose="docker compose -f tests/rig/compose.yaml"
failures=0

# token <issuer-id> <client> <scope> <audience> -> prints the access token
token() {
    $compose exec -T mes curl -sf -X POST "http://issuer:8080/$1/token" \
        -d grant_type=client_credentials -d "client_id=$2" -d client_secret=unused \
        -d "scope=$3" -d "audience=$4" | sed -n 's/.*"access_token" *: *"\([^"]*\)".*/\1/p'
}

# login <user> <token> <sql> -> prints the first row, or the error; exit status is the client's
login() {
    $compose exec -T -e PGOAUTHDEBUG=UNSAFE mes oauth_conn_test \
        "host=mes dbname=postgres user=$1 oauth_issuer=http://issuer:8080/rig oauth_client_id=saem-rig" "$3" "$2" 2>&1
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

alice=$(token rig alice saem-rig saem-rig-mes)
bob=$(token rig bob saem-rig saem-rig-mes)
[ -n "$alice" ] && [ -n "$bob" ] || { echo "FAIL  the issuer did not hand out tokens"; exit 1; }

expect "alice's token logs in as alice"           ok      '^alice$'  alice "$alice" "select current_user"
expect "bob's token logs in as bob"               ok      '^bob$'    bob   "$bob"   "select current_user"
expect "alice sees all three work orders"         ok      '^3$'      alice "$alice" "select count(*) from work_orders"
expect "bob sees only line 1"                     ok      '^2$'      bob   "$bob"   "select count(*) from work_orders"
expect "the canary is not visible to bob"         ok      '^0$'      bob   "$bob"   "select count(*) from work_orders where note like 'CANARY%'"
expect "a write is refused by the source"         refused 'read-only' alice "$alice" "delete from work_orders"
expect "bob's token cannot log in as alice"       refused 'fail|denied|not authorized|map' alice "$bob" "select current_user"
expect "a token without the scope is refused"     refused 'fail|denied|not authorized' alice "$(token rig alice other saem-rig-mes)" "select 1"
expect "a token from another issuer is refused"   refused 'fail|denied|not authorized|issuer' alice "$(token elsewhere alice saem-rig saem-rig-mes)" "select 1"

# Audience: the validator checks signature, issuer, expiry and scope -- not aud (1.0.0). Recorded as
# observed, not as wanted: this case documents what the source does today.
other_aud=$(token rig alice saem-rig some-other-api)
out=$(login alice "$other_aud" "select current_user"); status=$?
if [ $status -eq 0 ]; then echo "note  a token for another audience is ACCEPTED (aud is not checked) -- $out"
else echo "note  a token for another audience is refused -- $out"; fi

echo "waiting out the 20 s token lifetime ..."
sleep 25
expect "an expired token is refused"              refused 'fail|denied|not authorized|expire' alice "$alice" "select 1"

# The client sees one message for every refusal; the source's log says why. Each refusal above must
# have been refused for its own reason, not for some other one that happens to look the same.
log=$($compose logs --no-log-prefix mes 2>&1)
reason() {
    if printf '%s' "$log" | grep -q -- "$2"; then echo "ok    refused for the right reason: $1"
    else echo "FAIL  no server log line for: $1 (/$2/)"; failures=$((failures + 1)); fi
}
reason "bob's token as alice -> the identity map"      'no match in usermap "rig" for user "alice" authenticated as "bob"'
reason "a token without the scope -> scope"            'scope mismatch. Required scopes: saem-rig. Received scopes: other'
reason "another issuer's token -> its signing key"     'OAuth validation failed with exception: wrong algorithm'
reason "an expired token -> expiry"                    'OAuth validation failed with exception: token expired'

# Every login above took two connections: libpq first connects without a token to learn the issuer
# and scope from the server, then reconnects with the token. That is the price of one login.
attempts=$(printf '%s' "$log" | grep -c 'OAuth bearer authentication failed for user')
echo "note  first-contact refusals logged (one per login, before the token is sent, plus real refusals): $attempts"

[ $failures -eq 0 ] && echo "all cases as expected" || echo "$failures case(s) not as expected"
exit $failures

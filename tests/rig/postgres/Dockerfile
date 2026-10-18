# PostgreSQL 18 that accepts an OAuth bearer token as a login: SASL OAUTHBEARER on the wire, the
# token checked by pg_oidc_validator (Percona, Apache-2.0) against the issuer's published keys.
# Also carries oauth_conn_test, the validator's own libpq client that logs in with a token it is
# handed -- psql's only OAuth path is the interactive device flow, which a test cannot drive.
#
# Build context: this directory. Versions are pinned; a newer validator is a deliberate change here.

ARG PG_IMAGE=postgres:18.6-trixie
# 1.1.0 (2026-08-13): the newest version, and the first to carry oauth_conn_test. There is no tag for it.
ARG VALIDATOR_REF=ba9c4cb2dc9bd2e9779c7a33f7a30c9bb8c97862

FROM ${PG_IMAGE} AS build
ARG VALIDATOR_REF
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      build-essential git ca-certificates libcurl4-openssl-dev postgresql-server-dev-18 libpq-dev \
 && rm -rf /var/lib/apt/lists/*
RUN git clone https://github.com/percona/pg_oidc_validator.git /src \
 && git -C /src checkout --quiet "${VALIDATOR_REF}"
WORKDIR /src
RUN make USE_PGXS=1 -j"$(nproc)" \
 && make USE_PGXS=1 install \
 && make USE_PGXS=1 oauth_conn_test

FROM ${PG_IMAGE}
RUN apt-get update \
 && apt-get install -y --no-install-recommends libcurl4t64 curl ca-certificates \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /usr/lib/postgresql/18/lib/pg_oidc_validator.so /usr/lib/postgresql/18/lib/
COPY --from=build /src/oauth_conn_test /usr/local/bin/oauth_conn_test
COPY --from=build /src/LICENSE.txt /usr/share/doc/pg_oidc_validator/LICENSE.txt
COPY initdb/ /docker-entrypoint-initdb.d/

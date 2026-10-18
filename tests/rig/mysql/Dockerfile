# Percona Server for MySQL 8.4 with its OpenID Connect plugins (server and client), both shipped in
# the image from 8.4.11-11 on (the image also carries curl, which a probe uses to ask the issuer for
# a token from inside the rig's network). Build context: this directory.
FROM percona/percona-server:8.4.11-11
USER root
COPY rig.cnf /etc/my.cnf.d/rig.cnf
COPY initdb/ /docker-entrypoint-initdb.d/
USER mysql

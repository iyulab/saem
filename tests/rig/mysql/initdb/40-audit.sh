# Sourced once by the entrypoint (not executed: an exit here would end the entrypoint), last.
#
# The source's own record of who logged in: Percona's audit_log_filter component writes every
# connection to <datadir>/audit_filter.log with the login and its connection attributes -- among them
# program_name, which saem sets to the lookup a session reads for -- so a lookup in saem's log can be
# matched to the login the source authorised for it.
"${mysql[@]}" mysql < /usr/share/percona-server/audit_log_filter_linux_install.sql
"${mysql[@]}" <<'SQL'
SELECT audit_log_filter_set_filter('connections', '{"filter": {"class": {"name": "connection"}}}');
SELECT audit_log_filter_set_user('%', 'connections');
SQL

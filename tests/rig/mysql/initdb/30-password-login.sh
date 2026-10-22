# Sourced once by the entrypoint (not executed: an exit here would end the entrypoint), after
# 20-erp.sql, and acts only in the password-login profile (compose.password-login.yaml).
#
# Interim, as on mes (postgres/initdb/30-password-login.sh): saem's adapter cannot log in with a
# person's token yet, so each person gets a password account of their own -- alice_pw, bob_pw --
# that the source's rule treats as that person: the access table gives it the person's rows, and it
# may read the same views. Like the token accounts it needs TLS (require_secure_transport). The
# people's own accounts keep the token as their only credential.
if [ "${RIG_PASSWORD_LOGIN:-off}" = "on" ]; then
    "${mysql[@]}" <<'SQL'
USE erp;
CREATE USER 'alice_pw'@'%' IDENTIFIED BY 'rig-alice-local-only';
CREATE USER 'bob_pw'@'%' IDENTIFIED BY 'rig-bob-local-only';
INSERT INTO supplier_access (login, supplier_id)
    SELECT CONCAT(login, '_pw'), supplier_id FROM supplier_access WHERE login IN ('alice', 'bob');
GRANT SELECT ON erp.visible_suppliers TO 'alice_pw'@'%', 'bob_pw'@'%';
GRANT SELECT ON erp.visible_items TO 'alice_pw'@'%', 'bob_pw'@'%';
SQL
    # Fifty synthetic people, u01..u50, measured as many users at once, as on mes: the odd ones see what
    # alice sees, the even ones what bob sees. And a monitor login that may see every session the
    # source holds (PROCESS), so a measurement can count them from the source's side.
    for i in $(seq -w 1 50); do
        if [ $((10#$i % 2)) -eq 1 ]; then like=alice; else like=bob; fi
        echo "CREATE USER 'u$i'@'%' IDENTIFIED BY 'rig-synthetic-local-only';"
        echo "INSERT INTO erp.supplier_access (login, supplier_id) SELECT 'u$i', supplier_id FROM erp.supplier_access WHERE login = '$like';"
        echo "GRANT SELECT ON erp.visible_suppliers TO 'u$i'@'%';"
        echo "GRANT SELECT ON erp.visible_items TO 'u$i'@'%';"
    done | "${mysql[@]}"
    "${mysql[@]}" <<'SQL'
CREATE USER 'rig_monitor'@'%' IDENTIFIED BY 'rig-monitor-local-only';
GRANT PROCESS ON *.* TO 'rig_monitor'@'%';
GRANT SELECT ON performance_schema.* TO 'rig_monitor'@'%';
SQL
fi

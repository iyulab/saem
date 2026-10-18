-- A small ERP: items and the suppliers they come from. MySQL has no row-level security, so what a
-- person sees is decided by views that read the session's login (SESSION_USER(); inside a definer
-- view CURRENT_USER() is the view's definer, not the person) against an access table -- still the
-- source's rule, enforced by the source, for the account the token logged in as.
-- People get SELECT on the views only, never on the tables.
--   alice sees every supplier, including the canary;
--   bob sees only supplier S-1.

CREATE DATABASE erp;
USE erp;

CREATE TABLE suppliers (
    supplier_id   varchar(16) PRIMARY KEY,
    supplier_name varchar(64) NOT NULL
);

CREATE TABLE items (
    item_code   varchar(16) PRIMARY KEY,
    item_name   varchar(64) NOT NULL,
    supplier_id varchar(16) NOT NULL REFERENCES suppliers (supplier_id)
);

CREATE TABLE supplier_access (
    login       varchar(64) NOT NULL,
    supplier_id varchar(16) NOT NULL,
    PRIMARY KEY (login, supplier_id)
);

INSERT INTO suppliers VALUES ('S-1', 'Hanbit Parts'), ('S-2', 'CANARY-91c4-supplier-two');
INSERT INTO items VALUES ('I-100', 'Bracket', 'S-1'), ('I-200', 'Gasket', 'S-2');
INSERT INTO supplier_access VALUES ('alice', 'S-1'), ('alice', 'S-2'), ('bob', 'S-1');

CREATE SQL SECURITY DEFINER VIEW visible_suppliers AS
    SELECT s.supplier_id, s.supplier_name
    FROM suppliers s
    JOIN supplier_access a ON a.supplier_id = s.supplier_id
    WHERE a.login = SUBSTRING_INDEX(SESSION_USER(), '@', 1);

CREATE SQL SECURITY DEFINER VIEW visible_items AS
    SELECT i.item_code, i.item_name, i.supplier_id
    FROM items i
    JOIN supplier_access a ON a.supplier_id = i.supplier_id
    WHERE a.login = SUBSTRING_INDEX(SESSION_USER(), '@', 1);

-- Each account is bound to one subject at one issuer: a token for anyone else does not log in.
CREATE USER 'alice'@'%' IDENTIFIED WITH auth_openid_connect AS '{"identity_provider": "rig", "user": "alice"}';
CREATE USER 'bob'@'%' IDENTIFIED WITH auth_openid_connect AS '{"identity_provider": "rig", "user": "bob"}';
GRANT SELECT ON erp.visible_suppliers TO 'alice'@'%', 'bob'@'%';
GRANT SELECT ON erp.visible_items TO 'alice'@'%', 'bob'@'%';

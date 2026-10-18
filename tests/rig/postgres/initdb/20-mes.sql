-- A small MES: work orders, each naming the item it makes by the item's code in the ERP.
-- Row-level security decides what each person sees; saem never filters on its own.
--   alice sees every work order, including the canary;
--   bob sees only the work orders of line 1.
-- The canary's note is a value that must never appear in anything served to bob.

CREATE ROLE alice LOGIN;
CREATE ROLE bob LOGIN;
ALTER ROLE alice SET default_transaction_read_only = on;
ALTER ROLE bob SET default_transaction_read_only = on;

CREATE TABLE work_orders (
    wo_no     text PRIMARY KEY,
    line      int  NOT NULL,
    item_code text NOT NULL,
    qty       int  NOT NULL,
    note      text
);

INSERT INTO work_orders VALUES
    ('WO-1', 1, 'I-100', 50, NULL),
    ('WO-2', 1, 'I-200', 20, NULL),
    ('WO-3', 2, 'I-100', 10, 'CANARY-7f3a-line2-only');

ALTER TABLE work_orders ENABLE ROW LEVEL SECURITY;
CREATE POLICY everything_for_alice ON work_orders FOR SELECT TO alice USING (true);
CREATE POLICY line_one_for_bob ON work_orders FOR SELECT TO bob USING (line = 1);

GRANT SELECT ON work_orders TO alice, bob;

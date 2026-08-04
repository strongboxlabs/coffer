-- 046_drop_dead_txn_legs_commission.sql
--
-- Drop the dead `txn_legs.commission` column. Audit 2026-05-18:
-- 0 of 130,186 legs have a non-zero value; the importer always
-- writes 0/null.
--
-- Per ADR-0019 Rule 5, fees on a Buy/Sell live in their own paired
-- `txn_headers` row under one `txn_group_id` — the fee leg is the
-- single source of truth for the cash effect. Cost-basis math reads
-- `lots.unit_cost` (computed as price + apportioned commission at
-- import time) so the column was an intended redundancy from ADR-0018
-- Rule 3 that never got populated after the ADR-0019 cutover.
--
-- No backfill needed: the column is uniformly 0/null. Dropping is a
-- pure code-shape change.

ALTER TABLE txn_legs DROP COLUMN commission;

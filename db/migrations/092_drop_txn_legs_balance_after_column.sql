-- =============================================================================
-- 092 — drop txn_legs.balance_after (ADR-0034 part 4)
-- =============================================================================
--
-- The leg-level balance_after column is no longer maintained (mig 090
-- swapped the trigger family) and no longer read (mig 091 sources the
-- view from txn_header_account_balances; C# changes in the same PR
-- retarget HoldingsRepository and the importer).
--
-- Drop the column. Per ADR-0028's MAX(posting_index) picker rule — gone
-- with the column. Per ADR-0034 the picker isn't load-bearing anymore;
-- balance lives at (header, account) grain.
-- =============================================================================

ALTER TABLE txn_legs DROP COLUMN balance_after;

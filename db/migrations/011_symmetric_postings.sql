-- Phase 2 PR 2.6.5: switch to a strict symmetric double-entry posting model.
--
-- Per ADR-0019, every flow becomes a `transactions` row, every row pairs
-- with exactly one counterparty (also a `transactions` row), and the
-- former `splits` / `inv_txn_securities` tables disappear. Investment
-- transactions emit two paired rows: one on the brokerage's cash account,
-- one on a system-managed "Holdings" sibling account. Security details
-- (`security_id`, `quantity`, `unit_price`, `commission`) move onto
-- `transactions` and live on the holdings-side row.
--
-- This migration is destructive. Existing data in `transactions`,
-- `splits`, `inv_txn_securities`, `transaction_overrides`,
-- `transaction_tags`, `holdings`, `lots`, and `merge_candidates` is
-- TRUNCATEd before the column-shape change. Re-run the importer after
-- applying. CI starts from an empty DB so there is nothing to truncate
-- in that path; this is a one-time cost for any local DB.

-- ---------------------------------------------------------------------------
-- 1) Truncate dependent state. CASCADE handles splits, transaction_overrides,
--    transaction_tags, inv_txn_securities, lots, merge_candidates.
-- ---------------------------------------------------------------------------
TRUNCATE TABLE transactions, holdings RESTART IDENTITY CASCADE;

-- ---------------------------------------------------------------------------
-- 2) Drop tables whose role is taken over by the wider `transactions` shape.
-- ---------------------------------------------------------------------------
DROP TABLE splits;
DROP TABLE inv_txn_securities;

-- ---------------------------------------------------------------------------
-- 3) Add the symmetric-posting columns to `transactions`.
--    counterparty_id is NOT NULL with a DEFERRABLE FK so paired inserts within
--    one transaction can reference each other; the FK is checked at COMMIT.
--    No DEFAULT — every INSERT must supply a real counterparty.
-- ---------------------------------------------------------------------------
ALTER TABLE transactions
    ADD COLUMN counterparty_id UUID NOT NULL
        REFERENCES transactions(id) ON DELETE CASCADE
        DEFERRABLE INITIALLY DEFERRED,
    ADD COLUMN txn_group_id    UUID NULL,
    ADD COLUMN leg_index       INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN is_user_defined BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN security_id     UUID NULL REFERENCES securities(id) ON DELETE RESTRICT,
    ADD COLUMN quantity        NUMERIC(19, 6) NULL,
    ADD COLUMN unit_price      NUMERIC(19, 4) NULL,
    ADD COLUMN commission      NUMERIC(19, 4) NULL DEFAULT 0;

-- ---------------------------------------------------------------------------
-- 4) Holdings-account wiring on `accounts`.
--    is_system flags rows the system creates and the user UI hides by
--    default. holdings_account_id on a brokerage points at its sibling
--    Holdings account; that's where the holdings-side legs of investment
--    transactions land. Sibling-at-root + explicit FK preserves the
--    `accounts_parent_only_for_categories` invariant from ADR-0017.
-- ---------------------------------------------------------------------------
ALTER TABLE accounts
    ADD COLUMN is_system          BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN holdings_account_id UUID NULL REFERENCES accounts(id) ON DELETE SET NULL;

-- ---------------------------------------------------------------------------
-- 5) Indexes.
-- ---------------------------------------------------------------------------
CREATE INDEX idx_txn_group
    ON transactions(txn_group_id, leg_index, id)
    WHERE txn_group_id IS NOT NULL;

CREATE INDEX idx_txn_counterparty
    ON transactions(counterparty_id);

-- Per-security register query: filter holdings-side rows by security, ordered
-- chronologically. Partial (security_id IS NOT NULL) keeps the index small.
CREATE INDEX idx_txn_security
    ON transactions(security_id, feed_posted_at DESC, id DESC)
    WHERE security_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- 6) Counterparty-pairing trigger: enforce that every row's counterparty
--    points back at it. A↔B must be symmetric. The trigger is deferred to
--    COMMIT so paired inserts within one transaction can reference each
--    other before either side is fully resolved.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_validate_counterparty_symmetric()
RETURNS TRIGGER AS $$
DECLARE
    other_counterparty UUID;
BEGIN
    SELECT counterparty_id INTO other_counterparty
      FROM transactions
     WHERE id = NEW.counterparty_id;

    IF other_counterparty IS DISTINCT FROM NEW.id THEN
        RAISE EXCEPTION
            'counterparty pairing is not symmetric: row % declares counterparty=%, but that row counterparty=%',
            NEW.id, NEW.counterparty_id, other_counterparty;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER trg_counterparty_symmetric
AFTER INSERT OR UPDATE OF counterparty_id ON transactions
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION fn_validate_counterparty_symmetric();

-- Phase 2 PR 2.5: explicit ordering for splits within a transaction.
--
-- Moneydance encodes split order via numeric prefixes (`0.*`, `1.*`, ...);
-- we preserve that authoring order so the register UI shows splits in the
-- order the user originally entered them and so a future reorder feature
-- has a column to update. Tied display_order values are allowed; queries
-- break ties on `id` for determinism.
--
-- The new composite index replaces the flat (transaction_id) index — every
-- read of splits within a transaction wants them ordered, so the wider
-- index is a strict improvement.

ALTER TABLE splits
    ADD COLUMN display_order INTEGER NOT NULL DEFAULT 0;

DROP INDEX idx_splits_transaction;

CREATE INDEX idx_splits_transaction_order
    ON splits(transaction_id, display_order, id);

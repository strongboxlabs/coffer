-- Phase 1: indexes from §6.4 of the architecture doc, plus a few obvious join keys.

-- Cursor-paginated register reads:
--   WHERE account_id = ? AND is_merged_into IS NULL ORDER BY feed_posted_at DESC, id DESC
CREATE INDEX idx_txn_account_date
    ON transactions(account_id, feed_posted_at DESC, id DESC);

-- Idempotent upsert key for sync (only when external_id is set)
CREATE UNIQUE INDEX idx_txn_external
    ON transactions(account_id, external_id)
    WHERE external_id IS NOT NULL;

-- Merge candidate lookups: same account, same amount, near-date, not already merged out
CREATE INDEX idx_txn_merge_window
    ON transactions(account_id, feed_amount, feed_posted_at)
    WHERE is_merged_into IS NULL;

-- Fuzzy payee matching for the merge pipeline
CREATE INDEX idx_txn_payee_trgm
    ON transactions USING GIN (feed_payee gin_trgm_ops);

-- Splits join key (heavily used by the resolved view + reports)
CREATE INDEX idx_splits_transaction
    ON splits(transaction_id);

CREATE INDEX idx_splits_account
    ON splits(account_id);

-- Holdings by account
CREATE INDEX idx_holdings_account
    ON holdings(account_id, security_id);

-- Price history lookups: latest price per security
CREATE INDEX idx_prices_security_date
    ON security_prices(security_id, price_date DESC);

-- Lots per holding (for tax-lot identification)
CREATE INDEX idx_lots_holding
    ON lots(holding_id)
    WHERE is_closed = FALSE;

-- Investment txn -> security details
CREATE INDEX idx_inv_txn_securities_txn
    ON inv_txn_securities(transaction_id);

-- Merge review queue UI:
CREATE INDEX idx_merge_candidates_pending
    ON merge_candidates(status, created_at DESC)
    WHERE status = 'pending_review';

-- Sync history list
CREATE INDEX idx_sync_runs_started
    ON sync_runs(started_at DESC);

-- Pending transactions reconciliation lookups
CREATE INDEX idx_pending_account
    ON pending_transactions(account_id, transacted_at DESC);

-- Active rules in priority order
CREATE INDEX idx_rules_active_priority
    ON transaction_rules(priority, id)
    WHERE is_active = TRUE;

-- Recurring transactions due-date scan
CREATE INDEX idx_recurring_active_due
    ON recurring_transactions(next_due_date)
    WHERE is_active = TRUE;

-- Tag lookup by tag (the reverse direction; tag_id-first is rarer but still useful)
CREATE INDEX idx_transaction_tags_tag
    ON transaction_tags(tag_id, transaction_id);

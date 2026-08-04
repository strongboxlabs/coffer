-- Per-account SimpleFIN sync watermark (slice 2c.5).
--
-- Slice 2c.2 introduced "smart start-date" on
-- feed_connections.last_synced_at: subsequent syncs ask for
-- (last_synced_at - 7d) to now, instead of the full 90 days every
-- time. The watermark is per-CONNECTION, which produces a
-- correctness gap surfaced by a real user run:
--
--   1. User wipes the DB and reconnects SimpleFIN.
--   2. First sync runs BEFORE any account is mapped on the Coffer
--      side — bank returns 230+ transactions, all discarded because
--      no mapping exists.
--   3. last_synced_at advances anyway (it's per-connection, not
--      per-account, and the sync did get a 2xx).
--   4. User maps the accounts.
--   5. Next sync's window is (last_synced_at - 7d) → ~7-day slice;
--      the 230 historical transactions are now permanently out of
--      reach without manual intervention.
--
-- This migration moves the watermark to be per-account, so each
-- account's window is independent. `feed_connections.last_synced_at`
-- stays — it's now purely a "last sync attempt timestamp" used by the
-- UI ("Last synced 3h ago" connection label), not by the sync
-- algorithm's start-date computation.
--
-- Smart start-date (slice 2c.5):
--   * For each mapped account on the connection, desired start =
--     max(account.last_simplefin_sync_at - 7d, now - 90d + 1h).
--     Null watermark → use the floor (full 90-day window for this
--     account).
--   * SimpleFIN's API takes ONE start-date per request; we send the
--     MIN across all mapped accounts. Wide enough to satisfy every
--     account's window; FITID dedup (migration 039) handles any
--     overlap rows.
--   * After persisting, advance the per-account watermark to now()
--     only for accounts whose data was actually persisted (mapped at
--     the time of the sync, and not in the per-account errlist).
--   * Unbinding a feed mapping clears the watermark too — re-mapping
--     starts fresh with a 90-day window on the next sync.
--
-- User-resettable: a new endpoint
-- `PATCH /api/ledgers/{lid}/accounts/{aid}/sync-from-date` lets the
-- user pick an explicit "fetch transactions from this date forward"
-- value (capped server-side by SimpleFIN's 90-day floor on the next
-- request).

ALTER TABLE accounts
    ADD COLUMN last_simplefin_sync_at TIMESTAMPTZ;

COMMENT ON COLUMN accounts.last_simplefin_sync_at IS
    'Per-account SimpleFIN sync watermark (slice 2c.5). NULL when '
    'the account has never had a successful sync persist data, OR '
    'after the user resets via /accounts/{id}/sync-from-date. '
    'Advances on every sync that persisted at least one row for this '
    'account (including promote-on-clear). Stays put on partial '
    'syncs where errlist tags this account, and on needs_reauth / '
    'failed outcomes.';

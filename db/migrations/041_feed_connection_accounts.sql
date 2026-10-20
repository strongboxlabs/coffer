-- Per-connection account directory (Phase 5 slice 2c.4).
--
-- Slice 2c surfaced SimpleFIN's account list only inside the live
-- sync response (`SyncResultDto.unmappedAccounts`). That meant the
-- bank-side directory vanished as soon as the user navigated away,
-- and the only way to "see what accounts exist on this connection"
-- was to re-sync. This migration persists the SimpleFIN account
-- list per connection so the SPA can render a unified accounts
-- panel (mapped + unmapped together) at any time — the
-- MD+ "Set Up Moneydance+" Accounts dialog concept, adapted to our
-- own UX surface.
--
-- Sync upserts on every run; `last_seen_at` decays so a future
-- follow-up can detect stale entries (an external_id the bank
-- stopped returning).

CREATE TABLE feed_connection_accounts (
    id                       UUID            PRIMARY KEY DEFAULT gen_random_uuid(),
    feed_connection_id       UUID            NOT NULL REFERENCES feed_connections(id) ON DELETE CASCADE,
    external_id              TEXT            NOT NULL,
    name                     TEXT            NOT NULL,
    org_name                 TEXT,
    currency                 TEXT,
    balance                  NUMERIC(19, 4),
    balance_at               TIMESTAMPTZ,
    last_seen_at             TIMESTAMPTZ     NOT NULL DEFAULT now(),
    created_at               TIMESTAMPTZ     NOT NULL DEFAULT now()
);

COMMENT ON TABLE feed_connection_accounts IS
    'Per-connection bank-side account directory (slice 2c.4). One '
    'row per SimpleFIN account the bank surfaces on a connection. '
    'Upserted on every sync; the SPA reads this to render the '
    'unified accounts list (mapped + unmapped) on the bank-feeds '
    'page without requiring a fresh sync.';

COMMENT ON COLUMN feed_connection_accounts.external_id IS
    'SimpleFIN account id (`account.id` from v2 payload). '
    'Composite with feed_connection_id for the upsert key.';

COMMENT ON COLUMN feed_connection_accounts.last_seen_at IS
    'Bumped on every sync that returns this external_id. A row '
    'whose last_seen_at falls behind subsequent syncs is a stale '
    'entry — the bank stopped exposing this account. Future slice '
    'will sweep / flag these; today we just keep the timestamp.';

CREATE UNIQUE INDEX uq_feed_connection_accounts_external
    ON feed_connection_accounts (feed_connection_id, external_id);

CREATE INDEX idx_feed_connection_accounts_conn
    ON feed_connection_accounts (feed_connection_id);

-- ---------------------------------------------------------------------------
-- RLS — transitive through the parent feed_connection (same pattern
-- as sync_run_errors / sync_run_promotions from migration 038).
-- The user can see directory rows for any connection their
-- user_ledger_grants reach via feed_connections.ledger_id.
-- ---------------------------------------------------------------------------
ALTER TABLE feed_connection_accounts ENABLE ROW LEVEL SECURITY;
CREATE POLICY feed_connection_accounts_per_user
    ON feed_connection_accounts FOR ALL TO coffer_app
    USING      (feed_connection_id IN (SELECT id FROM feed_connections))
    WITH CHECK (feed_connection_id IN (SELECT id FROM feed_connections));

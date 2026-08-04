-- Phase A of multi-ledger support (per ADR-0020). Schema-only; the
-- API/auth wiring and Postgres RLS land in later phases. Goal of this
-- migration:
--
--   1. Introduce `ledgers` (the unit of book-isolation), `users`
--      (skeleton — Phase 3 fills in passkey columns), and
--      `user_ledger_grants` (per-user permissions on each ledger,
--      plus the data needed to auto-open the user's last ledger
--      after login).
--
--   2. Stamp `ledger_id` on the six anchor tables that don't reach a
--      ledger via an existing FK chain — `accounts`, `securities`,
--      `feed_connections`, `tags`, `merge_rules`, `transaction_rules`.
--      Every other business table inherits its ledger membership
--      transitively (per ADR-0020 Rule 1, the minimal-anchor design).
--
--   3. Backfill: a single default ledger row absorbs every existing row
--      so the migration is non-disruptive on a populated DB. Re-imports
--      after this migration target the same default ledger by default;
--      callers who want a separate book pass `--ledger-id` /
--      `--ledger-name` to the importer.
--
--   4. Recreate the per-anchor idempotent-import unique indexes as
--      `(ledger_id, external_id)` so two ledgers can both import an
--      MD export without colliding.
--
-- A constraint trigger pins `user_ledger_grants` so every ledger has
-- ≥1 owner (deferred to commit, fires on DELETE / UPDATE OF role).
-- RLS policies are deliberately not enabled here; they require the
-- Phase 3 auth pipeline to set `app.user_id` per request.

-- ---------------------------------------------------------------------------
-- 1) ledgers
-- ---------------------------------------------------------------------------
CREATE TABLE ledgers (
    id          UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    name        TEXT         NOT NULL,
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT now()
);

COMMENT ON TABLE ledgers IS
    'A self-contained book of accounts/transactions/etc. Multi-ledger '
    'isolation per ADR-0020: every anchor table carries `ledger_id`; '
    'derived tables inherit via FK chain.';

-- Bootstrap default ledger to absorb existing data.
INSERT INTO ledgers (id, name)
VALUES ('00000000-0000-0000-0000-000000000001', 'Default')
RETURNING id;

-- ---------------------------------------------------------------------------
-- 2) users (skeleton; Phase 3 auth adds passkey columns)
-- ---------------------------------------------------------------------------
CREATE TABLE users (
    id                      UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    display_name            TEXT         NOT NULL,
    last_opened_ledger_id   UUID         REFERENCES ledgers(id) ON DELETE SET NULL,
    created_at              TIMESTAMPTZ  NOT NULL DEFAULT now()
);

COMMENT ON TABLE users IS
    'Skeleton for Phase 3 WebAuthn auth (ADR-0013). `last_opened_ledger_id` '
    'remembers the ledger the user was viewing so the UI can auto-open it '
    'on next login; the application validates the user still has a grant '
    'before honouring it.';

COMMENT ON COLUMN users.last_opened_ledger_id IS
    'The ledger this user most recently switched to. NULL on first login '
    '— UI shows the ledger picker. Cleared on ledger deletion.';

-- Bootstrap "system" user for the importer / sync worker / pre-auth use.
-- Phase 3 will replace this with real users; this row stays as the
-- service-account identity for unattended workers.
INSERT INTO users (id, display_name)
VALUES ('00000000-0000-0000-0000-000000000001', 'system');

-- ---------------------------------------------------------------------------
-- 3) user_ledger_grants
-- ---------------------------------------------------------------------------
CREATE TABLE user_ledger_grants (
    user_id     UUID         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    ledger_id   UUID         NOT NULL REFERENCES ledgers(id) ON DELETE CASCADE,
    role        TEXT         NOT NULL CHECK (role IN ('owner', 'editor', 'viewer')),
    granted_at  TIMESTAMPTZ  NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, ledger_id)
);

COMMENT ON TABLE user_ledger_grants IS
    'Per-user permissions on each ledger. Owner: read+write+grant+delete. '
    'Editor: read+write. Viewer: read-only. A ledger must have >=1 owner '
    '(enforced by trg_user_ledger_grants_owner_present).';

-- Bootstrap: system user owns the default ledger.
INSERT INTO user_ledger_grants (user_id, ledger_id, role)
VALUES ('00000000-0000-0000-0000-000000000001',
        '00000000-0000-0000-0000-000000000001',
        'owner');

-- ≥1 owner constraint trigger: fires deferred at COMMIT so a multi-step
-- "remove old owner, add new owner" still passes mid-transaction.
CREATE OR REPLACE FUNCTION fn_validate_ledger_has_owner()
RETURNS TRIGGER AS $$
DECLARE
    owner_count INTEGER;
    target_ledger UUID;
BEGIN
    -- On DELETE OLD is set; on UPDATE both OLD and NEW exist; on INSERT only NEW.
    target_ledger := COALESCE(OLD.ledger_id, NEW.ledger_id);

    -- If the ledger itself was deleted, the row is already gone (CASCADE);
    -- nothing to validate.
    IF NOT EXISTS (SELECT 1 FROM ledgers WHERE id = target_ledger) THEN
        RETURN NULL;
    END IF;

    SELECT COUNT(*) INTO owner_count
      FROM user_ledger_grants
     WHERE ledger_id = target_ledger
       AND role = 'owner';

    IF owner_count < 1 THEN
        RAISE EXCEPTION
            'ledger % left with no owner; every ledger must have at least one owner',
            target_ledger;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER trg_user_ledger_grants_owner_present
AFTER DELETE OR UPDATE OF role ON user_ledger_grants
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION fn_validate_ledger_has_owner();

-- ---------------------------------------------------------------------------
-- 4) ledger_id on the six anchor tables
-- ---------------------------------------------------------------------------

-- accounts
ALTER TABLE accounts ADD COLUMN ledger_id UUID;
UPDATE accounts SET ledger_id = '00000000-0000-0000-0000-000000000001';
ALTER TABLE accounts
    ALTER COLUMN ledger_id SET NOT NULL,
    ADD CONSTRAINT accounts_ledger_id_fkey
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE RESTRICT;

-- securities
ALTER TABLE securities ADD COLUMN ledger_id UUID;
UPDATE securities SET ledger_id = '00000000-0000-0000-0000-000000000001';
ALTER TABLE securities
    ALTER COLUMN ledger_id SET NOT NULL,
    ADD CONSTRAINT securities_ledger_id_fkey
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE RESTRICT;

-- feed_connections
ALTER TABLE feed_connections ADD COLUMN ledger_id UUID;
UPDATE feed_connections SET ledger_id = '00000000-0000-0000-0000-000000000001';
ALTER TABLE feed_connections
    ALTER COLUMN ledger_id SET NOT NULL,
    ADD CONSTRAINT feed_connections_ledger_id_fkey
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE RESTRICT;

-- tags
ALTER TABLE tags ADD COLUMN ledger_id UUID;
UPDATE tags SET ledger_id = '00000000-0000-0000-0000-000000000001';
ALTER TABLE tags
    ALTER COLUMN ledger_id SET NOT NULL,
    ADD CONSTRAINT tags_ledger_id_fkey
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE RESTRICT;

-- merge_rules: PK changes from id to (ledger_id, id). Keep id as a stable
-- row identifier within the ledger; require exactly one rule row per ledger
-- (single-row config) via the unique index below.
ALTER TABLE merge_rules ADD COLUMN ledger_id UUID;
UPDATE merge_rules SET ledger_id = '00000000-0000-0000-0000-000000000001';
ALTER TABLE merge_rules
    ALTER COLUMN ledger_id SET NOT NULL,
    ADD CONSTRAINT merge_rules_ledger_id_fkey
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE RESTRICT;
CREATE UNIQUE INDEX uq_merge_rules_one_per_ledger
    ON merge_rules(ledger_id);

-- transaction_rules: `apply_account_id` is optional, so the rule has no
-- transitive FK to accounts.ledger_id when that column is NULL. Direct
-- ledger_id is required.
ALTER TABLE transaction_rules ADD COLUMN ledger_id UUID;
UPDATE transaction_rules SET ledger_id = '00000000-0000-0000-0000-000000000001';
ALTER TABLE transaction_rules
    ALTER COLUMN ledger_id SET NOT NULL,
    ADD CONSTRAINT transaction_rules_ledger_id_fkey
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE RESTRICT;

-- ---------------------------------------------------------------------------
-- 5) Per-ledger uniqueness on the anchor idempotent-import keys
-- ---------------------------------------------------------------------------

-- accounts.external_id -> (ledger_id, external_id)
DROP INDEX IF EXISTS uq_accounts_external_id;
CREATE UNIQUE INDEX uq_accounts_external_id_per_ledger
    ON accounts(ledger_id, external_id)
    WHERE external_id IS NOT NULL;

-- securities.external_id -> (ledger_id, external_id)
DROP INDEX IF EXISTS uq_securities_external_id;
CREATE UNIQUE INDEX uq_securities_external_id_per_ledger
    ON securities(ledger_id, external_id)
    WHERE external_id IS NOT NULL;

-- recurring_transactions.external_id stays as-is — recurring_transactions
-- is a *derived* table (transitively reaches accounts.ledger_id via
-- source_account_id), so its external_id is already per-ledger by
-- transitive scoping. The index from migration 013 needs no change.

-- tags.name was UNIQUE globally; needs to become per-ledger.
ALTER TABLE tags DROP CONSTRAINT IF EXISTS tags_name_key;
CREATE UNIQUE INDEX uq_tags_name_per_ledger
    ON tags(ledger_id, name);

-- transaction_rules has no external_id, so no per-ledger uniqueness to add.
-- feed_connections has no external_id either.

-- ---------------------------------------------------------------------------
-- 6) Helper view for "ledgers a given user can see" (used by Phase 3 RLS
--    policies and the auto-open-last-ledger flow). Included here so the
--    application surface has a stable shape from day one.
-- ---------------------------------------------------------------------------
CREATE VIEW user_visible_ledgers AS
SELECT g.user_id, g.ledger_id, g.role, l.name AS ledger_name, g.granted_at
  FROM user_ledger_grants g
  JOIN ledgers l ON l.id = g.ledger_id;

COMMENT ON VIEW user_visible_ledgers IS
    'Per-user list of ledgers the user has any role on. Used for the '
    'login-time picker, the auto-open-last-ledger validation, and as a '
    'building block for the Phase D RLS policies.';

-- User-curated sidebar "tabs" of accounts. Each user can define a
-- handful of named groups (typical: 2-4); each group contains any
-- subset of the accounts in one ledger; an account can belong to
-- multiple groups. The implicit "All" tab is virtual — not a row in
-- this table, just the default render when no group filter is in
-- effect.
--
-- Scope per (user, ledger): groups are user-curated and ledger-
-- specific (an account is bound to one ledger, so a group of
-- accounts is too). A shared ledger has potentially-different group
-- curations per user, matching ADR-0020's multi-user model.
--
-- Two tables: the group header (`user_account_groups`) and the N:M
-- membership (`user_account_group_members`).

-- ---------------------------------------------------------------------------
-- 1) Group headers
-- ---------------------------------------------------------------------------
CREATE TABLE user_account_groups (
    id          UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     UUID         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    ledger_id   UUID         NOT NULL REFERENCES ledgers(id) ON DELETE CASCADE,
    name        TEXT         NOT NULL CHECK (length(trim(name)) > 0),
    sort_order  INTEGER      NOT NULL DEFAULT 0,
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT now()
);

COMMENT ON TABLE user_account_groups IS
    'User-curated sidebar tabs (named groups of accounts), scoped per '
    '(user, ledger). The implicit "All" tab is not a row here — the SPA '
    'renders it when no group filter is applied.';

-- One user can have several groups in one ledger but no two with
-- the same name — keeps the sidebar tab strip readable.
CREATE UNIQUE INDEX uq_user_account_groups_name
    ON user_account_groups (user_id, ledger_id, lower(name));

-- Sidebar render order — groups listed by sort_order ASC. Ties
-- break on created_at so a freshly-inserted group with the same
-- sort_order lands deterministically.
CREATE INDEX idx_user_account_groups_listing
    ON user_account_groups (user_id, ledger_id, sort_order, created_at);

-- ---------------------------------------------------------------------------
-- 2) Group ↔ account membership (N:M)
-- ---------------------------------------------------------------------------
CREATE TABLE user_account_group_members (
    group_id    UUID         NOT NULL REFERENCES user_account_groups(id) ON DELETE CASCADE,
    account_id  UUID         NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    added_at    TIMESTAMPTZ  NOT NULL DEFAULT now(),
    PRIMARY KEY (group_id, account_id)
);

COMMENT ON TABLE user_account_group_members IS
    'N:M membership between user_account_groups and accounts. Removing '
    'a row removes that account from the group (the account itself is '
    'untouched). Both sides cascade on delete.';

-- Reverse lookup: "which groups does this account appear in?" Used
-- when the SPA renders the per-account context menu ("Add to ▸ /
-- Remove from ▸") to mark already-member groups.
CREATE INDEX idx_user_account_group_members_account
    ON user_account_group_members (account_id);

-- ---------------------------------------------------------------------------
-- 3) RLS — same pattern as accounts: per-user via ledger grants,
--    additionally pinned to the owning user.
-- ---------------------------------------------------------------------------
ALTER TABLE user_account_groups ENABLE ROW LEVEL SECURITY;
CREATE POLICY user_account_groups_self ON user_account_groups FOR ALL TO coffer_app
    USING (
        user_id = current_app_user_id()
        AND ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    )
    WITH CHECK (
        user_id = current_app_user_id()
        AND ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

-- Membership inherits the same per-user visibility via its group_id
-- — and additionally requires the account itself to be in a ledger
-- the user can access (defensive: the account is in the same
-- ledger as the group by design, but the RLS expresses the
-- invariant explicitly).
ALTER TABLE user_account_group_members ENABLE ROW LEVEL SECURITY;
CREATE POLICY user_account_group_members_self ON user_account_group_members FOR ALL TO coffer_app
    USING (
        group_id IN (
            SELECT id FROM user_account_groups
             WHERE user_id = current_app_user_id()
        )
        AND account_id IN (
            SELECT a.id FROM accounts a
             WHERE a.ledger_id IN (
                 SELECT ledger_id FROM user_ledger_grants
                  WHERE user_id = current_app_user_id()
             )
        )
    )
    WITH CHECK (
        group_id IN (
            SELECT id FROM user_account_groups
             WHERE user_id = current_app_user_id()
        )
        AND account_id IN (
            SELECT a.id FROM accounts a
             WHERE a.ledger_id IN (
                 SELECT ledger_id FROM user_ledger_grants
                  WHERE user_id = current_app_user_id()
             )
        )
    );

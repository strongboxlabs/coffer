-- =============================================================================
-- 187 — usernames compare case-insensitively (ADR-0089)
-- =============================================================================
--
-- `users.username` used the database's default collation, so `=` was
-- case-sensitive. Two consequences, both live bugs:
--
--   * LOGIN LOCKOUT. UsersRepository looks the account up with
--     `u.Username == username` → SQL `=`. Register as `Ada`, type `ada` at
--     sign-in, and the lookup returns nothing: "user not found" for an account
--     that exists. With passkeys the username is often the only thing the user
--     types, so there is no other way in.
--   * DUPLICATE IDENTITIES. uq_users_username is case-sensitive, so `Ada` and
--     `ada` are two separate accounts — an impersonation/confusion vector, and
--     worse now that usernames may be email addresses (mail providers treat the
--     local part case-insensitively, so both spellings are "the same person").
--
-- WHY A COLLATION AND NOT lower(username):
--   `lower()` depends on the database's ctype, which is baked in at initdb from
--   the host/container locale — so folding would differ between installs. On
--   this DB (en_US.utf8):
--       lower('İSTANBUL')               -> istanbul
--       lower('İSTANBUL' COLLATE "C")   -> İstanbul
--   Same expression, different answers. `COLLATE "C"` is deterministic but folds
--   ASCII only, which would leave `JOSÉ` and `josé` as distinct accounts — not
--   acceptable with per-user language/culture selection planned.
--
--   An ICU collation with locale `und` ("undetermined") and strength level 2
--   (`ks-level2` = case-insensitive, accent-sensitive) folds Unicode correctly
--   and is independent of every locale, including the user's own. That
--   independence is the point: identity must not resolve differently depending
--   on who is logging in, which is exactly what culture-driven folding would do
--   (the Turkish dotless-ı problem).
--
-- Setting the collation on the COLUMN — rather than adding a functional index —
-- makes `=` case-insensitive everywhere at once, so no application query
-- changes, and ALTER COLUMN TYPE rebuilds uq_users_username under the new
-- collation. Verified on a live DB: `WHERE username = 'SYSTEM'` matches
-- `system`, and inserting `SysTem` alongside `system` is rejected by the
-- existing unique index.
--
-- Non-deterministic collations do not support LIKE / pattern operators on the
-- column. Audited: nothing pattern-matches, orders, or searches on `username`
-- (lookups are all equality), so this costs nothing today. A future
-- username *search* feature must compare against an explicitly-collated
-- expression instead of adding LIKE here.
-- =============================================================================

CREATE COLLATION IF NOT EXISTS username_ci (
    provider      = icu,
    locale        = 'und-u-ks-level2',
    deterministic = false
);

-- Fail loudly BEFORE the ALTER if any existing rows differ only by case. The
-- ALTER would otherwise fail on its own with a bare "duplicate key" and no
-- indication of which accounts are at fault. Never auto-merge: two accounts
-- differing only by case may belong to two different people, and picking a
-- survivor silently would hand one person's ledgers to the other.
DO $Ci$
DECLARE
    collisions text;
BEGIN
    SELECT string_agg(DISTINCT a.username || ' / ' || b.username, '; ')
      INTO collisions
      FROM users a
      JOIN users b
        ON a.id < b.id
       -- Compare with the SAME semantics the index will enforce.
       AND a.username = b.username COLLATE username_ci
     WHERE a.username IS NOT NULL
       AND b.username IS NOT NULL;

    IF collisions IS NOT NULL THEN
        RAISE EXCEPTION
            'Cannot make usernames case-insensitive: these accounts differ only '
            'by case and would collide: %. Rename or delete one of each pair, '
            'then re-run the migration.', collisions;
    END IF;
END $Ci$;

ALTER TABLE users
    ALTER COLUMN username TYPE text COLLATE username_ci;

COMMENT ON COLUMN users.username IS
    'Login identifier. COLLATE username_ci (ICU und-u-ks-level2, '
    'non-deterministic) so = and uq_users_username are case-insensitive, '
    'independent of the install''s locale and of the user''s own culture '
    '(ADR-0089). Permissive charset — may be an email address. No LIKE / '
    'pattern operators on this column: non-deterministic collations reject them.';

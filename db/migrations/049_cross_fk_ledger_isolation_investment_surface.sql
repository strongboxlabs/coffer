-- =============================================================================
-- 049 — Cross-FK ledger isolation hardening (investment surface)
-- =============================================================================
--
-- Before this migration: securities.ledger_id and accounts.ledger_id are
-- enforced, but a row in holdings / lots / security_prices / txn_legs
-- could in principle reference a parent in a DIFFERENT ledger. The
-- importer never produces such a row, and the API layer filters every
-- read by ledger_id (slice A3's SecuritiesRepository in particular),
-- but the DB itself does not REJECT a malformed cross-ledger reference.
-- That's a defense-in-depth gap.
--
-- Slice A3 (user directive 2026-05-19: "even at the DB structure")
-- closes it for the investment surface by adopting **composite FKs**:
-- every cross-table reference is on (parent_id, ledger_id) → parent
-- (id, ledger_id), forcing the referencing row and the referenced
-- row to live in the same ledger. PostgreSQL refuses any INSERT or
-- UPDATE that would point one ledger's row at another.
--
-- Pattern, applied to every derivative table:
--   1. Parent gets UNIQUE (id, ledger_id) so composite FKs work.
--   2. Derivative gets a NOT NULL `ledger_id` column, backfilled from
--      the parent.
--   3. Old simple FK is replaced with composite FK.
--   4. CHECK CONSTRAINT or composite FK guarantees ledger coherence.
--
-- Tables covered in this slice (investment surface only):
--   * holdings        — account_id + security_id both → same ledger
--   * lots            — holding_id + leg_id both → same ledger
--   * security_prices — security_id → its ledger
--   * txn_legs        — header_id + account_id + (security_id when
--                       not null) all → same ledger
--
-- Other cross-FK leakage paths (accounts.parent_id self-ref,
-- accounts.holdings_account_id self-ref, txn_headers.is_merged_into,
-- feed_connections / sync_runs / account_groups chains,
-- txn_header_overrides / txn_leg_overrides / txn_header_tags chains)
-- are NOT touched here — they're a separate hardening slice
-- ("Phase 2 — non-investment cross-FK hardening", tracked in
-- follow-ups.md).
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 1. Parent tables: UNIQUE (id, ledger_id) so composite FKs can reference.
-- ---------------------------------------------------------------------------
-- PostgreSQL requires the referenced columns of a FK to be a unique key.
-- (id) is already the PK; (id, ledger_id) needs its own UNIQUE.

ALTER TABLE accounts     ADD CONSTRAINT uq_accounts_id_ledger     UNIQUE (id, ledger_id);
ALTER TABLE securities   ADD CONSTRAINT uq_securities_id_ledger   UNIQUE (id, ledger_id);
ALTER TABLE txn_headers  ADD CONSTRAINT uq_txn_headers_id_ledger  UNIQUE (id, ledger_id);

-- holdings and txn_legs both become composite-FK targets below (lots
-- references holdings AND txn_legs). Their PK is `id` alone; the
-- composite FK requires a UNIQUE on the exact (id, ledger_id) tuple.
-- These uniques are added immediately AFTER the holdings.ledger_id /
-- txn_legs.ledger_id columns get backfilled-and-NOT-NULL'd below.

-- ---------------------------------------------------------------------------
-- 2. holdings — add ledger_id, backfill, composite FKs to accounts + securities.
-- ---------------------------------------------------------------------------
ALTER TABLE holdings ADD COLUMN ledger_id UUID;

-- Backfill from the security's ledger. (Equivalently the account's;
-- the new composite FKs will reject any row where the two disagree.)
UPDATE holdings h
   SET ledger_id = s.ledger_id
  FROM securities s
 WHERE s.id = h.security_id;

ALTER TABLE holdings
    ALTER COLUMN ledger_id SET NOT NULL,
    ADD CONSTRAINT holdings_ledger_id_fkey
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE RESTRICT,
    ADD CONSTRAINT uq_holdings_id_ledger UNIQUE (id, ledger_id);

-- Drop the single-column FKs, add composite ones. ON DELETE RESTRICT
-- matches the prior semantics: deleting an account / security with
-- holdings is still forbidden.
ALTER TABLE holdings
    DROP CONSTRAINT holdings_account_id_fkey,
    DROP CONSTRAINT holdings_security_id_fkey,
    ADD CONSTRAINT holdings_account_id_fkey
        FOREIGN KEY (account_id, ledger_id) REFERENCES accounts(id, ledger_id)
        ON DELETE RESTRICT,
    ADD CONSTRAINT holdings_security_id_fkey
        FOREIGN KEY (security_id, ledger_id) REFERENCES securities(id, ledger_id)
        ON DELETE RESTRICT;

-- Common access pattern: per-ledger holdings list. The existing
-- UNIQUE (account_id, security_id) already covers most lookups;
-- (ledger_id) on its own gets a partial index since balance reads
-- usually filter further by account_id or security_id.
CREATE INDEX idx_holdings_ledger_id ON holdings(ledger_id);

COMMENT ON COLUMN holdings.ledger_id IS
    'Migration 049: denormalized from parent FKs. Composite FK to '
    'accounts(id, ledger_id) AND securities(id, ledger_id) guarantees '
    'both references resolve to the same ledger — DB refuses cross-'
    'ledger holdings.';

-- ---------------------------------------------------------------------------
-- 3. lots — add ledger_id, backfill, composite FKs to holdings + txn_legs.
-- ---------------------------------------------------------------------------
ALTER TABLE lots ADD COLUMN ledger_id UUID;

-- Backfill from holdings. (lots.leg_id → txn_legs's ledger; we'll
-- enforce coherence with the composite FK below.) holdings.ledger_id
-- exists post-step-2.
UPDATE lots l
   SET ledger_id = h.ledger_id
  FROM holdings h
 WHERE h.id = l.holding_id;

ALTER TABLE lots
    ALTER COLUMN ledger_id SET NOT NULL,
    ADD CONSTRAINT lots_ledger_id_fkey
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE RESTRICT;

ALTER TABLE lots
    DROP CONSTRAINT lots_holding_id_fkey,
    ADD CONSTRAINT lots_holding_id_fkey
        FOREIGN KEY (holding_id, ledger_id) REFERENCES holdings(id, ledger_id)
        ON DELETE CASCADE;

-- lots.leg_id → txn_legs. txn_legs gets its ledger_id below; we
-- compose the FK AFTER txn_legs's column exists. (Step 6.)

CREATE INDEX idx_lots_ledger_id ON lots(ledger_id);

COMMENT ON COLUMN lots.ledger_id IS
    'Migration 049: denormalized. Composite FK to holdings(id, '
    'ledger_id) and (below) txn_legs(id, ledger_id) so the lot, '
    'its holding, and the leg that produced it all share a ledger.';

-- ---------------------------------------------------------------------------
-- 4. security_prices — add ledger_id, backfill, composite FK to securities.
-- ---------------------------------------------------------------------------
ALTER TABLE security_prices ADD COLUMN ledger_id UUID;

UPDATE security_prices p
   SET ledger_id = s.ledger_id
  FROM securities s
 WHERE s.id = p.security_id;

ALTER TABLE security_prices
    ALTER COLUMN ledger_id SET NOT NULL,
    ADD CONSTRAINT security_prices_ledger_id_fkey
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE RESTRICT;

ALTER TABLE security_prices
    DROP CONSTRAINT security_prices_security_id_fkey,
    ADD CONSTRAINT security_prices_security_id_fkey
        FOREIGN KEY (security_id, ledger_id) REFERENCES securities(id, ledger_id)
        ON DELETE CASCADE;

CREATE INDEX idx_security_prices_ledger_id ON security_prices(ledger_id);

COMMENT ON COLUMN security_prices.ledger_id IS
    'Migration 049: per-ledger price isolation. A price row can only '
    'reference a security in the same ledger.';

-- ---------------------------------------------------------------------------
-- 5. txn_legs — add ledger_id, backfill, composite FKs to header + account + security.
-- ---------------------------------------------------------------------------
-- Triggers on txn_legs maintain `balance_after` (migration 011) and
-- the swing view (migration 023). The backfill UPDATE doesn't change
-- amount / account_id / header_id, so per-row trigger work is pure
-- overhead — and on real data (~130K legs) that overhead pushes the
-- UPDATE past DbUp's per-statement timeout. Suspend USER triggers
-- on the table for the duration of the backfill, then re-enable.
-- Postgres' system triggers (FK / NOT NULL / CHECK enforcement)
-- aren't affected by `DISABLE TRIGGER USER`.

ALTER TABLE txn_legs ADD COLUMN ledger_id UUID;
ALTER TABLE txn_legs DISABLE TRIGGER USER;

UPDATE txn_legs l
   SET ledger_id = h.ledger_id
  FROM txn_headers h
 WHERE h.id = l.header_id;

ALTER TABLE txn_legs ENABLE TRIGGER USER;

ALTER TABLE txn_legs
    ALTER COLUMN ledger_id SET NOT NULL,
    ADD CONSTRAINT txn_legs_ledger_id_fkey
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE RESTRICT,
    ADD CONSTRAINT uq_txn_legs_id_ledger UNIQUE (id, ledger_id);

-- Header FK: composite. ON DELETE CASCADE matches prior semantics —
-- deleting a header takes its legs with it.
ALTER TABLE txn_legs
    DROP CONSTRAINT txn_legs_header_id_fkey,
    ADD CONSTRAINT txn_legs_header_id_fkey
        FOREIGN KEY (header_id, ledger_id) REFERENCES txn_headers(id, ledger_id)
        ON DELETE CASCADE;

-- Account FK: composite. RESTRICT preserves the constraint that
-- accounts with legs can't be hard-deleted.
ALTER TABLE txn_legs
    DROP CONSTRAINT txn_legs_account_id_fkey,
    ADD CONSTRAINT txn_legs_account_id_fkey
        FOREIGN KEY (account_id, ledger_id) REFERENCES accounts(id, ledger_id)
        ON DELETE RESTRICT;

-- Security FK: composite, but security_id is nullable. PostgreSQL's
-- MATCH SIMPLE (default) treats any-NULL composite FK as unenforced,
-- so legs without a security still work; legs WITH a security are
-- locked to the same ledger as the security row.
ALTER TABLE txn_legs
    DROP CONSTRAINT txn_legs_security_id_fkey,
    ADD CONSTRAINT txn_legs_security_id_fkey
        FOREIGN KEY (security_id, ledger_id) REFERENCES securities(id, ledger_id)
        ON DELETE RESTRICT;

CREATE INDEX idx_txn_legs_ledger_id ON txn_legs(ledger_id);

COMMENT ON COLUMN txn_legs.ledger_id IS
    'Migration 049: denormalized from txn_headers. Three composite '
    'FKs (header / account / security) all key on (parent_id, '
    'ledger_id), so a leg, its header, its account, and (when set) '
    'its security all share one ledger — structurally impossible to '
    'cross-pollinate.';

-- ---------------------------------------------------------------------------
-- 6. lots.leg_id composite FK (deferred from step 3 until txn_legs has ledger_id).
-- ---------------------------------------------------------------------------
ALTER TABLE lots
    DROP CONSTRAINT lots_leg_id_fkey,
    ADD CONSTRAINT lots_leg_id_fkey
        FOREIGN KEY (leg_id, ledger_id) REFERENCES txn_legs(id, ledger_id)
        ON DELETE RESTRICT;

-- ---------------------------------------------------------------------------
-- 7. Verification — every derivative row's ledger_id agrees with its parents.
-- ---------------------------------------------------------------------------
-- A defensive sanity check. Composite FKs would have rejected the
-- migration if any row violated, but the explicit COUNT makes the
-- migration log say "0 mismatches" so future-us doesn't wonder.
DO $$
DECLARE
    bad_holdings INTEGER;
    bad_lots     INTEGER;
    bad_prices   INTEGER;
    bad_legs     INTEGER;
BEGIN
    SELECT COUNT(*) INTO bad_holdings FROM holdings h
        JOIN accounts a   ON a.id = h.account_id
        JOIN securities s ON s.id = h.security_id
        WHERE a.ledger_id <> h.ledger_id OR s.ledger_id <> h.ledger_id;
    SELECT COUNT(*) INTO bad_lots FROM lots l
        JOIN holdings h  ON h.id = l.holding_id
        JOIN txn_legs g  ON g.id = l.leg_id
        WHERE h.ledger_id <> l.ledger_id OR g.ledger_id <> l.ledger_id;
    SELECT COUNT(*) INTO bad_prices FROM security_prices p
        JOIN securities s ON s.id = p.security_id
        WHERE s.ledger_id <> p.ledger_id;
    SELECT COUNT(*) INTO bad_legs FROM txn_legs l
        JOIN txn_headers h ON h.id = l.header_id
        JOIN accounts a    ON a.id = l.account_id
        LEFT JOIN securities s ON s.id = l.security_id
        WHERE h.ledger_id <> l.ledger_id
           OR a.ledger_id <> l.ledger_id
           OR (s.id IS NOT NULL AND s.ledger_id <> l.ledger_id);

    RAISE NOTICE 'Migration 049 verification: holdings=% lots=% prices=% legs=% mismatches (all should be 0)',
        bad_holdings, bad_lots, bad_prices, bad_legs;

    IF bad_holdings + bad_lots + bad_prices + bad_legs > 0 THEN
        RAISE EXCEPTION 'Migration 049 found % cross-ledger row(s) — composite FK would reject. Halt.',
            bad_holdings + bad_lots + bad_prices + bad_legs;
    END IF;
END $$;

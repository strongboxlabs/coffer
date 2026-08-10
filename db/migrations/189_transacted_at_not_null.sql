-- =============================================================================
-- 189 — txn_headers.transacted_at is NOT NULL
-- =============================================================================
--
-- One state had two representations, and nothing declared which was canonical:
--
--   NULL                      -- Coffer-created transactions, reminder occurrences
--   transacted_at = posted_at -- every Moneydance-imported row
--
-- Both mean "no distinct tax date". The Moneydance importer mirrors MD's `td`
-- field faithfully, and MD populates it on every transaction — usually equal to
-- the posting date — so an imported ledger is entirely the second form while
-- anything Coffer created is the first. Readers had to know both, and the register
-- already encodes that knowledge as a noise filter (`taxDateSubLabel` renders a
-- second line only when the dates differ).
--
-- Same defect the `is_error` / `status` duplication had before migration 184: two
-- ways to say one thing keeps costing, because every future reader has to
-- remember. So collapse to the always-populated form.
--
-- WHY THIS DIRECTION rather than nulling out same-day rows: `transacted_at` is a
-- faithful copy of MD's `td`, and docs/moneydance-import-fidelity.md exists to
-- track every field where fidelity is lost. Nulling same-day values would add a
-- silent entry to that inventory. Backfilling nulls loses nothing — every consumer
-- already reads NULL as "same as posted", which is exactly what it becomes.
--
-- What this forecloses: "explicitly no tax date" can no longer be distinguished
-- from "same as posted". Nothing distinguishes them today either — no query, no
-- view, no UI — so this makes the existing semantics explicit rather than
-- removing a capability.
--
-- Writers were fixed at their call sites rather than behind a trigger, per
-- ADR-0032 gate 1 ("don't add a trigger to cover for a writer that should be doing
-- the work itself"). Snapshot restore inserts whatever a payload carries, but a
-- pre-189 payload cannot be restored — the schema-version guard refuses it — so
-- every payload reaching that path has non-null values.
-- =============================================================================

-- Backfill the Coffer-created rows. MD-imported rows already carry `td`.
UPDATE txn_headers
   SET transacted_at = posted_at
 WHERE transacted_at IS NULL;

ALTER TABLE txn_headers
    ALTER COLUMN transacted_at SET NOT NULL;

COMMENT ON COLUMN txn_headers.transacted_at IS
    'Tax / transaction date — Moneydance''s `td`. NOT NULL since migration 189: '
    'always populated, equal to posted_at when there is no distinct tax date. '
    'Readers must NOT treat equality with posted_at as "unset" in storage terms; '
    'it is simply the common case, and the register hides the sub-label for it.';

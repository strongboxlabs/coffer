-- 128_reconcile_is_merge_winner_denorm.sql
--
-- Reconcile the is_merge_winner denormalization with the is_merged_into
-- pointers (its source of truth).
--
-- Why
-- ===
-- is_merge_winner (mig 107) is a stored denorm: TRUE when at least one
-- other row's is_merged_into points at this row. It exists so the register
-- can render a merge-winner overlay without an EXISTS subquery on the hot
-- resolved_transactions view. The active merge path
-- (TransactionsRepository.PatchAsync) sets loser is_merged_into and winner
-- is_merge_winner atomically, so the flag stays correct going forward.
--
-- The ADR-0052 D4 audit found 2 rows on the Default ledger that ARE merge
-- targets (a loser points at them) yet carry is_merge_winner=false. Both
-- were merged under the PRE-"merge-direction-invert" code (editor was the
-- winner, candidate the loser -- the reverse of today), which left the flag
-- on the wrong side; mig 107's backfill could not catch them because the
-- surviving rows were created after mig 107 ran. This is one-time historical
-- residue, not a recurring bug. is_merged_into (the truth) is correct, so
-- balances and the register were never affected -- only the overlay icon.
--
-- This reconcile sets the missing TRUEs (false-negatives) only. It does NOT
-- clear TRUEs that lack a pointer: mig 107 defines the flag as monotonic
-- (there is no unmerge surface), so a winner whose loser was later
-- hard-deleted intentionally keeps the overlay. Idempotent.

BEGIN;

UPDATE txn_headers w
   SET is_merge_winner = true
 WHERE NOT w.is_merge_winner
   AND EXISTS (SELECT 1 FROM txn_headers l WHERE l.is_merged_into = w.id);

COMMIT;

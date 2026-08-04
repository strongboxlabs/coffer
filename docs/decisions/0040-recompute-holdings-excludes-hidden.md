# 0040 — `recompute_holdings_cost_basis` excludes soft-hidden headers

* Status: Accepted
* Date: 2026-06-09
* Related: ADR-0023 (delete semantics), ADR-0032 (triggers last
  resort), ADR-0039 (drop the quantity clamp), migration 103
  (balance recompute hidden-filter), migration 116 (holdings
  quantity clamp removal)

## Context

`txn_headers.is_hidden = TRUE` is Coffer's soft-delete marker
(ADR-0023 §Delete). When the user clicks Delete in the SPA on a
row that has a non-null `external_id`, the server soft-hides it
instead of hard-deleting. The raw row stays in the database so a
subsequent re-source (re-import / re-sync) doesn't resurrect it,
but every read path that derives state from headers must treat the
row as deleted.

Migration 103 (2026-05) made the *balance* recompute exclude
hidden headers: a soft-deleted txn's signed amount must not affect
the running cash balance. The *holdings* recompute
(`recompute_holdings_cost_basis`, mig 067 / 068; touched by
ADR-0039 / mig 116) was left out.

Real-data evidence: the user's MD JSON contained a manually-
entered "Sample transaction" DivReinvest for 5.000000000 shares of
FUNDX (no OFX provenance, dt = 2026-05-16). The user soft-deleted
it via the SPA at some point. `is_hidden = true` landed correctly
on the header. But `holdings.quantity` for FUNDX still reflected
those +5.00 shares, accounting for the entire mismatch between
Coffer's holdings (5.00) and the broker's (0).

The Portfolio View, the cost-basis hero numbers, every consumer
of `holdings.quantity` was telling the user they held a position
that — per the system's own soft-delete contract — they had
already deleted.

## Decision

Add `txn_headers.is_hidden = FALSE` to the leg walk in
`recompute_holdings_cost_basis`. Soft-deleted headers stop
contributing to:

* `running_qty` / `holdings.quantity`
* `running_basis` / `holdings.cost_basis`
* Lot resets (the LOTS join also gains `th.is_hidden = FALSE`,
  so a soft-deleted Buy's lot row is not refreshed by the
  recompute and will be removed on the next scrub)

A header coming OUT of hidden state (user un-hides via the API)
falls back into scope on the next recompute. Same lifecycle
contract as balances.

`security_splits` are not header-shaped — the splits table has no
`is_hidden`. A stock split that affected past holdings continues
to apply regardless of any soft-deleted txn rows in the same
window. This matches the conceptual model: splits are tax events
at the security level, not user-editable transactions.

## Consequences

### Positive

* **Holdings now agrees with the soft-delete contract**. A
  user-visible delete in the SPA produces the user-visible
  consequence (position drops) on the next recompute, instead of
  silently keeping the quantity around.
* **Parity with balances** (mig 103). The two derived-state
  systems now use the same source-of-truth predicate.
* **The FUNDX case resolves**. After applying mig 117 and re-
  walking, FUNDX should drop from 5.000000000 → 0, matching
  Moneydance and the broker.
* **No client-side changes**. The Portfolio View doesn't need a
  filter — the recompute already excludes hidden, and the holdings
  row itself surfaces the correct value.

### Negative

* **A previously soft-deleted Buy now produces a different
  cost-basis trajectory**. Before mig 117, the hidden Buy's
  basis still contributed; subsequent sells reduced against
  that inflated basis. After mig 117, that Buy is gone from the
  walk; subsequent sells consume from earlier (or no) basis
  instead. For most users this is a one-time correction; for
  histories with many soft-deleted rows, the basis number may
  shift noticeably. This is the correct contract — the previous
  behavior was an accident — but flag it loudly to the user via
  the migration's one-shot recompute and the position-row
  refresh.
* **Hidden lots dangle in the `lots` table** until the next
  recompute touches that holding. The lot rows themselves
  reference legs of hidden headers; they're not cleaned up
  automatically. Future enhancement: on `is_hidden = TRUE`
  transition for an investment header, also delete its lot rows.
  Not done here; the recompute's `quantity = 0` outcome is
  enough to make the lots irrelevant for any consumer that
  filters `is_closed = FALSE AND quantity > 0`.

## Alternatives considered

* **Filter at the read path** (`HoldingsRepository`): would force
  every consumer of `holdings.quantity` to re-do the work. Worse
  for cache consistency (the stored value diverges from the
  authoritative answer). Rejected.

* **Hard-delete instead of soft-delete on the API side**: would
  also fix the symptom but breaks the resurrection-safety property
  ADR-0023 explicitly designed around. Rejected.

* **Trigger or interceptor on `is_hidden` transitions**:
  redundant. The existing `HoldingsRecomputeInterceptor` (mig 104)
  fires on every `SaveChanges` that touches investment-shape legs;
  the API's soft-delete path touches the header, not the legs, so
  the interceptor doesn't fire — but the explicit recompute call
  in the soft-delete endpoint *does*. Verifying that recompute
  call exists is part of the rollout.

## Migration

Single forward-only migration (117):

1. `CREATE OR REPLACE FUNCTION recompute_holdings_cost_basis(...)` —
   same signature, new body with `is_hidden = FALSE` on both the
   leg walk and the lot reset.
2. `SELECT recompute_holdings_cost_basis();` — one-shot full-
   ledger repair.

No schema changes. No C# changes. No data migrations beyond the
one-shot recompute.

## Follow-up

After ship: verify the SPA `Delete` flow on investment rows
already calls the recompute service (or wakes the interceptor) so
the holdings panel updates without waiting for a server restart.
If it doesn't, that's a tracking follow-up — but the structural
fix here makes the next recompute correct regardless.

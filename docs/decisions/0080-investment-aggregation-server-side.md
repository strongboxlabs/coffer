# 0080 — Investment-transaction aggregation moves server-side (single source)

* Status: Accepted
* Date: 2026-07-17
* Relates to: [0063](0063-mcp-server.md) (MCP), [0036](0036-originating-vs-target-register-entries.md) (target splits), [0065](0065-transfer-shares-in-kind.md), [0064](0064-fifo-cost-basis.md) (FIFO)

## Context

The one-row-per-investment-transaction aggregation is **entirely client-side**
(`src/Web/src/lib/investmentAggregator.ts`): the register endpoint returns raw
per-leg `resolved_transactions` rows and the SPA collapses them (security-cell
sourcing, fee/category/transfer slotting, holdings-sibling stripping, ADR-0036
target-split regrouping). The server has **no** per-transaction investment view.

Exposing an investment `activity` MCP tool needs a server-side per-transaction
view. Building one *next to* the SPA aggregator would duplicate subtle, error-prone
domain logic (two `security` legs per posting, security-id-on-the-cash-leg for
Div/DivXfr/Misc, the single fee leg, transfers/conversions). TS (SPA) and C#
(server) can't literally share code, so "one copy" means **the server owns it and
the SPA consumes its output.**

## Decision

Move the aggregation server-side as the single source:

1. **`InvestmentEventProjector`** — a pure domain service in
   `Coffer.Domain.Investment`: a header's legs → one investment-event row (action,
   security, qty@price, amount, fee, category, transfer). The one copy of the
   collapse logic, anchored on the holdings-sibling / raw-`security_id NOT NULL`
   leg to avoid the two-legs double-count; the qty=0 security-on-cash-leg fallback
   for Div/DivXfr/Misc; the single `fee` leg; `hidden`/`merged` guards matching the
   FIFO tables. Port `investmentAggregator.ts`'s cases + its test suite to C#.
2. **Register read migrates onto it** — `RegisterRepository`, for investment
   accounts, runs the projector on each already-assembled entry's legs and returns
   **aggregated** investment rows. The synthesized slot fields (fee amount + fee
   category, the category / transfer split) become part of `InvestmentRowDto`, so
   the SPA renders them directly. Entry count per page is unchanged — the projector
   collapses *within* an entry, it doesn't re-page.
3. **`investmentAggregator.ts` is deleted; the target-split regroup moves to the
   shared split layer.** The composite-event aggregation (`aggregateLegs` +
   `normalizeSingleLeg`) is gone — the server does it. The one remaining pass —
   clustering consecutive ADR-0036 target-split entries into an expandable
   split-parent — moves to `lib/splitCollapse.ts` as a generic
   `regroupTargetSplits<R>`, applied by **both** registers (registers-unified-by-
   default). It stays client-side because it's cross-page (target entries are
   leg-keyed, so a cluster can straddle a server page); the split-parent's numbers
   come from the **existing shared helpers** — `groupAmount` (which reads the
   server-computed `headerAccountNetAmount`, not a client sum), `groupBalanceAfter`,
   `canonicalLeg` — so investment target-splits are derived identically to bank
   splits and honor overrides.
4. **MCP `activity`** rides directly on the projector; **`allocation`** gains
   `account` + `security` dimensions (small: 2 enum values + 2 switch arms on the
   existing per-security / per-account `HoldingsSnapshot`).

The projector **unifies `aggregateLegs` and `normalizeSingleLeg`** (the SPA's two
paths): a single-leg event is the degenerate case. They diverge in exactly one
spot — a role-less, non-Holdings single leg (a plain categorized brokerage
deposit): `normalizeSingleLeg` preserves its counterparty, bare `aggregateLegs`
blanks it. The projector preserves it (guarded on leg count), so no register
regression.

## Split strategy: one shared collapse, one investment-only projection

"Split" conflates two problems; this ADR keeps them distinct:

- **Split rendering** (a header's legs → collapsed parent + expandable children) is
  ONE shared, server-backed mechanism: `buildDisplayRows` + `groupAmount` /
  `groupBalanceAfter` / `canonicalLeg`, with the parent net from the server's
  `headerAccountNetAmount`. Both registers use it, for both originating groups and
  (now) the regrouped target-split clusters.
- **Composite-event projection** (structural legs of one economic act → one flat
  row with slots) is deliberately investment-only. A bank split is N *independent*
  categorizations the user expands to see; a Buy+Fee is one act shown as a single
  line with security / fee / category slots (ADR-0028), never expanded. That
  display choice is the whole reason the projector exists, and MCP needs it
  server-side regardless. Bank has no equivalent.

All of the above ship in **one PR** so there is never interim duplication of the
aggregation. Both registers' target-split rendering changes → **dev-validate
bank + investment** before merge.

## Consequences

- One authoritative aggregation; `investmentAggregator.ts` is deleted. New server
  consumers (MCP, a future Reports feature) reuse the projector, never re-implement
  it.
- The register read now returns collapsed investment events instead of raw leg
  groups — the core-UI risk, mitigated by porting the aggregator's test suite to
  C# (15 cases, green) + dev validation before merge. Paging is untouched (same
  entries per page).
- The **bank** register now clusters ADR-0036 target-splits into expandable
  parents too (previously investment-only) — a deliberate consistency change, so
  bank rendering is in the dev-validation scope.
- Raw per-leg detail leaves the register response; the edit / Duplicate / raw-data
  consumers move to the all-accounts `/legs` endpoint (`GetAllLegsForHeaderAsync`)
  and `legsByHeaderId` is deleted.
- TS/C# can't share the code; the server is the source of truth and the SPA is a
  consumer (a deliberate asymmetry).

## Out of scope (separate PRs)

- **`realized_gains` short-vs-long-term split** — a FIFO schema/engine change:
  `realized_gains` stores one row per sell leg with **no acquisition date**, and a
  sell can span lots with different holding periods. Needs a per-lot realized
  detail (or a reporting-layer FIFO re-run). Its own PR.
- **`list_transactions` keyset paging** — its own slice.

## Alternatives considered

- **Keep the client aggregator + add a parallel server one.** Rejected: duplicates
  the subtle domain logic in two languages — the exact drift this ADR avoids.

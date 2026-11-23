# 0065 — Transfer-shares (in-kind) action

* Status: Accepted — shipped v0.7.0 (mig 151/152). Engine + editor + D4 guided
  scrub all landed; the scrub ships as the read-only `find_in_kind_transfer_candidates`
  MCP detection tool + the `POST .../in-kind-transfers/convert` apply endpoint (no
  dedicated SPA page — chosen scope). Amended 2026-11-09 (D5: a returns report values
  the transfer's flow at market, not at the carried basis; extended 2026-11-16 by ADR-0114
  D15: the value keeps the 4-place read-side bound). D1–D4 amended 2026-11-14 by
  [ADR-0114](0114-benchmarks-and-currency-conversion.md) (between brokerages in different currencies, each lot's basis converts
  at its acquisition date's rate, and candidates exclude such pairs; extended 2026-11-16,
  D15: the converted basis rounds once at the destination brokerage's currency's places).
* Date: 2026-06-26
* Related: ADR-0027 (action catalog), ADR-0029 (action × field matrix), ADR-0064
  (FIFO lots — this carries them), ADR-0019/0025 (posting model)

## Context

No action moves *shares* between accounts: `transfer` moves cash; `buyx`/`sellx`
are cash-transfer-*funded* buy/sells that still realize a gain. So an in-kind
transfer / ACATS / account rollover (the same shares move X → Y) can only be
recorded as sell-in-X + buy-in-Y, which **fabricates a realized gain in X and
resets the destination basis to transfer-date market** instead of carrying the
original lots. FIFO makes it visible (ADR-0064): a rollover recorded this way reads
as the same security, same date, sold in one account + bought in another, net-zero
quantity.

## Decisions

### D1 — New `transfer_shares` action; zero realized gain; basis carries
Add `transfer_shares` to the `txn_headers.action` CHECK. A transfer posts share
legs spanning two holdings accounts — source holdings leg(s) (−qty) and
destination holdings leg(s) (+qty), **no cash legs** (in-kind: no money moves).
It is **not** a disposition: the source realizes **no** gain (no `realized_gains`
row), and the destination inherits the source's cost basis.

### D2 — Per-lot carry (preserve acquisition dates), modelled as N symmetric pairs
The destination inherits **each moved source lot's `acquired_at` + `unit_cost`**
(not an aggregate single lot) — so future FIFO and short/long-term holding
periods stay correct.

**Mechanism:** at create/scrub time the source's FIFO
lots to move are computed, then the transfer is posted as **one posting per moved
lot** — posting *i* = (source leg −lotᵢ.qty / −lotᵢ.cost, destination leg
+lotᵢ.qty / +lotᵢ.cost), both `posting_role='security'`, no cash side. The
destination lot rows are created **one-per-moved-lot**, each bound to its own
destination leg with the inherited `acquired_at`, `unit_cost` = lotᵢ.unit_cost,
qty = lotᵢ.qty.

The win of one-leg-per-lot: each destination lot is **leg-derived 1:1 exactly
like a normal buy** (leg amount = lot cost, leg qty = lot qty ⇒ the existing
recompute lot-reset re-derives `unit_cost = amount/qty` correctly), so the
recompute's reset and split handling need **no** transfer-specific code. The only
thing that differs from a buy is the lot's `acquired_at` (original, not the
posting date) and the absence of a cash leg.

### D3 — Recompute handling (3 minimal, surgical edits)
`recompute_holdings_cost_basis` (ADR-0064) needs only:
- **Thread `action` into the event stream** (the `txn_legs ⋈ live_txn_headers`
  walk now also selects `hd.action`).
- **Source (−qty leg):** consume lots FIFO and reduce basis by the consumed cost
  as usual, but **skip the `realized_gains` INSERT** when the event's action is
  `transfer_shares` (it's a transfer, not a sale). Basis still leaves the source.
- **Lot-availability gate:** the FIFO consume loop now only consumes lots whose
  **creating leg's header `posted_at` ≤ the sell event's time**. A transfer-in lot
  carries an *earlier* `acquired_at` (original) than its *arrival* (the transfer
  date); without this gate a destination sell dated **before** the transfer-in
  could wrongly consume a not-yet-arrived inherited lot. The gate is also a strict
  correctness improvement for native lots (a sell can never consume a lot bought
  *after* it — previously masked because valid data never needed future lots).

The **destination (+qty legs)** need no special branch: each is an ordinary buy-
shaped event (basis += leg amount = Σ inherited lot cost; qty += leg qty), and the
1:1 leg↔lot reset preserves the inherited unit cost.

**Known constraint:** per-lot carry is frozen at posting/scrub time (D2). Editing
*pre-transfer* source history later won't retroactively re-split an existing
transfer's destination lots — consistent with "computed at create/scrub time."

This is the riskiest part of the slice (cross-account lot carry + recompute
change) and gets the same brokerage-statement validation gate FIFO got.

### D4 — Editor + guided scrub
The investment editor gains a "Transfer shares" action (source acct → dest acct,
security, qty/all). Existing in-kind rollovers mis-modeled as sell+buy are
reclassified via a **guided, reviewable** tool (detect the net-zero
same-security/same-date cross-account pairs; the user reviews + applies after
checking against statements) — **not** a blind auto-migration of real data.

**Shipped shape (chosen scope).** Detection is the **read-only MCP tool**
`find_in_kind_transfer_candidates` (same-security / same-calendar-date /
equal-quantity / distinct-investment-account disposal+acquisition pairs, with
`sourceHadFee`/`destHadFee` flags for a fee the transfer would drop). Apply is the
authenticated **`POST /api/ledgers/{ledgerId}/in-kind-transfers/convert`** endpoint
(`{sellHeaderId, buyHeaderId}`): in one transaction it deletes both headers and
creates the `transfer_shares` (reusing the engine; the delete flush restores the
source lots so the FIFO plan is correct). **No dedicated SPA page** — the user
reviews conversationally via MCP and converts per-pair. Overlapping same-key
matches are all listed; converting one removes its headers from re-detection.

*Amended 2026-10-27 (ADR-0108 D1, D3, D12): what the convert does with memo and tags.* The
transfer keeps both events' values: its memo is the sell's joined with the buy's
(sell first, " · ", when they differ) and its tags are the union of both sets. It
is a system merge, so it is never capped by the tag limit and drops nothing. The
transfer is owned by its source brokerage, and its value sits where any event's
does: on that brokerage's leg when it has one posting, on the header when it has
several (`AnnotationPlanner.ConvertInKind`).

### D5 — Returns value the transfer at market

*Amended 2026-11-09.* A transfer's legs carry the moved lots' **cost basis** (D2),
which is what holdings and realized gains need. A returns report values every
boundary at **market**, so the flow it reads a transfer as is valued at market too:
at account scope (or at ledger scope with a brokerage outside the perimeter) the
flow is the moved shares' market value at the transfer instant, not the sum of the
legs' basis. A flow booked at basis would leave (market − basis) inside the
sub-period that contains the transfer: the destination would read an unearned gain,
the source would read its own appreciation as nothing, and the gain, the
time-weighted chain, the money-weighted cash flows and `contributionsBySource`
(`in_kind_transfers`) would all carry it.

The market value is the share leg's quantity at the unit value the position is
priced at at that instant by the rule every valuation uses
(`holdings_market_value_as_of_set`: the latest close on or before the instant's
date, else the security's last priced trade, both restated onto the instant's split
basis), read for every transfer in one batched query. A security with neither a
close nor a priced trade is carried at cost by every valuation, so there market
equals basis and the flow is unchanged. The flow's instant stays the header's
`posted_at`. Full ledger scope drops the flow and is unaffected.

*Amended 2026-11-16 ([ADR-0114](0114-benchmarks-and-currency-conversion.md) D7, D15).* The
market value is in the price's currency and the flow is in the brokerage's, so a price
in another currency converts at the transfer's day. The value is a read-side figure: it
keeps the 4-place bound every converted figure keeps (`ConvertedTotal.DestinationScale`),
the scale the positions it is a share of are valued at
(`ReportingScale.MarketValuePlaces`), on REST and MCP alike, and is not rounded to the
currency's places. The SPA presents it at the currency's places (settled by Claude,
2026-11-16).

### Amendment, 2026-11-14 — between brokerages in different currencies ([ADR-0114](0114-benchmarks-and-currency-conversion.md) D13; extended 2026-11-16, D15)

D1's "the destination inherits the source's cost basis" and D2's symmetric pairs hold
between brokerages in one currency. Between brokerages in different currencies:

- **D1, D2.** Each leg is in its own account's currency. Posting *i*'s destination leg
  carries lotᵢ's basis converted at the rate for lotᵢ's acquisition date, which an earlier
  move keeps, and rounded once, at the destination brokerage's currency's places, as
  that leg's amount (ADR-0114 D15, 2026-11-16), half away from zero as every figure the
  API computes is rounded (settled by Claude, 2026-11-16). The rate between the two
  brokerages' currencies is derived from the stored rates against the reporting
  currency, never fetched or stored
  (ADR-0114 D13). Each stored rate is read through `RateDay.Of` (ADR-0114 D7, D9) for
  the same day: an acquisition date before the server's UTC today reads the rate on or
  before that day, a lot acquired today reads the latest rate, and a pair whose coverage
  has lapsed has none; either rate missing leaves the lot with none. The first stored
  value for a day wins, and the figure is computed at full precision and rounded only
  once, as above. So the stored basis is reproducible
  from stored rates while the reporting currency stays the same, and a later change of
  reporting currency leaves it as stored. The destination lot's
  `unit_cost` is that amount over its quantity, and its `acquired_at` is still carried.
  If any lot has no rate, the whole move is refused with `rate-unavailable`
  (`BusinessError.Codes.RateUnavailable`), whose `rateUnavailable` names each missing
  stored leg (account currency → reporting currency) and the day (ADR-0114 D9, D13).
  Each account currency's rates are needed from the ledger's earliest day (ADR-0114
  D11), so a lot acquired before its current or its new brokerage existed is covered;
  a day before the provider's history still has no rate, and the move is refused. This is the one stored conversion (ADR-0114 D7). A transfer's prefilled
  second amount and an estimating reminder's other side are entry figures, written once
  with their transaction, not conversions of a stored figure. The source still realizes
  no gain.
- **D3.** The destination leg's amount is the converted basis, so the 1:1 leg↔lot reset
  yields the converted unit cost.
- **D4.** `find_in_kind_transfer_candidates` excludes pairs whose two investment accounts
  are in different currencies.

## Consequences

- Correct in-kind transfers/rollovers: no fabricated gains, basis + holding
  periods preserved.
- Recompute gains transfer-aware lot handling; the editor computes the FIFO split
  at posting time. Both are held to the statement-reconciliation gate of ADR-0064 D4.

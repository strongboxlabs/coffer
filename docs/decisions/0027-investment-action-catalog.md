# 0027 — Ledger investment action catalog

* Status: Accepted (action catalog + MD txntype sources + per-splittype posting role all locked; importer-side classification rules grounded in real-world MD-export data). **Extended by [ADR-0065](0065-transfer-shares-in-kind.md)**, which adds the Ledger-native `transfer_shares` action (in-kind share move; no MD txntype source).
* Date: 2026-05-20 (action catalog), 2026-05-21 (txntype sources + splittype → posting role + bare-row classification)
* Companion to: [moneydance-investment-actions.md](../moneydance-investment-actions.md) — the MD-side ground-truth reference this catalog maps from.

## Context

`txn_headers.action` carries the investment-event type. The set needs
to be locked before A4 (the investment-transaction editor) can spec
form templates. Pre-A4 the set had drifted into a 10-action list with
three structurally-equivalent actions (`interest` / `misc_income` /
`misc_expense`), no first-class representation for MD's compound
types (`buyx` / `sellx` / `divx`), and one non-event (`split` — moved
to `security_splits` by B0.7).

MD-side ground truth is locked separately in
[moneydance-investment-actions.md](../moneydance-investment-actions.md):
12 `invest.txntype` values (most appear in real-world data; a few are sample-only),
discriminated by `invest.txntype` alone (every other top-level field
is either redundant or intermittent).

## Decision — accepted

### Catalog: 9 Ledger actions, 1:1 to MD txntypes

| # | Ledger `action`     | MD `invest.txntype` |
|---|---------------------|---------------------|
| 1 | `buy`               | `buy`               |
| 2 | `buyx`              | `buyx`              |
| 3 | `sell`              | `sell`              |
| 4 | `sellx`             | `sellx`             |
| 5 | `dividend_cash`     | `div`               |
| 6 | `dividend_reinvest` | `divr`              |
| 7 | `divx`              | `divx`              |
| 8 | `transfer`          | `bank`              |
| 9 | `misc`              | `inc`, `exp`        |

Notes:

  - `misc` is the only Ledger action sourced from more than one MD
    txntype. It coalesces what were three pre-A4 Ledger actions
    (`interest`, `misc_income`, `misc_expense`) since MD has one
    txntype family (`inc` / `exp`) for all of them. Income vs
    expense at read time is disambiguated by the sign of the
    brokerage-side leg's amount.
  - MD's standalone `xfr` is not a top-level investment txntype in
    real-world data and is not in the importer's discriminator;
    `xfr` only appears as a *split* type inside compound (`*x`,
    `bank`) txns. Ledger's `transfer` action is sourced from MD
    `bank`.

### Not supported

| MD `invest.txntype` | Ledger `action` |
|---------------------|-----------------|
| `short`             | — (deferred)    |
| `cover`             | — (deferred)    |

Neither is in real-world data nor in the importer's
discriminator. Adding them later requires extending the catalog +
the importer's pattern-match table.

## Decision — txntype sources (importer dispatch)

`invest.txntype` is canonical when MD has it, but about a third of
investment-shape txns in long-history real exports don't carry
it (OFX-imported and older QIF-imported rows predate MD's
tagging-on-import). The importer reads **three ordered sources**
and dispatches on the first that resolves:

  1. **`invest.txntype`** (primary) — set on every natively-
     entered txn and every recent OFX/QIF import. 1:1 with
     `xfer_type`; see the catalog table at the top of this ADR.

  2. **`qif_invst_action`** (secondary) — set on QIF-imported
     txns that predate MD's `invest.txntype` tagging. The full
     observed vocabulary maps to `invest.txntype` as follows:

     | `qif_invst_action` | (companion `xfer_type`) | → `invest.txntype` | → Ledger `action` |
     |---|---|---|---|
     | `Buy`              | `xfrtp_buysell`     | `buy`   | `buy` |
     | `BuyX`             | `xfrtp_buysellxfr`  | `buyx`  | `buyx` |
     | `Sell`             | `xfrtp_buysell`     | `sell`  | `sell` |
     | `SellX`            | `xfrtp_buysellxfr`  | `sellx` | `sellx` |
     | `ShrsIn`           | `xfrtp_buysellxfr`  | `buyx`  (basis-preserving shares in)  | `buyx` |
     | `ShrsOut`          | `xfrtp_buysellxfr`  | `sellx` (basis-preserving shares out) | `sellx` |
     | `Div`              | `xfrtp_dividend`    | `div`   | `dividend_cash` |
     | `DivX`             | `xfrtp_dividendxfr` | `divx`  | `divx` |
     | `IntInc`           | `xfrtp_dividend`    | `div`   (interest cash = same shape as div) | `dividend_cash` |
     | `IntIncX`          | `xfrtp_dividend`    | re-route via the `inc`+`xfr` split-structure special case  | `divx` |
     | `ReinvDiv`         | `xfrtp_dividend`    | `divr`  | `dividend_reinvest` |
     | `ReinvInt`         | `xfrtp_dividend`    | `divr`  (reinvested interest)              | `dividend_reinvest` |
     | `ReinvLg`          | `xfrtp_dividend`    | `divr`  (reinvested long-term cap gain)    | `dividend_reinvest` |
     | `ReinvMd`          | `xfrtp_dividend`    | `divr`  (reinvested medium-term cap gain)  | `dividend_reinvest` |
     | `ReinvSh`          | `xfrtp_dividend`    | `divr`  (reinvested short-term cap gain)   | `dividend_reinvest` |
     | `XIn`              | `xfrtp_bank`        | `bank`  | `transfer` |
     | `XOut`             | `xfrtp_bank`        | `bank`  | `transfer` |
     | `Cash`             | `xfrtp_bank`        | `bank`  | `transfer` |
     | `Cash`             | `xfrtp_miscincexp`  | `inc`   (edge: MD shape is misc-income despite QIF "Cash" name) | `misc` |
     | `ContribX`         | `xfrtp_bank`        | `bank`  (retirement contribution = bank transfer in MD) | `transfer` |
     | `MiscIncX`         | `xfrtp_bank`        | `bank`  (MD shape is pure transfer — only `xfr` split, no `inc` split — QIF action name is misleading) | `transfer` |

  3. **Structural classification** (tertiary) — when both tags
     are absent (bare rows: typically older OFX-imported txns).
     Reads observable MD signals:

     | `xfer_type` | Discriminator | → `invest.txntype` |
     |---|---|---|
     | `xfrtp_buysell`     | sec.samt < 0 | `sell`  |
     | `xfrtp_buysell`     | sec.samt ≥ 0 | `buy`   |
     | `xfrtp_buysellxfr`  | sec.samt < 0 | `sellx` |
     | `xfrtp_buysellxfr`  | sec.samt ≥ 0 (incl. zero-qty basis xfer) | `buyx` |
     | `xfrtp_dividend`    | `reinvest = true` OR sec.samt > 0 | `divr` |
     | `xfrtp_dividend`    | no `reinvest`, sec.samt = 0       | `div`  |
     | `xfrtp_dividendxfr` | —                                  | `divx` |
     | `xfrtp_miscincexp`  | has `inc` splittype                | `inc`  |
     | `xfrtp_miscincexp`  | has `exp` splittype                | `exp`  |
     | `xfrtp_bank`        | —                                  | `bank` |

     **This is classification, not inference.** Every signal is
     a field MD wrote (`xfer_type`, `reinvest`, sec-split `samt`,
     split structure). No heuristic guessing.

The importer's function previously named `InferInvestTxnType`
is renamed to `ClassifyInvestTxnType` to reflect that it reads
observable structure rather than inferring.

## Decision — splittype → posting_role

Each MD `invest.splittype` maps 1:1 to a Ledger
`txn_legs.posting_role` value (migration 056). The role is
**immutable to category-account changes** — it captures the
posting's structural intent at import time and downstream code
dispatches off it (no category sniffing).

| MD `invest.splittype` | Appears on MD txntypes | → Ledger `posting_role` |
|---|---|---|
| `sec` | buy, sell, buyx, sellx, divr (plus zero-qty on div/divx/inc/exp/short/cover) | `'security'` |
| `inc` | div, divr, divx, inc | `'income'` |
| `exp` | exp (Misc-expense) | `'income'` *(sign on amount discriminates direction)* |
| `fee` | optional on every fee-eligible txntype | `'fee'` |
| `xfr` | buyx, sellx, divx, bank | `'transfer'` |

**Misc inc/exp collapse**: MD's `inc` and `exp` splittypes both
map to Ledger `posting_role = 'income'`. The Ledger `action` is
`'misc'` (per the catalog above) regardless of MD direction;
amount sign on the brokerage-side leg discriminates income from
expense at read time. This matches how the Ledger `action`
itself collapses `inc`/`exp` into a single `'misc'` value.

The `posting_role` value space is closed:
`{'security', 'income', 'transfer', 'fee'}` — no `'expense'`
or other directional variants. Direction is sign-on-amount,
everywhere.

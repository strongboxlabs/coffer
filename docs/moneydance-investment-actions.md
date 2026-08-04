# Moneydance investment-transaction data shapes

* Status: Accepted. Drafted from `data/samples/moneydance-export-demo.json` (23 txns covering each MD UI investment action with and without a fee where MD permits one; MD 2024.4 build 5253), cross-checked against a long-history real export covering OFX-imported, QIF-imported, and natively-entered txns.
* Purpose: ground-truth reference for what Moneydance emits in JSON for each investment-transaction type, before any Ledger-side mapping decision.

## Sources

  - **Sample**: `data/samples/moneydance-export-demo.json` — 23
    investment txns exercising each MD UI investment action in
    both its with-fee and no-fee forms (where MD permits a fee).
    `bank` is the only txntype that has no fee variant.
  - **Long-history real-export verification**: claims about
    field presence / absence on OFX-imported and QIF-imported
    txns were cross-checked against a real export but no real-
    export counts are reproduced here (the file isn't in the
    repo and would leak account-level shape data).

## Three sources for txntype classification

A txn's intended shape comes from one of three signals, in this
priority order. **`invest.txntype` is canonical when present, but
not every investment-shape txn carries it** — OFX-imported and
older QIF-imported txns predate the field. The importer must read
all three and dispatch on the first that resolves.

  1. **`invest.txntype`** — primary tag, MD's native canonical
     type field. Set on every natively-entered txn and on every
     OFX/QIF txn imported after MD started tagging during import.
     1:1 with the txn's intended shape; see the per-type table
     below.
  2. **`qif_invst_action`** — secondary, for QIF-imported txns
     where MD didn't backfill `invest.txntype`. Carries the
     original QIF action verbatim (`Buy`, `ReinvDiv`, `ShrsIn`,
     etc.). Maps 1:N to `invest.txntype` (e.g. all five reinvest
     variants → `divr`). Full mapping in
     [ADR-0027](decisions/0027-investment-action-catalog.md).
  3. **Structural classification** — tertiary, for the bare-row
     case where both tags are absent (OFX-imported old txns +
     QIF-imported pre-tagging-era txns). Reads `xfer_type` +
     `reinvest` flag + sec-split's `samt` sign + presence of an
     `xfr` splittype. Deterministic on observed MD data; see
     "Bare-row structural classification" below.

Source 3 is **classification from observable signals**, not
inference. Every fact comes from a field MD wrote.

## Primary source: `invest.txntype`

When present, `invest.txntype` is 1:1 with the txn's shape. All
12 txntypes from the demo sample, paired with their `xfer_type`:

| `invest.txntype` | Paired `xfer_type` | Sample count | `reinvest` present? |
|------------------|--------------------|-------------:|---------------------|
| `buy`            | `xfrtp_buysell`     | 2 | absent |
| `buyx`           | `xfrtp_buysellxfr`  | 2 | absent |
| `sell`           | `xfrtp_buysell`     | 2 | absent |
| `sellx`          | `xfrtp_buysellxfr`  | 2 | absent |
| `short`          | `xfrtp_shortcover`  | 2 | n/a    |
| `cover`          | `xfrtp_shortcover`  | 2 | n/a    |
| `div`            | `xfrtp_dividend`    | 2 | absent |
| `divr`           | `xfrtp_dividend`    | 2 | absent |
| `divx`           | `xfrtp_dividendxfr` | 2 | absent |
| `bank`           | `xfrtp_bank`        | 1 | absent |
| `inc`            | `xfrtp_miscincexp`  | 2 | absent |
| `exp`            | `xfrtp_miscincexp`  | 2 | absent |

The (`invest.txntype` → `xfer_type`) mapping is 1:1 — given
`invest.txntype`, `xfer_type` is fully redundant.

### About `xfer_type`

Coarser grouping than `invest.txntype`. Multiple txntypes share
an `xfer_type`:

  - `buy` and `sell` both → `xfrtp_buysell`
  - `buyx` and `sellx` both → `xfrtp_buysellxfr`
  - `div` and `divr` both → `xfrtp_dividend`
  - `short` and `cover` both → `xfrtp_shortcover`
  - `inc` and `exp` both → `xfrtp_miscincexp`

Not a sufficient discriminator on its own when `invest.txntype`
is set; useful as a sanity check, and becomes load-bearing in
sources 2 and 3 (where `invest.txntype` is absent).

### About `reinvest`

The sample file (MD 2024.4 build 5253) emits `reinvest = true`
for `divr` and `reinvest = false` for `div`. On `invest.txntype`-
tagged real-export rows the field is **absent across the board**
— either an exporter-version artifact or emitted only under
specific conditions we haven't isolated. So when `invest.txntype`
is present, do NOT rely on `reinvest`; use `invest.txntype` to
distinguish `div` from `divr`.

`reinvest = true` IS observed on a subset of **bare-row OFX-
imported reinvest dividends** (rows that carry neither
`invest.txntype` nor `qif_invst_action`). The bare-row
structural-classification path uses it as a primary discriminator
between `div` and `divr`; see the table below.

## Secondary source: `qif_invst_action`

QIF-imported txns that predate MD's `invest.txntype` tagging
carry the original QIF action in `qif_invst_action`. The
discovered vocabulary in real-world MD-from-QIF data:

```
Buy  BuyX  Sell  SellX  ShrsIn  ShrsOut
Div  DivX  IntInc  IntIncX
ReinvDiv  ReinvInt  ReinvLg  ReinvMd  ReinvSh
XIn  XOut  Cash  ContribX  MiscIncX
```

Each maps to exactly one `invest.txntype` (sometimes via the
companion `xfer_type` for edge cases like `Cash`); the full
mapping is in [ADR-0027](decisions/0027-investment-action-catalog.md).

## Tertiary source: bare-row structural classification

For rows carrying NEITHER `invest.txntype` NOR `qif_invst_action`
— typically OFX-imported txns from before MD started backfilling
the canonical tag — every observed row classifies deterministically
from MD's own structural signals:

| `xfer_type` | Discriminator | → `invest.txntype` |
|-------------|---------------|--------------------|
| `xfrtp_buysell`     | sec.samt < 0                                | `sell` |
| `xfrtp_buysell`     | sec.samt ≥ 0                                | `buy`  |
| `xfrtp_buysellxfr`  | sec.samt < 0                                | `sellx` |
| `xfrtp_buysellxfr`  | sec.samt ≥ 0 (incl. zero-qty basis xfer)    | `buyx` |
| `xfrtp_dividend`    | `reinvest = true` OR sec.samt > 0           | `divr` |
| `xfrtp_dividend`    | no `reinvest`, sec.samt = 0                 | `div`  |
| `xfrtp_dividendxfr` | —                                            | `divx` |
| `xfrtp_miscincexp`  | has `inc` splittype                          | `inc`  |
| `xfrtp_miscincexp`  | has `exp` splittype                          | `exp`  |
| `xfrtp_bank`        | —                                            | `bank` |

Every signal here is a field MD wrote — `xfer_type` at the top
level, `reinvest` at the top level when present, sec-split
amounts in the per-split block. **No guessing.** The `xferdir`
field inside `ol.orig-txn` (the embedded OFX payload) further
confirms direction on zero-qty basis-transfer rows (`xferdir =
"IN"` → `buyx`).

### Other common top-level fields

`obj_type=txn`, `id`, `acctid` (the brokerage account), `dt`
(yyyymmdd), `td` (transacted date, yyyymmdd), `dtentered` (millis),
`ts` (millis), `memo`, `desc`, `chk`, `stat`. These are present on
all investment txns and not type-specific.

## Split composition per txntype

Each txn has N splits encoded as numeric-prefixed fields
(`0.acctid`, `0.invest.splittype`, etc.). Each split carries a
required `invest.splittype` from the closed set
`{sec, fee, inc, exp, xfr}` (no other splittype values appear in
either source).

Split index order is **not** stable across txntypes (e.g. `div`
orders `[sec, fee, inc]` while `divr` orders `[sec, inc, fee]`).
Importer code must dispatch on splittype, not on index.

The table below lists splittype sets (sorted alphabetically for
readability) per txntype, with sample counts:

| `invest.txntype` | Splittype set            | Sample count |
|------------------|--------------------------|-------------:|
| `bank`           | `[xfr]`                  | 1 |
| `buy`            | `[sec]`                  | 1 |
| `buy`            | `[fee, sec]`             | 1 |
| `buyx`           | `[sec, xfr]`             | 1 |
| `buyx`           | `[fee, sec, xfr]`        | 1 |
| `cover`          | `[sec]`                  | 1 |
| `cover`          | `[fee, sec]`             | 1 |
| `div`            | `[inc, sec]`             | 1 |
| `div`            | `[fee, inc, sec]`        | 1 |
| `divr`           | `[inc, sec]`             | 1 |
| `divr`           | `[fee, inc, sec]`        | 1 |
| `divx`           | `[inc, sec, xfr]`        | 1 |
| `divx`           | `[fee, inc, sec, xfr]`   | 1 |
| `exp`            | `[exp, sec]`             | 1 |
| `exp`            | `[exp, fee, sec]`        | 1 |
| `inc`            | `[inc, sec]`             | 1 |
| `inc`            | `[fee, inc, sec]`        | 1 |
| `sell`           | `[sec]`                  | 1 |
| `sell`           | `[fee, sec]`             | 1 |
| `sellx`          | `[sec, xfr]`             | 1 |
| `sellx`          | `[fee, sec, xfr]`        | 1 |
| `short`          | `[sec]`                  | 1 |
| `short`          | `[fee, sec]`             | 1 |

Verified observations (cross-checked against real-world MD-export
data):

  - **Every txntype except `bank` has a `sec` split.** Even
    semantically-cashless `inc`/`exp`/`div`/`divx` events emit
    the `sec` split with `pamt = 0`, `samt = 0`, a `rate`, and
    an `acctid` pointing at the security.
  - **`bank` always has exactly one `[xfr]` split.**
  - **`fee` split is optional.** MD omits it entirely when no
    fee was entered. Both no-fee and with-fee variants exist for
    every fee-eligible txntype.
  - **`xfr` splittype appears only on the `*x` and `bank`
    txntypes.**
  - **`inc` splittype appears on `div`, `divr`, `divx`, `inc`.**
  - **`exp` splittype appears only on `exp`.**
  - **No multi-`inc` shapes in raw MD.** The 4 "compound MiscInc"
    headers that migration 058 split (workplace-plan-import "Change in
    Market Value" events) were the standard `[fee, inc, sec]`
    shape; the multi-posting structure was created by the
    importer when it paired the three splits into two Ledger
    postings, not by MD emitting multiple `inc` splits. This was
    a Ledger-side artifact, not an MD-side anomaly.

## Split field shape

Each split carries: `acctid`, `id`, `desc`, `invest.splittype`,
`pamt` (amount in the txn's primary account's minor units, signed),
`samt` (amount in the split account's minor units, signed),
optional `rate` (present on `sec` splits — relates pamt and samt
when units differ), optional `stat` (present on `sec` splits in the
sample).

Sign conventions observed in the sample:

  - `sec` split: `pamt` is the cash effect on the brokerage account
    (negative on Buy/Cover/Short pre-cover, positive on Sell/Short
    sale opening); `samt` is the share-side effect (positive on
    acquisitions, negative on dispositions).
  - `fee` split: `pamt` always negative (cash leaving brokerage);
    `samt` always positive (expense category booking the fee).
  - `inc` split: `pamt` positive (cash arriving at brokerage);
    `samt` negative (income category booking the income).
  - `exp` split: same sign convention as `fee`.
  - `xfr` split: signs invert depending on direction — positive
    `pamt` for transfer-in (buyx, the initial-deposit bank),
    negative `pamt` for transfer-out (sellx, divx).

## Stock splits (`csplit`)

The sample also contains one `csplit` object (security-level split
metadata, not a transaction). Documented in B0.7 (migration 060);
fields: `id`, `curr` (security ref), `dt` (yyyymmdd), `ratio`,
`oldshrs`, `newshrs`, `ts`.

## Remaining gaps

  - **Multi-currency txns** — sample is USD-only; cross-currency
    investment txns not yet exercised.
  - **`chk` value variations** — sample uses `Auto`, `EXfr`,
    `Xfr`, and numeric values. The full vocabulary of values MD
    emits across all import paths (OFX, QIF, manual) is not
    catalogued.

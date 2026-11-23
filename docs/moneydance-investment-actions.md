# Moneydance investment-transaction data shapes

* Status: Accepted. Drafted from placeholder investment rows once kept in `data/samples/moneydance-export-demo.base.json` (each MD UI investment action with and without a fee where MD permits one; recorded from MD 2024.4). Those rows are gone from the base. The tables below still record that MD 2024.4 recording, with-fee variants and `short`/`cover` included; they are not a list of what a fixture carries now. The C# Demo generator (`src/Api/Provisioning/Demo/`) writes the no-fee form of every ADR-0027 action as a dated event, and a fee only on stock trades (a stock `buy` and one stock `sell`); it never writes `short` or `cover`. The QIF-origin shapes the Demo cannot carry are exercised by `data/samples/reference-ledger.json`.
* Purpose: ground-truth reference for what Moneydance emits in JSON for each investment-transaction type, before any Ledger-side mapping decision.

## Sources

  - **Demo**: the generated Demo export
    (`src/Api/Provisioning/Demo/`, `DemoExportGenerator`). The
    placeholder rows this document was drafted from are gone from
    the curated base. The generator carries the no-fee form of each
    ADR-0027 action as a dated event: the brokerage's regular buys,
    the rollover's `buyx` funded from the old plan, the down
    payment's `sellx` and brokerage-owned `bank` cash move, `div`,
    `divr` and `divx` dividends, and the employer match (`inc`) and
    HSA medical spending (`exp`). Only stock trades carry a
    commission `fee` split: stock buys, and one brokerage stock
    `sell`. The other with-fee variants, and `short`/`cover`, exist
    only in the MD 2024.4 recording the tables below describe.
  - **Reference ledger**: `data/samples/reference-ledger.json` —
    synthetic and deliberately pathological; carries the
    QIF-origin classification path (`qif_invst_action`, blank
    `invest.txntype`).

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
     `xfr` splittype. Deterministic, because every signal is a
     field MD wrote; see "Bare-row structural classification" below.

Source 3 is **classification from observable signals**, not
inference. Every fact comes from a field MD wrote.

## Primary source: `invest.txntype`

When present, `invest.txntype` is 1:1 with the txn's shape. All
12 txntypes of the original MD 2024.4 recording, paired with their
`xfer_type` (the count column counts rows of that recording, not
of any current fixture):

| `invest.txntype` | Paired `xfer_type` | Recorded count | `reinvest` present? |
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
for `divr` and `reinvest = false` for `div`, but MD does **not
emit it reliably** on `invest.txntype`-tagged rows: it can be
absent, and what decides it (exporter version or some other
condition) is not isolated. So when `invest.txntype`
is present, do NOT rely on `reinvest`; use `invest.txntype` to
distinguish `div` from `divr`.

`reinvest = true` CAN appear on **bare-row OFX-
imported reinvest dividends** (rows that carry neither
`invest.txntype` nor `qif_invst_action`). The bare-row
structural-classification path uses it as a primary discriminator
between `div` and `divr`; see the table below.

## Secondary source: `qif_invst_action`

QIF-imported txns that predate MD's `invest.txntype` tagging
carry the original QIF action in `qif_invst_action`. The
vocabulary the importer maps:

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
the canonical tag — the row classifies deterministically from MD's
own structural signals:

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
`{sec, fee, inc, exp, xfr}` (the importer skips a txn carrying
any other value, and the import reports it as dropped).

Split index order is **not** stable across txntypes (e.g. `div`
orders `[sec, fee, inc]` while `divr` orders `[sec, inc, fee]`).
Importer code must dispatch on splittype, not on index.

The table below lists splittype sets (sorted alphabetically for
readability) per txntype, with counts from the original MD 2024.4
recording:

| `invest.txntype` | Splittype set            | Recorded count |
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

Split-composition rules the importer relies on:

  - **Every txntype except `bank` has a `sec` split.** Even
    semantically-cashless `inc`/`exp`/`div`/`divx` events emit
    the `sec` split with `pamt = 0`, `samt = 0`, a `rate`, and
    an `acctid` pointing at the security.
  - **`bank` always has exactly one `[xfr]` split.**
  - **`fee` split is optional.** MD omits it entirely when no
    fee was entered. MD can write both no-fee and with-fee
    variants of every fee-eligible txntype (the recording has
    both); the importer must accept either.
  - **`xfr` splittype appears only on the `*x` and `bank`
    txntypes.**
  - **`inc` splittype appears on `div`, `divr`, `divx`, `inc`.**
  - **`exp` splittype appears only on `exp`.**
  - **No multi-`inc` shapes in raw MD.** MD emits the standard
    `[fee, inc, sec]` shape; pairing those three splits into two
    Ledger postings yields a multi-posting "compound MiscInc"
    header (the shape migration 058 split), which is a Ledger-side
    artifact, not MD emitting multiple `inc` splits.

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

## Memo and tags

An investment transaction shows ONE memo and ONE tag set in Coffer,
edited from the brokerage
([ADR-0108](decisions/0108-memo-and-tags-per-side.md)). Its other
sides (the category, the bank account of an `*x` transfer, the
holdings account) are read-only rows, so nothing Moneydance keeps
for them may stay there. `MoneydanceNotes.InvestmentEvent` merges it
all into the event's value:

  - **Memo:** the txn's `memo` (else the feed's `ol.orig-memo`),
    trimmed, then each non-`sec` split's `desc` that is the user's
    own (not blank, not equal to the txn's `desc`, the payee),
    joined in split order as "memo · other", each distinct memo
    once (`AnnotationText.JoinDistinctMemos`): texts a, b, a give
    "a · b". Memos are compared trimmed and case-sensitively, each
    whole. Migration 264 joins an upgraded event's memos by the same
    rule (`mig264_join_distinct`, Steps B and D).
  - **Tags:** the txn's `tags` and every split's `N.tags`, each name
    once, resolved case-insensitively against the ledger's tags.
    A merged set is never capped and never drops a tag.

Where the value lands depends on the posting count:

  - **One posting** (a `bank` txn, an Xfr): on the brokerage leg, as
    on any one-split; the other side shows it inherited. The header
    carries nothing.
  - **Several postings:** on the header. The investment side's legs
    carry nothing of their own, and each opposite side shows the
    event's value inherited.

Ledgers imported before migration 264 got the memo on the header
and no tags; 264 rewrites such rows to this placement from
`provider_raw_payload`. It rewrites a memo only while the event is
as imported, and adds tags only to an event that has none, so a
memo the user has edited, or an event the user has tagged, is left
as it is.

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

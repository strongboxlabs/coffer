# 0027 — Ledger investment action catalog

* Status: Accepted (action catalog + MD txntype sources + per-splittype posting role all locked). **Extended by [ADR-0065](0065-transfer-shares-in-kind.md)**, which adds the Ledger-native `transfer_shares` action (in-kind share move; no MD txntype source). Amended 2026-11-14 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (an Xfr whose transfer account is a real account in another currency also edits that account's own amount).
* Date: 2026-05-20 (action catalog), 2026-05-21 (txntype sources + splittype → posting role + bare-row classification), 2026-10-21 (amendment: brokerage cash rows on the bank route, `chk` kept on both routes; mig 260), 2026-11-04 (amendment: each action's display word, from one table the SPA and the API read)
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
12 `invest.txntype` values,
discriminated by `invest.txntype` alone (every other top-level field
is either redundant or intermittent).

## Decision — accepted

### Catalog: 9 Ledger actions, 1:1 to MD txntypes

| # | Ledger `action`     | MD `invest.txntype` | Display word  |
|---|---------------------|---------------------|---------------|
| 1 | `buy`               | `buy`               | Buy           |
| 2 | `buyx`              | `buyx`              | BuyXfr        |
| 3 | `sell`              | `sell`              | Sell          |
| 4 | `sellx`             | `sellx`             | SellXfr       |
| 5 | `dividend_cash`     | `div`               | Dividend      |
| 6 | `dividend_reinvest` | `divr`              | Reinvest      |
| 7 | `divx`              | `divx`              | DividendXfr   |
| 8 | `transfer`          | `bank`              | Xfr           |
| 9 | `misc`              | `inc`, `exp`        | Misc          |

The display word is how every surface shows the action: the register's action
chip and its feed-hint chip ("↗ Dividend"), the Overview's lists, a merge
candidate, the editor's action picker and the Action column of a security's
transactions on its page, which sets the word as the chip prints it, in the
column's small muted type and never uppercased or letter-spaced. ADR-0065's `transfer_shares` reads "Transfer shares".
A leg with no header action whose other side is a non-category account reads
the derived `Xfr` (ADR-0036); one whose other side is a category has no derived
word. One table holds the words: `src/Web/src/lib/investmentActionWords.json`,
keyed by the stored value. The SPA imports it as `ACTION_LABEL` in
`investmentStrategy.tsx`, read through `formatInvestmentAction`, and the API
embeds the same file (`InvestmentActionWords`) for the investment register's
Action sort, which orders by the word ([ADR-0028](0028-investment-register-surface.md)).
The picker's `ACTION_PICKER_ENTRIES` keeps only the
order and the hints and takes each label from it, and `actionLayout.test.ts`
holds the picker and the file to it and pins every word; on the server,
`InvestmentActionWordsTests` holds the file to every `LedgerActions` value. Only
the words are display: the stored `action`, the action value the API returns and
the import verbs a source file uses (QIF `Div`, `ReinvDiv`, …, below) are not
words and do not follow them; only the register's Action sort, which the server
runs, reads the words ([ADR-0028](0028-investment-register-surface.md)). *(Amended 2026-11-04: the dividend actions read Dividend, Reinvest
and DividendXfr, where they read Div, DivReinvest and DivXfr, and one table,
which the SPA and the API both read, holds every word.)*

Notes:

  - `misc` is the only Ledger action sourced from more than one MD
    txntype. It coalesces what were three pre-A4 Ledger actions
    (`interest`, `misc_income`, `misc_expense`) since MD has one
    txntype family (`inc` / `exp`) for all of them. Income vs
    expense at read time is disambiguated by the sign of the
    brokerage-side leg's amount.
  - MD's standalone `xfr` is not in the importer's discriminator as
    a top-level investment txntype; the importer handles `xfr` only
    as a *split* type inside compound (`*x`, `bank`) txns. Ledger's `transfer` action is sourced from MD
    `bank`.

### Not supported

| MD `invest.txntype` | Ledger `action` |
|---------------------|-----------------|
| `short`             | — (deferred)    |
| `cover`             | — (deferred)    |

Neither is in the importer's discriminator. Adding them later requires extending the catalog +
the importer's pattern-match table.

## Decision — txntype sources (importer dispatch)

`invest.txntype` is canonical when MD has it, but investment-shape
txns that predate MD's tagging-on-import (OFX-imported and older
QIF-imported rows) don't carry it. The importer reads **three ordered sources**
and dispatches on the first that resolves:

  1. **`invest.txntype`** (primary) — set on every natively-
     entered txn and every recent OFX/QIF import. 1:1 with
     `xfer_type`; see the catalog table at the top of this ADR.

  2. **`qif_invst_action`** (secondary) — set on QIF-imported
     txns that predate MD's `invest.txntype` tagging. The
     vocabulary the importer recognises maps to `invest.txntype` as follows:

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
     are absent (bare rows, such as older OFX-imported txns).
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

The importer's function is named `ClassifyInvestTxnType` because it
reads observable structure rather than inferring.

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

## Amendment, 2026-10-21 — brokerage cash rows on the bank route are transfers, and `chk` is kept (mig 260)

**The `qif_invst_action` table above was only half applied.** Its `XIn` / `XOut` /
`Cash` / `ContribX` / `MiscIncX` rows with `xfrtp_bank` say Ledger action
`transfer`, but the importer never reaches the txntype dispatch for them:
`MdTxn.IsInvestmentShape` routes a txn to the investment mapper only when
`invest.txntype` is set or `xfer_type` is one of `xfrtp_buysell`, `xfrtp_buysellxfr`,
`xfrtp_dividend`, `xfrtp_dividendxfr`, `xfrtp_miscincexp`. A QIF-origin cash row
(`xfrtp_bank`, no txntype) and a plain brokerage row (no `xfer_type` at all) took the
BANK route, which writes `action` NULL and `posting_role` NULL. Where the other side
was a real account the register still derived `'Xfr'`; where it was a CATEGORY it
derived nothing (`derived_action`, migration 230), and the investment editor opened
on "Pick action...".

- **The rule.** A bank-route row whose home account is a brokerage (Coffer
  `account_type = 'investment'`) and which emits exactly ONE posting, counted after
  the self-reference filter, is action `transfer` with `posting_role = 'transfer'` on
  both legs: what the investment route writes for txntype `bank`
  (`InvestmentPostings.BuildXferPair`). It covers the QIF cash words and plain rows
  alike; the QIF word itself stays in `check_number`. One shared rule,
  `BrokerageCashTransfer.Classify`, serves `TransactionMapper` and `ReminderMapper`, so a
  reminder template classifies as a live row of the same shape does.
- **Reminders.** A brokerage reminder is an investment reminder: a
  template whose embedded txn has ONE emittable split and is NOT investment-shaped
  (`MdReminderTxn.IsInvestmentShape`, the same test as `MdTxn.IsInvestmentShape` over
  `txn.invest.txntype` / `txn.xfer_type`) is `transfer` like the live row, because the
  bank route only ever sees txns that are not investment-shaped. Its occurrences clone the
  template's action and roles when fired, so they are Xfr rows fired from the brokerage.
  An investment-shaped template keeps `action` NULL, as before.
- **Multi-split fails closed.** `transfer` means cash moving between the brokerage and
  ONE other account, and the Xfr editor edits one amount and one transfer account; a row
  with two or more postings cannot be shown faithfully there, and saving it would drop
  the postings the editor does not show. So it keeps `action` NULL, with every posting
  intact, rather than asserting a shape. A row whose home is a
  bank account is untouched, including one whose counterparty is a brokerage.
  *(Amended 2026-11-14, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D14: "one amount" holds for a same-currency or
  category transfer side. When the transfer account is a real account in another
  currency, the Xfr editor also edits that account's own amount, Received or Sent, right
  of Amount, with the rate line under it, so a save keeps the far leg it shows. BuyXfr,
  SellXfr and DividendXfr pair it with the trade amount, not the fee.)*
- **The transfer side may be a category.** That is a named category (an employer
  contribution arriving, a withdrawal leaving) or, for Moneydance's blank-named
  category, the ledger's system Uncategorized: on a fresh import a blank-named
  TOP-LEVEL LEAF category (its parent MD's root or none, no children) lands on that
  system row (the one bank feeds create) instead of becoming an "(unnamed)" category of
  its own. The system row is an expense-kind category whatever MD's kind was, `i` or `e`:
  Uncategorized means "not yet classified", as bank feeds use it,
  not a direction. A blank category with children, or nested under another category,
  keeps its own "(unnamed)" row in its place.
  The database, the investment write API and the editor already accepted a category as
  the transfer leg's counterparty; the returns flow test counts a `transfer`-role leg
  facing a category as a contribution or withdrawal, not as return.
- **`chk` is kept verbatim on both routes.** The bank route always copied it to
  `check_number`; the investment route dropped it. On a QIF-origin investment row it is
  the QIF action word (`Buy`, `ReinvLg`, ...), the only place the reinvest variants stay
  distinct after the table above collapses them to `dividend_reinvest`. A blank `chk`
  is NULL.

**Existing ledgers.** MD import is a one-shot bootstrap, so migration 260 replays the
first two rules over `provider_raw_payload` (and `recurring_transactions.source_payload`
for reminder templates, with the same investment-shape gate), writing only where the
column still holds what the old importer wrote (`action IS NULL`, `check_number IS NULL`)
and only where the legs still have the shape the payload says. Occurrences already fired
from a template that now holds `transfer` under the rule get it too, with the same leg
checks (two legs on one posting, no security, one on the template's brokerage). The
blank-category rule is for fresh imports only; an existing ledger keeps its "(unnamed)"
category.

Pinned by `TransactionMapperTests` and `ReminderMapperTests` (every QIF word against
every kind of counterparty, plain rows, the multi-split, bank-home and investment-shape
guards), `AccountMapperTests` (the top-level leaf predicate),
`BrokerageCashTransferImportTests` (persisted and read back as `transfer`, including
onto the system Uncategorized), `UncategorizedCategoryImportTests`,
`InvestmentTransactionMapperTests` (the `chk` cases) and
`BrokerageTransferAndOriginalsBackfillTests` (migration 260 restores exactly what the
importer now writes, fired occurrences included, changes nothing on a second run, and
never overwrites a user's value).

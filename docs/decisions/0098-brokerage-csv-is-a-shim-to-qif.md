# 0098 — a brokerage CSV provider is a shim to QIF

* Status: Accepted
* Date: 2026-09-12
* Related: ADR-0031 (ingest-provider pattern; this narrows its Phase 6),
  ADR-0042 (QIF file importer — the path this delegates to),
  ADR-0027 (investment action catalog), ADR-0038
  (`provider_security_mappings` ticker rail), migration 223
  (`accounts.import_provider_key`)

## Context

ADR-0031 Phase 5 shipped a generic delimited-file provider driven by a YAML mapping
document: a column is declared to mean *date*, *payee*, *amount*. That works for a bank
export and cannot work for a brokerage one. A brokerage row's meaning is not in a
column — it is in an English sentence:

```
DIVIDEND RECEIVED FIDELITY GOVERNMENT MONEY MARKET (SPAXX) (Cash)
PURCHASE INTO CORE ACCOUNT as of 2025-06-30 FIDELITY GOVERNMENT MONEY MARKET (SPAXX)
TRANSFER OF ASSETS ACAT DELIVER (Cash)
```

No column mapping expresses "the words before the fund name, together with the sign of
the quantity, decide whether this is a purchase or a dividend". Phase 6 always
anticipated hand-coded per-institution providers for this. The question this ADR
settles is *what such a provider is*.

The obvious reading of Phase 6 — and what ADR-0031's own wording implied — is that each
provider parses its institution's file and emits `IngestedTransaction`s carrying Coffer
action hints, exactly as the generic provider does. That was built first, for Fidelity,
and is not what shipped.

## Decision

**A brokerage CSV provider converts its institution's file to QIF and delegates to
`QifFileProvider`.** It contributes format knowledge and nothing else.

```
Fidelity CSV ──▶ FidelityActivityToQif ──▶ QIF ──▶ QifFileProvider ──▶ transactions
                 (format: columns, junk rows,      (vocabulary: which action,
                  which verb each row is)           which cash model)
```

`FidelityActivityFileProvider` is the `IFileProvider` the orchestrator dispatches to;
it is thin, and its whole job is to run the conversion and hand the result on.

## Why

**The mapping that can corrupt a ledger should exist once.** Deciding which action a row
represents is what silently wrecks cost basis and realized gains. That decision already
lives, documented and tested, in `QifFileProvider.ClassifyInvestmentAction`. A provider
emitting Coffer hints directly is a second copy of it — then a third, one per brokerage,
each drifting independently, each able to corrupt a ledger alone. The shim keeps one
copy and makes every brokerage after the first a question of format.

**The bespoke version was not merely longer — it was wrong, in two places the QIF path
already had right.** This is the substantive argument, not a tidiness one.

* A **cash rollover** became a `transfer` hint. Through QIF it becomes `XIn`, which takes
  the BANK shape: cash arriving in a brokerage account is not a security transaction, and
  the bespoke provider was asserting that it was.
* An **in-kind ACAT** became a transfer too. Through QIF it becomes `ShrsIn`, which
  `QifFileProvider` deliberately maps to plain `buy` rather than `buyx` — because `buyx`
  requires an asset counter-account and dead-ends every row whose real counterpart is an
  expense. That reasoning is recorded in that provider. A bespoke provider overrides such
  decisions without ever learning they were made.

**Provenance survives the conversion.** Every imported row carries both the CSV line it
came from and the QIF record it became, on `txn_headers.provider_raw_payload`. A
converted format otherwise loses the trail exactly when it is wanted — when a number
looks wrong. The user is never shown QIF; they pick a CSV and get transactions.

This costs the ORCHESTRATOR a line, not just the shim: `RunFileAsync` had never written
`provider_raw_payload`, because until the shims no file provider populated it — OFX and
QIF both pass null. A shim that builds the trail and an insert that ignores it look
identical from every test that stops at the provider's return value, which is where the
provider tests and the converter tests both stop. The end-to-end tests read the column
back out of the database for that reason.

**The provider key stays the brokerage's.** `provider_security_mappings` is keyed by
provider, so a symbol learned from a Fidelity file is remembered as Fidelity's rather
than pooled with genuine QIF imports from elsewhere.

## What a brokerage shim owns

The format, and only the format. For Fidelity's "Activity & Orders" export, all of it
learned from real files rather than documentation:

* **Columns by NAME.** A real export orders them `Type,Price ($),Quantity`; the
  widely-cited reference implementation expects `Type,Quantity,Price ($)`. By position,
  a share count becomes a unit price.
* **Finding the table at both ends.** Blank lines precede the header; legal prose
  containing commas and quotes follows the data, parsing as ragged rows. Neither end can
  be skipped by count — the header is found, and data stops at the first row whose width
  differs.
* **Quantity is present and ZERO on cash rows**, so "the column has a value" says
  nothing about whether shares moved.
* **The date can be inside the sentence.** `Run Date` is when the institution processed
  the row; `as of <date>` in the action text is when it happened, and they differ.
* **Rows that mean nothing**: a pending trade carrying `Processing` where a balance
  belongs; a money-fund CUSIP riding along on a zero-amount cash row.
* **The verb rules**, which are a joint function of the action sentence, the type cell,
  and the SIGNS of quantity and amount — in a specific order, because `DIVIDEND` must be
  tested before `REINVEST`. The sign fallbacks are not a safety net: four of the seven
  distinct actions in a real export reach them, and `PURCHASE INTO CORE ACCOUNT` contains
  neither "BUY" nor "BOUGHT".
* **A rule may only read the VERB PHRASE, never the whole sentence.** The action cell
  carries the fund's name, so a substring test reads the security by accident. Testing for
  `TAX`/`FEE` this way classified every purchase and reinvestment of a tax-exempt or
  tax-free fund as a miscellaneous expense — posting the cash with the wrong sign and
  dropping the shares. The rules are guarded by the row's SHAPE (a fee has no shares)
  rather than by more words.

## What a shim must not do: arithmetic

The conversion is **transport**. Anything it rounds is unrecoverable, because no later
stage can see the CSV cell.

Share counts and unit prices are therefore emitted at the decimal's full scale and rounded
once, by the destination column — `ingest_shares` is NUMERIC(28,8), and the
`txn_legs.quantity` a row grows into is NUMERIC(25,12), both widened deliberately by
migration 043 for fractional shares. Formatting them like cash ("0.####") turned a real
0.000980392-unit holding into 0.001, a 2% overstatement carried into cost basis, lots and
realized gain — and erased any holding under 0.00005 units entirely.

This is the general form of the rule already recorded for money: round at the destination's
scale, never before it.

## Consequences

**A new brokerage is a text transform plus tests.** No schema, no ingest semantics, no
action mapping.

**Choosing the brokerage is explicit, not sniffed.** An investment account's import
offers a list of supported brokerages, remembered on `accounts.import_provider_key`
(mig 223) as a side effect of a successful import. Detecting the format from the file
header was considered and rejected: sniffing cannot answer *"what do you support?"*, so a
user whose brokerage is unsupported learns only that their file could not be read —
indistinguishable from a bug. A list says which ones work, which is actionable, and it
does not degrade as headers converge.

**Bank CSV is explicitly NOT in scope.** A bank row is date, payee and a signed amount;
QIF's `!Type:Bank` carries the same fields, so a shim would translate them into
themselves while putting a generated document between the user's file and the error
messages about it. The shim exists to share a vocabulary, and bank rows have none to
share. Two things would change that answer: a bank format needing check numbers or
categories, which QIF's `N` and `L` fields carry and the Phase 5 mapping does not.

**A shim inherits QIF's limits.** It can only say what QIF can express. A brokerage
concept with no QIF verb would have to be added to `ClassifyInvestmentAction` — which is
the right place for it, and is the point.

## Rejected

**Extending the Phase 5 mapping document to an investment shape** (action column,
quantity, price, fee). The action is not in a column; it is derived from free text and
two signs. A declarative mapping cannot express that without becoming a rule engine.

**A bespoke provider per brokerage emitting Coffer hints.** Built first, and rejected for
the reasons under *Why* — one copy of the action mapping per brokerage, and two
already-settled decisions silently overridden.

**Sniffing the brokerage from the file.** Rejected above: it cannot state its own
boundary, and a wrong guess is invisible where a wrong selection is not.

**A cash-balance cross-check**, carried over from the reference implementation, which
trusts the running balance over the institution's stated amount. Implemented, then
disproved by a real export: Fidelity settles a dividend and its sweep into the core fund
together and stamps both rows with the balance after the pair, so the within-pair delta
is always zero. It rewrote every amount in a valid file to `0.00`. Removed rather than
softened to a warning — a check that cannot distinguish a mis-parse from normal
settlement grouping has nothing to warn about.

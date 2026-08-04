# 0042 — QIF file importer

* Status: Accepted
* Date: 2026-06-09
* Related: ADR-0031 (ingest-provider pattern; file providers are
  Phase 4–6), ADR-0027 (investment action catalog), ADR-0038
  (provider_security_mappings ticker rail), the OFX REINVEST $0
  cash contract (PR #166)

## Context

A workplace 401(k) plan — the platform hosting the user's retirement account —
offers only **QIF or CSV** transaction downloads. No OFX/QFX. Until
now QIF reached Coffer only by transit through Moneydance (download
QIF → import to MD → export MD JSON → re-import to Coffer), which is
heavy for routine syncs of an account whose sole egress is QIF.

The file-provider seam (ADR-0031 `IFileProvider`) was built to admit
exactly this: a new format implements one interface and reuses the
orchestrator, the action catalog, the ticker-hint rail, and the SPA
upload wizard.

## Decisions

### D1 — Hand-roll the parser; no NuGet dependency

The .NET QIF package landscape is thin and weak:

| Package | Investment support | Last activity | License | Modern .NET |
|---|---|---|---|---|
| Hazzik.Qif | Yes (verified in source) | release 2022-04, ~4 yr dormant | MIT | netstandard2.0, 0 runtime deps |
| Cinary.Finance.Qif | No — investment DTO is unwired dead code (bank-only) | 2025-02 (active) | MIT | yes |
| QifApi | Yes | abandoned/unlisted 2017 beta, 8 legacy `System.*` deps | MIT | weak |

Only Hazzik.Qif does investment, and it's dormant. More decisively:
a real workplace-plan file has issuer-specific quirks (a parenthetical
fund code as the only security identifier, no account metadata,
fees-paid-in-shares) that need custom handling *on top of* any
library — so a dependency saves almost nothing while adding a
dormant transitive package. QIF is a frozen, line-based format
(single-letter field tags, `^` record terminators, `!Type:` section
headers); the parser is ~200 lines we own and unit-test. **Decision:
hand-roll** (`QifFileProvider`), with Hazzik.Qif (MIT) as a
read-only reference for field mapping. Consistent with the project's
dependency-hygiene posture.

### D2 — Per-provider dedup scope; synthetic external id

QIF carries no FITID. The dedup key is a SHA-1 over the **target
Coffer account id** plus the row's stable fields (date, action,
security, qty, price, amount, memo), prefixed `qif-`. This makes
re-importing the same file into the same account idempotent
(`IngestOrchestrator`'s `(ledger, provider_key='qif', external_id)`
dedup catches it) and prevents two distinct accounts' identical-
shaped rows from colliding within the per-provider scope.

The ledger already holds thousands of MD-transited rows with
`provider_key='qif'` (the MD importer preserves the original source
provider). Their `external_id` is the Moneydance UUID, which the
synthetic key never reproduces — so a direct QIF import does NOT
dedup against MD history. **The user accepted this** ("OK with
deduping being scoped per provider, no worries about MD records").
A direct QIF import that overlaps the MD-covered period would create
parallel rows for that period; the practical flow is to import QIF
forward from where MD coverage ends. Cross-source dedup against
MD-preserved QIF state is explicitly out of scope (a `follow-ups.md`
item if it ever surfaces real pain).

Genuinely identical rows (every hashed field equal) collapse to one
— the standard QIF limitation, since QIF gives no way to
distinguish them. Documented; acceptable given D2.

### D3 — The importer reports the feed; it does not impose a cash model

This is the load-bearing principle (the user's: *"feeds change, cash
model doesn't"*). The importer maps each QIF `N` action code to the
nearest ADR-0027 action and carries the wire amount as the
bank-shape cash-flow hint, signed by the action's convention. It
**does not** interpret memo strings ("Contribution", "Fees",
"Exchange Out") to net cash to zero or synthesize offsetting rows.
The cash model is a stable property of the ADR-0027 action, owned by
the user and adjusted in the editor — e.g. a contribution-funded
`buy` re-pointed to `buyx` (buy-transfer), or an exchange pair
upgraded to `sellx`/`buyx`. Every imported row lands `needs_review`.

`dividend_reinvest` carries **$0** cash — but that is the action's
net-zero contract on every feed (matching the OFX REINVEST
handling), NOT a QIF-imposed interpretation.

### Action map (QIF `N` → ADR-0027)

```
Buy → buy            Sell → sell           ShrsIn  → buy
BuyX → buyx          SellX → sellx         ShrsOut → sell
Div → dividend_cash  DivX → divx
CGLong/CGShort/CGMid → dividend_cash   (…X variants → divx)
IntInc → dividend_cash                 IntIncX → divx
ReinvDiv/ReinvLg/ReinvSh/ReinvMd/ReinvInt → dividend_reinvest ($0 cash)
MiscInc/MiscExp/MiscIncX/MiscExpX/MargInt → misc
StkSplit, ReminderTxn, unrecognised → unsupported (skip + preview warning)
```

Share-movement codes (`ShrsIn` / `ShrsOut`) default to the **plain**
variants (`buy` / `sell`), not the transfer variants. The
X-variants require a transfer counter-account, and the editor's
transfer field only accepts asset accounts (bank / asset /
investment) — never an expense category. So a share-movement whose
real counterpart is an expense (e.g. the workplace plan emits administrative
fees as `ShrsOut`) has no valid transfer destination; defaulting it
to `sellx` dead-ends the row. The plain variant opens cleanly and is
saveable; the user upgrades to `buyx` / `sellx` only for genuine
inter-account transfers. (Modeling a fee-paid-in-shares precisely is
an open question — no single ADR-0027 action both reduces shares and
books an expense category; tracked in follow-ups.) `StkSplit` is
skipped — splits belong on the security-splits surface, not the txn
editor (parity with the OFX SPLIT skip).

### Security identity

The workplace plan names a security as `DISPLAY NAME(CODE)` — e.g.
`BOND FUND(BFND)`. There is no ticker or CUSIP. The parser
lifts the trailing parenthetical (`BFND`) as the
`SecurityTickerHint` for the ADR-0038 mapping rail and uses the name
without the parenthetical as the register Payee. The user maps each
code to a real Coffer security once; every same-code row then
auto-resolves on the next read.

### Single-account-implicit

A workplace-plan QIF has no `!Account` header — the file *is* one
account's transactions. The provider surfaces exactly one
`DiscoveredFileAccount` with a sentinel `ProviderAccountId`
(`"qif"`); the dialog binds it to a target Coffer account. Every
transaction carries the sentinel so the orchestrator's per-provider-
account filter (a no-op for single-account files) passes them all.

## Consequences

### Positive
- Workplace 401(k) plans import natively; no Moneydance round-trip.
- Reuses the entire post-parse pipeline (orchestrator, action
  catalog, ticker rail, holdings recompute, REINVEST $0, SPA
  wizard). The QIF-specific surface is the parser + a thin
  endpoint/dialog mirror.
- Zero new runtime dependencies.

### Negative / accepted
- Parallel `Qif*` DTOs + endpoint mirror the `Ofx*` ones. Tracked
  as a follow-up: unify file-ingest DTOs + an endpoint mapper into
  provider-neutral shapes once the pattern has settled across both
  providers. Not done here to keep the slice self-contained and the
  just-merged OFX path untouched.
- Two import icons (OFX/QFX and QIF) on the investment register top
  bar. A unified "Import file → choose format" affordance is a
  possible future cleanup; two clearly-labelled buttons are honest
  and discoverable for now.
- QIF can't distinguish exact-duplicate rows; they collapse to one.

## Verification

Parser built and verified against a real-world workplace-plan QIF
export (a few hundred records — buys, sells, and share-out fee rows
across several funds; 0 unsupported). Integration tests (`QifIngestTests`, 6 cases) cover
preview (supported count + skip warning), action mapping + ticker-
hint extraction + prefill carriers, the buy-negative / sell-positive
/ reinvest-zero cash convention, re-import idempotency, and the
cross-ledger account-guard 422. Committed fixtures use generic fund
names (no plan-identifying data).

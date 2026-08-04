# 0067 — Rich security classification

* Status: Accepted — shipped v0.7.0 (mig 150; mig 153 made `asset_class =
  'multi_asset'` the single look-through signal, dropping `needs_look_through`).
  Provider auto-classify + manual look-through population deferred (see
  docs/follow-ups.md "Investment-model slice")
* Date: 2026-06-26
* Related: ADR-0063 (`allocation` tool consumes this), ADR-0054 (market-data
  provider — future auto-classify), ADR-0066 (account tax_status — distinct axis)

## Context

`securities.asset_class` is overloaded: it stores the **vehicle** (`mutual_fund`,
`cash_equivalent`) on many rows, conflating two orthogonal things. The single
field can't express true exposure, so allocation/performance analysis can't slice
by what a holding actually is. The dominant remediation is moving a vehicle value
out of `asset_class`, plus filling nulls and correcting the occasional mis-class
(e.g. a commodity fund stored as equity). Producer is the importer (writes the MD
fund type into `asset_class`).

## Decisions

### D1 — Orthogonal dimensions on `securities`
- **`asset_class`** cleaned to economic classes only: `equity · fixed_income ·
  multi_asset · cash · real_assets · alternative` (CHECK).
- **`vehicle_type`** (new): `mutual_fund · etf · stock · money_market · cit ·
  separate_account · plan_529 · option · cd · bond · other`.
- **`region`** (new): `us · developed_ex_us · emerging · global · na`.
- **`tax_character`** (new, nullable): `taxable · tax_managed · tax_exempt` — the
  security's own tax nature (muni exemption, tax-managed funds), distinct from the
  account's tax_status (ADR-0066).
- **`classification_source`** + **`classification_confidence`** (`known |
  assumed`): provenance for auto vs verified.

(A `needs_look_through` bool was added in mig 150 to flag wrappers to decompose,
then **dropped in mig 153** — `asset_class = 'multi_asset'` is the single
look-through signal; see D3.)

### D2 — Style as split, asset-class-specific axes (NOT one overloaded column)
A single `style` column would re-commit the overloading sin (equity style-box vs
fixed-income duration/credit are different vocabularies). Instead, four nullable,
single-vocabulary, CHECK-validatable columns, only the relevant pair populated:
- `equity_size` (`large | mid | small`) + `equity_style` (`value | blend | growth`)
- `fi_duration` (`short | intermediate | long`) + `fi_credit` (`government |
  investment_grade | high_yield`)

This keeps each column one meaning — cleanly filterable ("all small-cap", "all
IG") and validatable, unlike a unioned/freeform style field.

### D3 — Look-through via a `security_components` table
For multi-asset wrappers (target-date / 529 / balanced), a
`security_components` table (`security_id → component asset_class/region →
weight%`) so allocation decomposes them into sleeves rather than counting 100% as
"multi-asset". **`asset_class = 'multi_asset'` is the look-through signal** (a
multi-asset wrapper is exactly the thing that needs decomposing — the original
separate `needs_look_through` flag was redundant and was dropped in mig 153).
Allocation falls back to a single `multi_asset` bucket until components are
entered. The sleeve editor lives in the security edit dialog, shown only when
asset class = multi-asset. Population is manual now (provider-assisted later).

### D4 — Sourcing: manual-first
Classification is set in the security editor; a per-ledger classified catalog
seeds existing rows in the remediation migration. Auto-classify from the
market-data provider (ADR-0054) is a later, opt-in enhancement — not this slice.

### D5 — Producer fix + remediation
The importer stops writing the vehicle into `asset_class`: it maps the MD fund
type → `vehicle_type` + a best-guess `asset_class`. The migration remediates
existing rows (vehicle values → `vehicle_type`, infer `asset_class`).

## Consequences

- True allocation (with look-through) by asset_class × region × style, and
  performance attribution by those dimensions — the `allocation` tool + Overview
  stop bucketing by the vehicle-polluted field.
- Sparse but clean style columns (4 nullable, one vocabulary each).
- A securities-dedup pass is a separate follow-up (the catalog also surfaced
  duplicate definitions).

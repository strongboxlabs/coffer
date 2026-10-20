# 0066 — Account tax status

* Status: Accepted — shipped v0.7.0 (mig 149)
* Date: 2026-06-26
* Related: ADR-0063/0064 (MCP reporting + FIFO surfaced the need), ADR-0002/0017
  (account model)

## Context

`account_type` ('investment', 'bank', …) can't distinguish a **taxable
brokerage** from a **401k/IRA** — both are `investment`. So realized gains,
dividends, and tax reporting can't be treated correctly (a 401k's realized gains
aren't 1099-B-reportable). The tax treatment of an account is orthogonal to its
type.

## Decision

Add **`accounts.tax_status`** — a nullable enum, `taxable | tax_deferred |
tax_free | other` (null = unknown). Orthogonal to `account_type`.

- Set in the account editor; the MD importer seeds a best-guess from the source
  account type, then Coffer owns it (ADR-0050 import-once).
- Consumers: MCP `list_accounts` + `net_worth` expose it; `realized_gains`
  distinguishes taxable (1099-B-relevant) from tax-deferred; dividend/interest
  tax treatment later.
- Composes with the security-level `tax_character` (ADR-0067): *where* a security
  is held vs the security's *own* tax nature (e.g. a muni fund's tax-exemption is
  wasted in a tax-deferred account).

## Consequences

A small, additive attribute (column + editor + importer seed + reporting use); no
engine change. Enables tax-aware reporting that `account_type` alone can't.

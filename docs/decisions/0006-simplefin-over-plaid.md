# 0006 — SimpleFIN as bank feed; Plaid rejected for personal use

* Status: Accepted
* Date: 2026-05-08

## Context

The app needs an automated bank-feed source covering checking, credit cards, and brokerage accounts (including a major brokerage). The realistic options for a US-based single user in 2026 are SimpleFIN Bridge, Plaid, and "no automated feed" (manual / OFX / CSV).

The brokerage is the constraint that drives most of this. The brokerage ended OFX/direct-connect in 2024 and migrated to OAuth via Plaid/MX in February 2025. SimpleFIN sources its data from MX, so SimpleFIN inherits proper brokerage OAuth support without requiring Coffer to integrate Plaid directly.

## Decision

Use **SimpleFIN Bridge** as the bank feed.

- $15/year, supporting up to 25 institutions and 25 apps.
- Daily polling cadence — adequate for personal finance.
- OAuth tokens persist until expiry; hardware-key MFA (YubiKey) requires periodic re-auth, surfaced in UI as a `feed_connections.status = 'needs_reauth'`.
- Returns balances, transactions, and holdings (security positions) for investment accounts.

### Protocol version + defensive contract

Pinned to the SimpleFIN protocol **v2.0.0** (released 2026-03-19). Every `/accounts` request from `SimpleFinClient` carries `?version=2`, so SimpleFIN returns the v2 top-level shape — three sibling arrays at the root: `connections[]`, `errlist[]`, `accounts[]` — instead of the pre-v2 nested form (`account.org` is gone).

Two defensive contracts back the wire-level posture, per the project rule that every external API surface must distinguish failure modes rather than collapsing everything into a single exception:

- **HTTP 403** on `/accounts` is *not* an exception. The access URL is revoked or expired; `SimpleFinSyncService` flips `feed_connections.status='needs_reauth'`, stamps `last_synced_at`, and returns a typed `SyncResultDto` so the SPA renders a Re-connect call-to-action instead of a generic error toast. Non-403 non-2xx still throws `SimpleFinException` and surfaces as a 422.
- **`errlist[]`** is parsed verbatim and surfaced through `SyncResultDto.errors[]`. Partial failures (e.g. *"Bank A in maintenance"* alongside healthy account data on Bank B) are visible to the user, not silently dropped.

Plaid is **not** used directly. The free Trial tier (post April 2026) supports up to 10 Items and is fine for early development experiments, but Plaid's production pricing — per-account monthly fees with $1k–$3k/month minimum commitments — is built for businesses with many users and is uneconomical for a single-user app.

## Consequences

**Positive**
- Cost is fixed and trivial.
- The brokerage works through proper OAuth without Coffer touching Plaid integration code.
- One feed integration covers the full account portfolio.
- The MX upstream is a real fintech aggregator with mature institution coverage.

**Negative**
- Daily polling, not real-time. Acceptable for personal finance.
- Dependency on a third-party service. Mitigated by storing all transactions locally in our Postgres; if SimpleFIN goes away we keep the data and fall back to OFX/CSV imports per institution.
- Re-auth interruptions for OAuth-token-expiry institutions (the brokerage especially). Surfaced clearly in UI per [architecture.md](../architecture.md) §2.4.

## Alternatives considered

- **Plaid direct integration.** Pricing model is wrong for a single-user app. The free tier is enough for development but cannot be relied on long-term. Rejected.
- **Per-institution OFX where supported, CSV fallback elsewhere.** Maximum independence but maximum maintenance burden, and the brokerage alone breaks the model. Kept as a *fallback* for the brokerage's CSV uploads when re-auth lapses, not as the primary path.
- **Hand-build an MX integration.** MX requires business onboarding and pricing comparable to Plaid. Rejected.
- **Yodlee / other aggregators.** Same business-pricing problem. Rejected.

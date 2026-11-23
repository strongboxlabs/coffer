# 0006 — SimpleFIN as bank feed; Plaid rejected for personal use

* Status: Accepted (amended 2026-11-06: a connection is named after its institution only when it reaches one, and its owner can rename it)
* Date: 2026-05-08

## Context

The app needs an automated bank-feed source covering checking, credit cards, and brokerage accounts. The realistic options for a US-based single user in 2026 are SimpleFIN Bridge, Plaid, and "no automated feed" (manual / OFX / CSV).

Brokerages are the constraint that drives most of this. Some have ended OFX/direct-connect and moved to OAuth through aggregators (Plaid/MX). SimpleFIN sources its data from MX, so SimpleFIN inherits OAuth brokerage support without requiring Coffer to integrate Plaid directly.

## Decision

Use **SimpleFIN Bridge** as the bank feed.

- A low flat annual subscription, supporting up to 25 institutions and 25 apps.
- Daily polling cadence — adequate for personal finance.
- OAuth tokens persist until expiry; hardware-key MFA (YubiKey) requires periodic re-auth, surfaced in UI as a `feed_connections.status = 'needs_reauth'`.
- Returns balances, transactions, and holdings (security positions) for investment accounts.

### Protocol version + defensive contract

Pinned to the SimpleFIN protocol **v2.0.0** (released 2026-03-19). Every `/accounts` request from `SimpleFinClient` carries `?version=2`, so SimpleFIN returns the v2 top-level shape — three sibling arrays at the root: `connections[]`, `errlist[]`, `accounts[]` — instead of the pre-v2 nested form (`account.org` is gone).

Two defensive contracts back the wire-level posture, per the project rule that every external API surface must distinguish failure modes rather than collapsing everything into a single exception:

- **HTTP 403** on `/accounts` is *not* an exception. The access URL is revoked or expired; `SimpleFinSyncService` flips `feed_connections.status='needs_reauth'`, stamps `last_synced_at`, and returns a typed `SyncResultDto` so the SPA renders a Re-connect call-to-action instead of a generic error toast. Non-403 non-2xx still throws `SimpleFinException` and surfaces as a 422.
- **`errlist[]`** is parsed verbatim and surfaced through `SyncResultDto.errors[]`. Partial failures (e.g. *"Bank A in maintenance"* alongside healthy account data on Bank B) are visible to the user, not silently dropped.

Plaid is **not** used directly. The free Trial tier (post April 2026) supports up to 10 Items and is fine for early development experiments, but Plaid's production pricing — per-account monthly fees on top of monthly minimum commitments sized for a business — is built for businesses with many users and is uneconomical for a single-user app.

## Consequences

**Positive**
- Cost is fixed and trivial.
- OAuth-only brokerages work without Coffer touching Plaid integration code.
- One feed integration covers the full account portfolio.
- The MX upstream is a real fintech aggregator with mature institution coverage.

**Negative**
- Daily polling, not real-time. Acceptable for personal finance.
- Dependency on a third-party service. Mitigated by storing all transactions locally in our Postgres; if SimpleFIN goes away we keep the data and fall back to OFX/CSV imports per institution.
- Re-auth interruptions for OAuth-token-expiry institutions (OAuth brokerages especially). Surfaced clearly in UI per [architecture.md](../architecture.md) §2.4.

## Alternatives considered

- **Plaid direct integration.** Pricing model is wrong for a single-user app. The free tier is enough for development but cannot be relied on long-term. Rejected.
- **Per-institution OFX where supported, CSV fallback elsewhere.** Maximum independence but maximum maintenance burden, and an OFX-less brokerage alone breaks the model. Kept as a *fallback* (e.g. brokerage CSV uploads when re-auth lapses), not as the primary path.
- **Hand-build an MX integration.** MX requires business onboarding and pricing comparable to Plaid. Rejected.
- **Yodlee / other aggregators.** Same business-pricing problem. Rejected.

## Amendment, 2026-11-06 — a connection's name: renamed by its owner, and named after one institution only

A connection's name is `feed_connections.institution_name`, the heading of its row in
Settings → Bank feeds. It was only ever set by the connect step, from the first
`connections[]` entry of the probe. One SimpleFIN connection can reach accounts at several
institutions, each its own `connections[]` entry, so a connection could read as one
institution's when it held others'. Two decisions follow.

**The starting name.** `SimpleFinClient.GetInstitutionNameAsync` returns a name only when
the connection list holds exactly one distinct non-blank name, compared trimmed and
ordinal; an entry with a blank or missing name counts for nothing. Several names, or none,
give null, which the SPA renders as "SimpleFIN". A connection that already exists keeps its
stored name: nothing rewrites the column. Nothing fills a null name in later either: a sync
never writes the column.

**Renaming.** `PATCH /api/ledgers/{ledgerId}/feed-connections/{connectionId}` with
`{ name }` writes the trimmed name and answers the connection's summary. The name is 1 to
80 characters once trimmed, as a sidebar folder's is; otherwise 422
`feed-connection-name-required` or `feed-connection-name-too-long`. Two connections may share a
name. The rename is the owner's, as Disconnect is (ADR-0083's 2026-10-19 amendment):
the route carries `AsLedgerOwner()`, and the pencil that opens it is drawn only beside a
Disconnect. RLS cannot hold it to owners, because editors update the same row when a sync
records its status, so like the account mapping it is enforced at the API only. It takes no
ledger write lock (ADR-0112 D3): it is one statement that recomputes nothing and holds only
the connection's row, and once it holds that row it waits for nothing.

In the SPA the pencil sits right after the name. It opens the shared inline name field,
text selected; Enter or a blur saves, Escape or a blank name cancels, and after Enter or
Escape focus goes back to the pencil (ADR-0023 §L). The new name shows at once while the
pencil is pending; a refusal puts the old name back and is said under the row, where a
sync's failure is. There is no success notice. The save writes the
`['feed-connections', ledgerId]` list, which the row, the Disconnect dialog, the sidebar and
the Overview's Feeds & checks all read, and then reads it again.

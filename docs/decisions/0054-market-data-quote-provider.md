# 0054 — Market-data quote provider + scheduled price refresh

* Status: Accepted
* Date: 2026-06-18
* Related: ADR-0033 (quote-provider family — this extends it), ADR-0031
  (ingest provider pattern), ADR-0020 (multi-ledger row-scoped), ADR-0026
  (per-ledger encryption key)

## Context

ADR-0033 built the quote-provider family and it is **implemented**:
`IQuotePullProvider` / `IQuotePushProvider`, the `QuoteOrchestrator`
(per-ledger; upserts `security_prices` via EF; surfaces unresolved
securities), the ledger-bound `POST /api/ledgers/{id}/quotes/refresh`
endpoint, and a `refreshQuotes` web client. The only registered provider is
`SimpleFinHoldingsQuoteProvider`, which extracts prices from stored SimpleFIN
sync payloads (no external HTTP).

The gap: securities **not** sourced from a SimpleFIN sync — manually entered
or MD-imported (brokerage funds, IRAs, retirement/college accounts) — never
get refreshed; their `security_prices` stay at the one-time import snapshot.
ADR-0033 explicitly deferred (a) a real market-data HTTP provider, (b) the
scheduled refresh worker, (c) per-ledger config, (d) a no-resolvable-symbol
override.

This ADR adds those — designed for a general release (any asset mix, any
scale, external dependency optional), not one user's dataset.

## Decisions

### D1 — A market-data pull provider (new `IQuotePullProvider`)

A new provider does HTTP **EOD-close** fetch by symbol and returns
`QuoteResult`. Registered via DI, so the explicit `/refresh` endpoint
(`RunAllPullsAsync`) and the scheduled worker pick it up with no
orchestrator/endpoint change (ADR-0033 §1/§6). **Exception — the post-sync
pull:** a successful SimpleFIN sync refreshes prices in the same user action,
but it runs **only** the `simplefin-holdings` provider (`RunPullAsync`, not
`RunAllPullsAsync`) — that provider just reads the payload the sync already
fetched (no egress). Fanning a bank sync out to an external market-data
provider would turn every sync into outbound HTTP; external providers belong on
the explicit refresh and the scheduled worker, where the user opted into the
fetch. Provider-agnostic: ship one **free, no-API-key EOD reference adapter**
so the feature works out-of-box; keyed/paid providers plug in identically.
Prices stored **unadjusted** (splits live in `security_splits` / lots).

> **Superseded (ADR-0057):** the original opt-in was a deployment config flag
> (`Quotes:Yahoo:Enabled`). It is now a **per-ledger user preference** (the
> `quotes` namespace): Yahoo is always registered, and the orchestrator runs it
> only when the acting ledger's pref enables it. The env flag is removed.

**v1 reference adapter: Yahoo Finance** (the unofficial
`query*.finance.yahoo.com` chart endpoint) — chosen for zero-config (no API
key) and the broadest free coverage, including the mutual funds /
money-market funds that dominate real books. Recorded caveat: it is an
**unofficial, best-effort** source (Yahoo's ToS gray area; periodic breakage;
may need cookie/crumb handling). Acceptable here because the family is
provider-agnostic and the orchestrator already degrades gracefully —
unresolved securities surface, manual entry remains. For a robust shipped
story, a **keyed official adapter (e.g. Tiingo or Twelve Data — clean ToS,
free tier, EOD fund coverage) is planned as a sibling provider**; a deployment
chooses which provider(s) to register. Fund coverage — not book size — is the
real variable, validated against a real unresolved set.

### D2 — Coverage: `quote_symbol` + `auto_price`, source-aware upsert

ADR-0033 queries by `securities.ticker` and treats a null ticker as
manual-only. Generalise:

- **`quote_symbol`** (nullable, defaults to `ticker`) — the symbol actually
  sent to the provider, so a security whose provider symbol differs from its
  display ticker (mutual funds, international suffixes) can be corrected.
- **`auto_price`** (bool, default true) — exclude a security from auto-pricing
  (manual-only, or a stable-NAV money-market fund pinned by hand) without
  nulling its ticker.
- **`quote_symbol_public`** (bool, default true; mig 156) — a `quote_symbol` may
  be a private / feed-internal identifier (e.g. a 529 plan's portfolio number)
  rather than a public ticker. When **false**, the symbol is matched only by the
  no-egress SimpleFIN-holdings provider and is **never** sent to an external
  (egress) provider like Yahoo, which would 404 or mis-resolve the bare number
  and overwrite the feed price. A DB CHECK (`quote_symbol_public OR quote_symbol
  IS NOT NULL`) codifies the model — **a bare ticker is always public**; you can
  mark something non-public only when there is a `quote_symbol` to keep private.
  The orchestrator filters each egress provider's working set to public symbols;
  the API (Create/Patch) mirrors the CHECK for a clean 422. Auto-pricing is gated
  on having *some* symbol (ticker **or** quote_symbol), so a private-symbol 529
  is still feed-priced.
- A **`source`** tag on `security_prices` (import / fetch / manual) and the
  upsert **must not overwrite a manual/import price** for a date with a
  fetched one. (Today's upsert overwrites price unconditionally — a
  correctness gap for hand-entered prices.)

Unresolved securities keep surfacing via `QuoteRunOutcome.SecuritiesUnresolved`.

### D3 — On-demand refresh UI

Wire the existing `refreshQuotes(ledgerId)` client to a "Refresh prices"
action on the holdings/portfolio surface (endpoint + client already exist;
only the button is missing), with the outcome (updated / unresolved) shown in
a toast.

### D4 — Per-ledger scheduled auto-run (shipped)

A background worker refreshes prices on a schedule, **per ledger**: each ledger
carries its own on/off + daily time-of-day, and the worker runs scoped to that
ledger. Two ledgers holding the same security fetch independently (no
cross-ledger dedupe) — accepted for isolation.

As built — on the **generic scheduler** (mig 136 `scheduled_jobs`, see
database-schema.md; `quote-refresh` is one `job_type`):

- **`scheduled_jobs`** (mig 136/137), one row per `(ledger, job_type)`:
  `enabled`, `hour_local` / `minute_local` interpreted in the schedule's
  `timezone` (the user's IANA browser tz, mig 137; server-local fallback),
  `configured_by_user_id`, `last_run_at` / `next_run_at`. (Generalizes the
  original per-feature `quote_schedules`.)
- **`SchedulerService`** — one in-process `BackgroundService` ticking every
  15 min over the **service-role (BYPASSRLS)** context (a background tick has no
  request user, so the RLS app role is fail-closed). It dispatches each due row
  by `job_type` to a handler; **`QuoteRefreshJobHandler`** runs
  `RunAllPullsAsync(ledgerId, "scheduled", configuredByUserId)`. Single instance
  assumed; multi-instance would add a per-ledger advisory lock.
- **Provider resolution:** the run uses the **configuring user's** `quotes`
  opt-in (ADR-0057), *not* a system-user pref. A system-user pref is precluded
  by the own-user RLS on `user_preferences` (a normal user couldn't set it
  without a service-role carve-out), so the schedule records who turned it on
  and uses their providers. The run is recorded `triggered_via='scheduled'`
  (this refines ADR-0055's original "scheduled → system user").
- **Config:** the Settings → Market data tab gains an "auto-refresh daily at
  HH:MM" control (default 19:00).

### D5 — Config

The no-key reference provider needs no per-ledger config. A keyed provider's
credential lives in config/secrets (env) per ADR-0033 §4; per-ledger provider
config + a Connections UI are revisited when a keyed provider lands. The
per-ledger **schedule** setting (D4) is the first piece of per-ledger quote
config.

## Consequences

- Non-SimpleFIN securities get current prices → accurate per-position market
  value, price history, cost-vs-market, and (later) the dashboard
  portfolio-value widget + Portfolio analytics time-series.
- External egress (symbols → provider) becomes possible; opt-in (the feature
  does nothing if no market-data provider is registered/configured) and
  documented.
- Securities with no resolvable symbol stay manual by design.

## Out of scope

- The background-scheduler infrastructure choice (D4) — its own slice/ADR if
  it warrants one.
- A Connections / API-key management UI — with the first keyed provider.
- A `quote_runs` audit table — per ADR-0033 §3, add when failures warrant.

## Slices (each = one PR, preflight-green)

Slice A is delivered as two reviewable PRs (A1 then A2):

A1. **Provider + source-aware persistence + refresh button** — the new pull
   provider (D1), the `source` tag + source-aware upsert at all three price
   writers (D2 persistence half), and the "Refresh prices" button (D3). Ships:
   non-SimpleFIN securities refresh on demand, ledger-bound; a fetch never
   clobbers a manual/imported price.
A2. **Coverage override** — `quote_symbol` + `auto_price` (D2 coverage half) +
   the orchestrator's symbol selection + the security-editor fields.
B. **Scheduled per-ledger auto-run (D4)** — the worker + per-ledger schedule
   setting, once scheduler infrastructure is chosen.

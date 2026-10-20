# 0033 — Quote providers; per-family typed interfaces (no shared base)

* Status: Accepted
* Date: 2026-05-29
* Related: ADR-0031 (ingest provider pattern), ADR-0032 (triggers as last resort)

## Context

Portfolio View ([HoldingsRepository.GetByBrokerageAsync](../../src/Api/Db/Repositories/HoldingsRepository.cs))
reads `security_prices` to compute per-position market value,
unrealized gain, and as-of date. Currently the only writers are:

* The MD importer (`csnap` snapshots at one-time import).
* The manual single-price endpoint (`SecuritiesRepository.AddPriceAsync`).

For SimpleFIN-synced brokerages, every sync brings fresh
`holdings[].market_value` + `shares` on every position — but
nothing extracts that into `security_prices`, so the Portfolio
View shows stale unrealized values relative to the MD export
date until the user manually adds prices.

The fix is a **quote-provider family** — a parallel to ADR-0031's
ingest providers but for price data instead of transactions.

### Design tension surfaced during the design discussion

A shared "Layer 1" provider base (common `IProvider` interface,
unified `provider_connections` + `provider_runs` tables, capability
flags) was considered and **rejected as overengineering**:

* **Two families isn't enough to know the shape.** Ingest and
  quote are the only two we know of. Rule of three.
* **The "shared" infrastructure doesn't actually unify.**
  * `provider_connections` would store SimpleFIN's encrypted
    access URL alongside Yahoo's API key alongside a file
    upload's stateless absence-of-config. JSONB blob with
    family-specific interpretation. That's anti-abstraction.
  * `provider_runs` would have to merge ingest's typed columns
    (`txns_fetched`, `txns_promoted`) with quote's (`prices_added`,
    `securities_unresolved`). Same anti-pattern.
  * `IProvider` with `FamilyKey` + `Capabilities` flags is two
    enum-ish fields — marker interfaces give the same info at
    the type level with zero runtime checks.
* **Push vs pull duality** is real and important, but lives in
  the per-family interface count (`*PullProvider` +
  `*PushProvider`), not in a shared base or capability flag.

## Decision

### 1. Per-family typed interfaces; no shared base

For each provider family (currently ingest; this ADR adds quote),
declare typed pull and/or push interfaces:

```csharp
public interface IQuotePullProvider
{
    string ProviderKey { get; }
    Task<QuoteResult> PullAsync(QuotePullContext ctx, CancellationToken ct);
}

public interface IQuotePushProvider
{
    string ProviderKey { get; }
    Task<QuoteResult> PushAsync(QuotePushPayload payload, CancellationToken ct);
}
```

A provider implements **one or both** based on its actual
capability:

* `SimpleFinHoldingsQuoteProvider` — pull only (reads stored
  raw payload from the SimpleFIN sync orchestrator's writes).
* Future `YahooFinanceQuoteProvider` — pull only (HTTP fetch).
* Future file-upload / paste-CSV provider — push only.
* Future webhook-driven provider — push only (deferred, see §3).

Push/pull is a compile-time guarantee via interface choice — a
push-only provider can't be invoked through the pull path
because it doesn't implement `IQuotePullProvider`.

### 2. Per-family orchestrator

Each family gets its own orchestrator. Same shape; different
typed input/output/persistence:

```csharp
public sealed class QuoteOrchestrator
{
    public Task<QuoteRunOutcome> RunPullAsync(Guid ledgerId,
        string providerKey, CancellationToken ct);
    public Task<QuoteRunOutcome> RunPushAsync(Guid ledgerId,
        string providerKey, QuotePushPayload payload, CancellationToken ct);
    public Task<QuoteRunOutcome> RunAllPullsAsync(Guid ledgerId,
        CancellationToken ct);
}
```

Persistence (writes to `security_prices`) lives in the
orchestrator, not in providers — same boundary as ADR-0031 §1.
Providers translate; orchestrator writes the DB.

### 3. No shared "provider runs" audit table

`IngestOrchestrator` uses `sync_runs` (typed for ingest).
`QuoteOrchestrator` does **not** get a parallel `quote_runs`
table in v1. Audit needs surface only if real-world failure
modes warrant it — start simple, add later.

(Ingest's `sync_runs` exists because partial-failure surfaces
matter for bank syncs. Quote refreshes are simpler — if Yahoo
returns 429 we skip those tickers, not an "audit-worthy" event
in v1.)

### 4. No shared connection-config storage

`feed_connections` stays as-is for SimpleFIN (encrypted access
URL ciphertext). Quote-family providers in v1 need no
per-ledger config:

* `SimpleFinHoldingsQuoteProvider` reads existing
  `feed_connection_accounts.last_provider_raw_payload` — no
  separate quote-side config.
* `YahooFinanceQuoteProvider` (future) — API key from env var,
  not per-ledger.
* Manual / file push — payload IS the config; nothing persisted.

When a user-facing "Connections" management UI lands (probably
with the Yahoo provider), revisit. Until then, env-var config is
fine.

### 5. Explicit per-family orchestrator coupling, not hooks

When SimpleFIN ingest sync completes successfully, the
`IngestOrchestrator` explicitly invokes
`QuoteOrchestrator.RunAllPullsAsync` for the same ledger. Two
lines of code:

```csharp
// inside IngestOrchestrator.RunPullAsync, near end of success path:
await _quoteOrchestrator.RunAllPullsAsync(ledgerId, cancellationToken);
```

No event-bus, no plugin-registration "post-sync hook"
abstraction. If a future family wants the same coupling, add
two lines there too.

### 6. Push/pull capability advertised by interface, not flag

A provider that supports both push and pull implements both
interfaces:

```csharp
public sealed class FutureProvider : IQuotePullProvider, IQuotePushProvider
{
    public string ProviderKey => "future";
    public Task<QuoteResult> PullAsync(...) { ... }
    public Task<QuoteResult> PushAsync(...) { ... }
}
```

The orchestrator's DI registration registers it under both
sets:

```csharp
services.AddSingleton<IQuotePullProvider, FutureProvider>();
services.AddSingleton<IQuotePushProvider>(sp => sp.GetRequiredService<FutureProvider>());
```

(Or wire via `TryAddEnumerable` — implementation detail.)

## Consequences

### Better

* **Code matches the design.** Push-only / pull-only providers
  declare their shape via interface choice. Compile-time gate.
* **No premature abstraction.** Each family ships in its own
  shape; we extract a shared base only when a third family
  proves repeated patterns.
* **Ingest is untouched.** This ADR doesn't churn ADR-0031's
  surface. The `IPullProvider` / `IFileProvider` interfaces stay
  as-is.

### Worse

* **Parallel structure duplication.** When the third family
  lands we'll likely see "same shape, different types" code in
  3+ orchestrators. Extraction cost compounds the longer we wait.
* **No unified provider-config surface.** When the "Connections"
  management UI ships, it'll either bridge two separate tables
  (`feed_connections` + future quote-side table) or refactor
  toward a unified store. Both have cost.

### Migration plan when (if) extraction makes sense

When a third family arrives, look at:

* Do all three orchestrators share the same lifecycle pattern
  (load config → call provider → persist result → log audit)? If
  yes, candidate for a shared base.
* Do all three need user-facing connection config? If yes,
  candidate for a unified `provider_connections` table.
* Do all three benefit from a unified audit log? Probably not —
  each family's audit needs are usually domain-specific.

The trigger for extraction is *evidence from the third family*,
not speculation now.

## Out of scope

* User-facing "Connections" / "Integrations" management page —
  lands with the Yahoo provider slice (configures API keys).
* Yahoo Finance provider — separate slice; this ADR establishes
  the family-level shape only.
* Background scheduled refresh worker — deferred until SimpleFIN-
  sync piggyback proves insufficient.
* Webhook-driven push providers (third-party callbacks) —
  deferred; same Push interface applies when needed, plus
  webhook auth + idempotency-key surface.
* Audit log (`quote_runs` table) — add when partial-failure
  surfaces matter in real data.

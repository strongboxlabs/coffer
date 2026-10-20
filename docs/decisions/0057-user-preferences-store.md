# 0057 — User preferences store

* Status: Accepted
* Date: 2026-06-22
* Related: ADR-0056 (overview dashboard — first consumer), ADR-0020
  (multi-ledger / RLS), ADR-0013 (auth / users), ADR-0055 (typed-record ↔ jsonb
  precedent), ADR-0050 (accounts management)

## Context

We need somewhere to persist per-user UI preferences. The first concrete need is
the overview dashboard's pick-and-choose widget config (ADR-0056 slice 2), but
preferences are a **cross-cutting** concern — appearance, register defaults, and
the section-collapse state currently stranded in `localStorage` are all the same
shape of problem. Building a `user_dashboard_prefs` table (and later a
`user_appearance_prefs`, …) would be a per-feature sprawl.

Today there is no general mechanism: only `localStorage` (Hub section collapse,
device-local, no cross-device sync) and the server-side
`users.last_opened_ledger_id` (a single typed column).

**Directive:** every preference is **per-ledger** — the user opens different
ledgers and wants each to remember its own settings. There is no global
preference scope in this store; the only truly-global state
(`last_opened_ledger_id` — which ledger to open) already lives on `users` and
stays there.

## Decisions

### D1 — One general per-(user, ledger) store

A single table, not a table per feature:

| column | |
| --- | --- |
| `user_id` | FK → `users` (the row is personal) |
| `ledger_id` | FK → `ledgers`, **NOT NULL** (every preference is per-ledger) |
| `namespace` | text — `dashboard`, `appearance`, `register`, … |
| `value` | `jsonb` |
| `updated_at` | timestamptz |

Primary key `(user_id, ledger_id, namespace)`. RLS scopes rows to the owning
user **and** a visible ledger (ADR-0020). Composite FK `(ledger_id)` consistent
with the rest of the schema; both FKs configured on the EF entity up front
(`HasOne`/`WithMany`/`OnDelete`).

New preference areas are new `namespace`s — **no schema change**.

### D2 — Namespaced, typed jsonb (not loose JSON)

Each `namespace` maps to a typed C# record (e.g. `DashboardPrefs`,
`AppearancePrefs`) serialized to `value` via `System.Text.Json` — the exact
pattern ADR-0055 uses for `provider_runs.details` (typed record ↔ jsonb, snake
case). Per-namespace validation lives in the API. `namespace` is free text at
the DB; the **API owns the known set** (an unknown namespace → 400), so adding a
preference is a new typed record + validator, never a migration or a DB CHECK.

### D3 — Ledger-scoped, user-bound endpoints

`GET /api/ledgers/{ledgerId}/preferences/{namespace}` and `PUT …`. The row
written is for `(current user, ledgerId, namespace)`. Consistent with every
other per-ledger route + the existing user-bound `…/me/last-opened` pattern.

`GET` always returns a **fully-populated value** — server defaults when no row
exists — so the SPA never has to guess defaults or special-case "never set."
Repository is LINQ/EF over the entity (no raw SQL in the API).

### D4 — First consumer: the `quotes` namespace (Yahoo opt-in)

The store ships **with** its first consumer, built against a real need rather
than speculative knobs. That consumer is the **per-ledger market-data opt-in**,
namespace `quotes`:

```jsonc
// value shape — quotes namespace
{ "enabledProviders": ["yahoo"] }   // external quote providers this ledger opted into
```

Default `[]` (**opt-in** — no outbound market-data egress until the user enables
it per ledger). This **supersedes the ADR-0054 `Quotes:Yahoo:Enabled` config
gate**: the env flag is removed, Yahoo is always registered, and the
`QuoteOrchestrator` runs an opt-in provider (`IQuotePullProvider.RequiresOptIn`)
only when the acting ledger pref lists its key. The no-egress
`simplefin-holdings` provider is not opt-in and always runs.

**Whose pref gates a run?** The run's `triggered_by_user_id` (ADR-0055): a
**manual** refresh reads the acting user's `quotes` pref; a **scheduled** run
(ADR-0054 B) reads the **schedule's configuring user's** pref (the
`quote_schedules.configured_by_user_id`). A system-user pref was the original
intent, but the own-user RLS on this table means a normal user can't set the
system user's row without a service-role carve-out — so the schedule remembers
who turned it on and uses their providers, recorded `triggered_via='scheduled'`.

The dashboard pick-and-choose widget config (ADR-0056 slice 2) is a later
consumer (`dashboard` namespace), built on this same store.

Candidate future consumers (noted, **not built** — no invented UI): `appearance`
(theme / density / number format), `register` defaults (columns, page size), and
migrating the `localStorage` Hub-section-collapse into a `dashboard`-namespace
value so it syncs across devices.

## TBD (not yet agreed)

- The concrete shape of every namespace beyond `quotes` (defined when each is
  built).
- Whether `appearance` genuinely wants to be per-ledger (the directive) or later
  warrants a global exception — revisit if/when it's built; not in scope now.

## Consequences

- Extensible without migrations: a new preference is a typed record + a
  validator + a default.
- Preferences are personal (per user) and remembered per ledger, matching the
  directive.
- The `localStorage` collapse state can later migrate here for cross-device
  sync, retiring the device-local fallback.
- One round trip per namespace; the always-populated GET keeps default logic on
  the server (single source of truth).

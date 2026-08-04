# 0047 — Reminders / recurring transactions / calendar

* Status: Accepted
* Date: 2026-06-12
* Related: ADR-0010 (recurring_transactions Phase-1 schema, "schema-
  ready, UI-deferred"), ADR-0027 (investment action catalog), ADR-0019
  (symmetric postings), ADR-0003 (header overrides), ADR-0023 (modern-web
  UX), ADR-0030 (register surface / `resolved_transactions`), ADR-0032 /
  0034 (recompute as interceptors, not triggers), ADR-0036 (originating
  vs target-split / posting counts). Migrations of note: 002 (table), 013
  (external_id), 072 (ledger_id), 103 (balance walk excludes hidden), 112
  (snapshot scope).

## Context

ADR-0010 shipped the `recurring_transactions` table and the Moneydance
importer round-trips reminders into it idempotently (`external_id`,
mig 013), snapshot-included (mig 112) — but deliberately deferred the UI.
Verified state today: **no EF entity, no DTO, no repository, no
endpoints, no UI, and no firing engine.** The table is effectively
write-only from the importer. `next_due_date` is null in every import.

Separately, the register's **`scheduled`** state is a *derived* display
status (`posted_at > now`), never persisted onto `txn_headers.status`
(the mig-030 recon 3-state), and today **unlinked** to any series — the
filter catches any future-dated header, never a recurring template.

This ADR designs reminders/recurring transactions and the calendar
surface on top of that foundation, settling the data model and the
first-slice scope. All decisions below were agreed with the user.

## Decisions

### D1 — A reminder's transaction is a real `txn_header` + `txn_legs` (a template)

The reminder's transaction shape is **persisted in the same structures as
live transactions** — a `txn_headers` row and its `txn_legs` — flagged
`is_recurring_template = true`. Consequences, all intended:

- **Splits are free** — the template's legs *are* the splits. No splits
  child table; multi-leg reminders model faithfully (this retires the
  ADR-0010 single-target flattening as the working model).
- **Investment-shape reminders work** — same security/quantity leg model.
- **The editor is the same editor** — create/edit a reminder reuses
  `TxnRowCreate` / `TxnRowEdit`; we are editing a header + legs.

`txn_headers` already carries non-live rows that readers filter
(`is_hidden`, `is_merged_into`, `is_pending`); `is_recurring_template` is
the same kind of structural discriminator.

### D2 — View-layer separation: live vs template (the exclusion is structural)

The discriminator alone is not enough — **every** reader of "live"
transactions must exclude templates, and that is the balance/holdings
surface just hardened (#196–#198). Rather than scatter
`AND NOT is_recurring_template` across N readers, the exclusion lives in
the **view layer**:

- **`resolved_transactions`** (ADR-0030) gains the template exclusion →
  it is the **live** surface. The SPA register, reports, and bulk
  selection read it and never see templates by construction.
- **A new `recurring_reminders` view** joins template headers + their
  legs to the `recurring_transactions` metadata → the **reminders** read
  surface.
- **The recompute functions read live data only.** This is the one hard
  invariant: a template's legs must NEVER reach
  `txn_header_account_balances` or `holdings`. `fn_recompute_balances_for_account`
  and `recompute_holdings_cost_basis` filter the discriminator (same
  mechanism as the mig-103 `is_hidden` exclusion), so templates are
  invisible to balances/holdings structurally, not by caller discipline.

Reader inventory to update (known from the #196–#198 work): the view, the
two recompute functions, the posting-count denorm, the `scheduled`
predicate, reports.

### D3 — `recurring_transactions` becomes recurrence metadata + RRULE + a template pointer

The table slims to *recurrence* concerns and points at the template:

- **`rrule TEXT`** — the recurrence spec (RFC 5545). **Replaces** the
  discrete `frequency` / `monthly_day` / `weekly_dow` / `interval_units`
  columns (dropped). RRULE expresses MD's patterns directly (*Monthly ·
  every · 5th* → `FREQ=MONTHLY;BYMONTHDAY=5`; *Last* → `BYMONTHDAY=-1`)
  and the future patterns MD can't. Chosen by the user over discrete
  columns for expressiveness.
- **`source_payload JSONB`** — the raw MD reminder object, lossless
  (same pattern as `provider_raw_payload`, mig 110). Preserves the full
  split structure, `acdays`, and anything else MD sends, independent of
  what the structured model captures.
- **`template_header_id`** — composite FK (ledger-scoped, mig 121) to the
  template `txn_header`. The denormalized transaction-shape columns
  (`source_account_id` / `target_account_id` / `amount` / `memo` /
  `description`) are dropped; that data now lives on the template
  header + legs.
- Retained: `start_date`, `end_date`, `next_due_date` (maintained
  cursor), `last_acknowledged_date`, `is_loan_reminder` (**passive** in
  this design — carried through from import, no behavior), `is_active`,
  `origin`, `external_id`.

`is_loan_reminder` is a reminder owned/driven by a **loan account**
(mortgage, auto-pay) — amortization-aware principal/interest splits,
payoff tracking, likely auto-generation from loan terms. That is a
**separate design** (its own ADR) built on this spine; here the flag is
preserved but inert. Tracked in `follow-ups.md`.

RRULE expansion is genuinely complex (RFC 5545: `BYSETPOS`, edge cases) —
unlike the trivial QIF parser (ADR-0042), this warrants a **vetted,
maintained RRULE library** rather than hand-rolling, pinned per the
dependency-hygiene posture (exact package an implementation choice,
reviewed at build time). Expansion is C# computation (a
`RecurrenceExpander` service), not data access — no raw SQL in the API
layer.

The existing imported rows migrate (discrete columns → RRULE, shape →
template header+legs) and the **importer is updated** to emit RRULE +
create the template header+legs + stash `source_payload`. The importer
otherwise stays as-is (ADR-0010 round-trip preserved).

### D4 — Auto-commit is the days-in-advance signal; manual approve ships first

**`auto_commit_days_before INT NULL`** unifies MD's "Auto-commit / *N*
days before scheduled" control and closes the currently-dropped `acdays`
gap:

- `NULL` → **manual approve** (a Due prompt; the user posts/skips/edits).
- `N ≥ 0` → **auto-commit** the occurrence *N* days before its due date.

The column exists from slice 1 (data carries the intent), but **manual
approve ships in slice 1**; the **auto-commit firing worker is slice 2**
(see slice plan). Default is manual — the user stays sole approver of
cash events (the cash-model principle).

### D5 — Firing materializes a committed header by cloning the template

An occurrence is materialized **on demand** (never pre-created):
clone the template header + legs into a new `txn_header`
(`is_recurring_template = false`) dated at the occurrence, stamped
**`txn_headers.recurring_transaction_id` + `occurrence_date`** (the link
back to series + slot). It then flows through the normal register/recon
path and the `scheduled` filter catches it for free if it is future-
dated — no new status value. The series `next_due_date` advances.

### D6 — Edit scope: series + skip-next first; never mutate committed headers

v1 supports **edit the series** (the pattern) and **skip the next
occurrence**. A small **occurrence-exception table**
`(recurring_transaction_id, occurrence_date)` is reserved in the schema
for skip/override; "edit just this one" (override) and "this and future"
(series split) are later slices. Already-committed headers are **never**
retroactively mutated (committed cash is immutable).

### D7 — Surfaces: a sidebar "Reminders" hub (list + editor + calendar)

- A top-level **Reminders** sidebar entry with: the management **list**,
  the **recurrence editor**, and an **upcoming** surface that ships with
  BOTH a **month-grid calendar** (primary) and an **agenda** list toggle
  over the same windowed-expansion data. The calendar is in slice 1 (user
  call) — not deferred.
- The upcoming-occurrences endpoint is deliberately reusable: a future,
  **separate** Home dashboard (its own ADR) can render a reminders summary
  from it. Home is **out of scope** here.

### D8 — Keep importing reminders; fidelity is now preserved, not flattened

The importer keeps ingesting reminders (ADR-0010). With D1 (real legs)
and D3 (`source_payload` JSONB), the two prior fidelity losses are
closed: multi-leg reminders model as multi-leg templates, and `acdays`
is preserved (both structurally as `auto_commit_days_before` and raw in
`source_payload`).

## UI walkthrough (page-by-page)

Examples below use generic placeholders (no plan-identifying data).

1. **Reminders list** — series from `GET …/reminders`: Description,
   Source → Target account, Amount, recurrence rendered human
   ("Monthly on the 5th", "Every 2 weeks on Tue"), Next due, Active
   toggle, origin badge (Imported / Manual). Row actions: Edit, Disable
   (`is_active=false`, soft — never deletes), Skip next. Header: New
   reminder. Never shows individual occurrences.
2. **Recurrence editor** (dialog from the list) — the **same transaction
   editor** (header + legs, splits, investment shape) plus a recurrence
   panel: a Daily/Weekly/Monthly/Yearly picker that builds the RRULE
   (interval, by-day, by-month-day), Start date, optional End date, and
   an **Auto-commit** control (off = "Ask me to approve"; on = "*N* days
   before"). Modern-web layout (labels above inputs, `[Cancel][Save]`,
   inline validation — ADR-0023).
3. **Upcoming (calendar + agenda)** — one windowed-expansion data source,
   unioning materialized future headers (already `scheduled`) with
   computed-but-unfired series occurrences (expanded from RRULE), each
   badged **Scheduled** (committed future header) vs **Reminder** (un-fired
   slot). Slice 1 ships a **month-grid calendar** (primary) plus an
   **agenda** list toggle over the same data.
4. **Due / Approve prompt** (manual reminders) — shows the proposed
   transaction with `[Skip this occurrence]` `[Edit & post…]` `[Post]`.
   Post → D5 materialize; Skip → exception row + advance cursor;
   Edit&post → the editor pre-filled for a one-time override.
5. **Register integration (non-breaking)** — unchanged structurally; a
   materialized occurrence carrying `recurring_transaction_id` may show a
   small "series" affordance ("View series" / "Skip this occurrence").
   Un-fired slots are not headers, so they never appear in the register.

## Slice plan

- **Slice 1** — schema reshape (D1–D6 columns + views + recompute
  exclusion + occurrence-exception table reserved) + importer update +
  EF entity/DTO/repository/endpoints + Reminders list + recurrence editor
  + the **calendar + agenda** upcoming surface + manual Due/Approve +
  register badge. Large; expect to land as a few sub-PRs (backend
  foundation → API → manage UI → calendar/approve UI).
- **Slice 2** — the auto-commit firing **worker** (acts on
  `auto_commit_days_before`); reliable firing even when the app is
  closed. New background-worker infra (parallels, but is separate from,
  the deferred SimpleFIN sync worker).
- **Later (separate ADRs)** — occurrence override ("just this one") +
  "this and future" (series split); the broader Home dashboard;
  loan-account-driven reminders (`is_loan_reminder`).

## Consequences

### Positive
- Maximal reuse: the transaction editor, the leg/split model, investment
  shape, the `scheduled` filter, and snapshots all carry over.
- The hard balance/holdings invariant is protected **structurally** (view
  + recompute exclusion), not by caller discipline.
- Import fidelity improves (real legs + lossless `source_payload`).

### Negative / accepted
- Every live-transaction reader must exclude templates. Bounded and
  enumerable (the inventory above); centralized in the view layer + the
  two recompute functions. This is the price of the "same structures"
  reuse and is the highest-risk part of slice 1 — covered by explicit
  balance/holdings tests asserting a template never affects balances.
- One new runtime dependency (an RRULE library), dependency-hygiene
  reviewed. Justified: RFC 5545 expansion should not be hand-rolled.
- A migration rewrites the existing imported `recurring_transactions`
  rows into template headers+legs + RRULE. One-time, tested against a
  large real-world MD export's reminder count.

## Resolved (formerly open)
- **Month-grid calendar** ships in slice 1 (user call), with an agenda
  toggle on the same data.
- **Home dashboard** is a separate ADR; this design only exposes the
  reusable upcoming-occurrences endpoint, builds no Home surface.
- **`is_loan_reminder`** is passive here; loan-account-driven reminders
  are a separate ADR (see D3 and `follow-ups.md`).

## Test plan
- Balance/holdings: a template header + legs must produce **no**
  `txn_header_account_balances` / `holdings` rows; firing an occurrence
  produces exactly the live rows a hand-entered transaction would
  (extends the #196–#198 invariant suite).
- RRULE expansion: unit tests over the MD pattern set (daily/weekly/
  monthly-by-day/monthly-last/yearly + interval) and boundary cases
  (start/end clipping, DST-free UTC dates).
- Firing: clone correctness (legs, splits, investment shape), cursor
  advance, skip writes an exception + no header, committed headers never
  mutated.
- Importer: MD reminder → RRULE + template header+legs + `source_payload`
  round-trip; re-import idempotency; existing-row migration.

## Addendum — manual authoring surface (mutation slice)

The read + fire slice landed the templates, views, and `list`/`upcoming`/`fire`.
This slice adds **manual authoring** (create / edit / disable / skip) for
**both** transaction shapes, plus migration 125. Captured here so the route
surface and the reuse boundaries are pinned (the base ADR committed
"endpoints in slice 1" without enumerating them).

### Endpoint surface (added to `/api/ledgers/{ledgerId}/reminders`)

Series-level operations stay unified; only create/edit fork by shape, mirroring
the live `/transactions` vs `/investment-transactions` split (cross-shape edits
are refused with `reminder-shape-mismatch`):

| Verb | Route | Shape |
|---|---|---|
| `GET`   | `/{id}`            | detail — `Kind` bank/investment, recurrence + the template's legs |
| `POST`  | `/`               | bank create (`CreateReminderRequest`) |
| `POST`  | `/investment`     | investment create (`CreateInvestmentReminderRequest`) |
| `PATCH` | `/{id}`           | bank edit (partial recurrence + replace-all postings) |
| `PATCH` | `/{id}/investment`| investment edit (partial recurrence + replace-all transaction) |
| `PATCH` | `/{id}/active`    | disable/enable (soft `is_active`, shape-agnostic) |
| `POST`  | `/{id}/skip`      | skip one occurrence (shape-agnostic) |

The read DTOs carry an **`amount`** — `ReminderSummary.Amount` and
`UpcomingOccurrence.Amount` — the source-side net of the template (the cash
impact on the originating account; negative = outflow, positive = inflow). It
is the figure the SPA agenda/calendar shows next to each reminder (Moneydance
parity), computed from the template's originating-account legs
(`account_postings_on_header = header_total_postings`, the register's own
"net on the source account" definition) — not a new stored column. A fired
occurrence carries its own committed net; a reminder slot carries the
template's net.

### Reuse boundaries (the organizing principle of this slice)

- **Bank** legs mirror `TransactionsRepository.AddPostingLegs` (source leg
  carries the signed amount + memo; the server writes the counterpart as
  `-amount`, so sum-to-zero is server-enforced). The per-posting shape +
  account validation is the shared `PostingValidation` extracted from
  `TransactionsEndpoints`.
- **Investment** legs are built by
  `InvestmentTransactionsRepository.BuildTemplateLegsAsync`, which runs the
  identical action-by-field validation + account/security resolution + posting
  construction as a live investment create (`ValidateAndResolveAsync`, extracted
  from `CreateAsync`/`PatchAsync`) — but produces **no holdings/lots** (a
  template never touches them). Error mapping reuses
  `InvestmentTransactionsEndpoints.MapFailure`.

### Manual `external_id` strategy

A manual series and its template header both write `external_id = NULL`
(`origin = 'manual'`, `provider_key = NULL`) — the manual arm of the
mig-107/109 `txn_headers` CHECKs and the mig-013 partial-unique on
`recurring_transactions.external_id` (which excludes NULLs). The
`mdreminder:{id}` synthetic id stays import-only. No schema change.

### Keystone hardening — `HoldingsRecomputeInterceptor`

`recompute_holdings_cost_basis` reads `live_txn_headers`, so a template is
already excluded from the holdings walk. But the interceptor that *enqueues*
recompute work captured any investment-shape leg by `(account, security)` —
including a template's holdings-side leg — which would hit the recompute's
unconditional auto-create branch and leave a spurious zero-qty `holdings` row.
The interceptor now treats template legs as invisible (skips legs whose header
is `is_recurring_template`), so the keystone holds for **investment** templates
exactly as for bank. Proven by
`Create_investment_reminder_template_never_touches_holdings_lots_or_balances`.

### Skip mechanics + catch-up (migration 125; §9.2)

`recurring_occurrence_exceptions` records one suppressed `(series, date)` slot.
`GetUpcomingAsync` anti-joins it out of the un-fired branch; `FireAsync` refuses
a skipped slot; `SkipAsync` refuses an already-fired slot — skip and fire are
mutually exclusive per slot. The next-due cursor recomputes to the earliest
occurrence that is neither fired nor skipped.

**Catch-up (ADR-0049 refinement).** Acting on an occurrence — **Post or Skip** —
also marks every EARLIER un-acted occurrence (strictly before the acted date,
within `[start, date)`) as skipped, so the calendar + cursor never strand an
overdue backlog; the cursor then lands on the first occurrence after the acted
slot. An earlier ALREADY-FIRED occurrence is preserved — it carries real cash
and is never re-skipped. `FireAsync` / `SkipAsync` run the cascade inside their
transaction (before the cursor recompute) and report the tally
(`skippedEarlierCount` + the earliest cascaded date) so the SPA can name it in
its confirm + a post-action notice. This **supersedes** the earlier "a
far-future skip doesn't regress the near cursor" intent: the cursor computation
is still earliest-un-consumed, but acting now deliberately consumes the backlog
behind the acted slot. (User-confirmed: **Post cascades like Skip**, gated by a
named-count confirm given the income-hiding risk on un-posted past deposits.)

Migration 125 also extends `fn_ledger_snapshot_payload` / `_restore` to
round-trip the new table (the mig 111->112 lesson) and keeps
`LedgerSnapshotPayload.InScopeTables` aligned.

### Deferred (not built here)

- Series **hard-delete** endpoint (disable supersedes it; the template FK is
  `ON DELETE RESTRICT`, so delete is its own small slice).
- **Unskip** (`DELETE /{id}/skip`).
- `recurring_occurrence_exceptions.exception_kind` discriminator (added when the
  occurrence-override slice needs it).
- The SPA (list -> recurrence editor -> calendar/agenda -> approve-to-fire).
- Auto-commit firing worker (`auto_commit_days_before` is captured, not acted on).

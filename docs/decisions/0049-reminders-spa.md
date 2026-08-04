# 0049 — Reminders SPA (calendar + agenda, parity with a better UX)

* Status: Accepted
* Date: 2026-06-15
* Related: ADR-0047 (reminders backend + mutation API), ADR-0048 (live/template
  view layer), ADR-0023 (modern-web UX conventions), ADR-0043 (account/category
  picker), ADR-0037 (snapshots SPA panel — the structural precedent), ADR-0021
  (design tokens/primitives). Backend: PRs #199–#202.

## Context

The reminders backend is complete (#202): list / upcoming / detail / create /
edit / disable / skip / fire, for bank and investment shapes, with each series'
signed **amount** on the read surface (`ReminderSummary.Amount`,
`UpcomingOccurrence.Amount` — the net on `recurring_transactions.source_account_id`,
mig 125). There is no SPA surface yet.

The parity target is Moneydance's Reminders view: a **month calendar** of
reminders on their due days, a **right-hand agenda** (one row per reminder:
name, signed amount, next-due date, humanized frequency), a **click-a-day
popover** with per-reminder Edit / Record-next-occurrence / Skip, and add/delete.
The goal is **feature parity with a better UX** (ADR-0021/0023 idioms; clearer
frequency phrasing; colour+sign amounts) — not a visual copy (per the
"MD parity = features, not visuals" principle).

## Decisions (agreed with the user)

### D1 — One route, calendar + agenda together (parity centerpiece)

`/ledgers/$ledgerId/reminders` (code-based route in `router.ts`, sibling of
`settingsRoute`; ledger id via `useParams`). The page hosts a **List / Upcoming**
view toggle. **Upcoming** = a full-width month-grid calendar over
`GET /reminders/upcoming` whose chips ARE the occurrences; clicking an un-fired
chip opens an action popover (see D7). The Home dashboard remains OUT (separate
ADR); the upcoming endpoint is built to be reusable by it.

### D2 — Sub-slice plan (each = one PR)

- **R1 — View & act:** the route + nav + the `reminders` client/types + the
  **recurrence humanizer**, the **full-width month calendar** (chips = the
  occurrences) + the **click-chip action popover** (D7), and the read detail;
  plus the three shape-agnostic actions — **Record/fire**, **Skip**,
  **Disable/Enable**. No authoring. Ships value over the user's imported
  reminders (after re-import materializes them).
- **R2 — Author:** the create/edit dialog (bank + investment transaction
  sub-forms reusing the live editors) + the **recurrence picker** (RRULE
  builder) + start/end/auto-commit; and a hard-delete affordance for manual
  series. The same editor backs **adjust-at-post** — Post opens the occurrence
  pre-filled + editable (a `fire`-with-override), so a varying bill (e.g. a
  credit-card payment) commits the adjusted instance rather than the template's
  fixed amount.

R1 first: it establishes every reuse seam (client, query keys, page shell,
detail→legs) and carries none of the editor-extraction / RRULE-builder risk.

### D3 — Amounts everywhere (Moneydance parity)

The agenda, calendar day-popover, and detail show each occurrence's **signed,
colour-coded amount** (`formatSignedAmount`; outflow `state-danger`, inflow
`state-success`). The backend already supplies it (#202); no per-row detail
fetch. This was the key gap vs MD and is the most prominent datum in its view.

### D4 — Zero new dependencies (hand-roll, per the pinned-deps posture)

- **RRULE → human text:** a hand-rolled `lib/recurrence.ts` (`humanizeRrule`,
  and in R2 `buildRrule`/`parseRrule`). The SPA never expands occurrences (the
  server does via `/upcoming`); it only humanizes a closed pattern set (daily /
  weekly-by-day / monthly-by-day / monthly-last / yearly + interval). No `rrule`
  npm dep. Clearer phrasing than MD ("Every 2 weeks on Mon" vs MD's "daily
  (14 days)"; "Every 3 months" vs "monthly (every third)"). An unsupported
  imported rule humanizes to a safe fallback, never a wrong phrase.
- **Calendar:** a hand-rolled CSS-grid month view (~40 cells, a
  `monthMatrix(y,m)` UTC helper). No calendar dep. No date-selection widget / no
  i18n edge cases beyond the month label; the day cells are static, but the
  occurrence chips are interactive (they open the action popover — D7).

### D5 — Nav: a Hub link now; sidebar entry deferred to a maintainer decision

R1 ships a **Reminders link on the Ledger Hub header row** (next to Bank feeds /
Settings) — the on-convention discovery path.

A top-level **sidebar rail entry** is **deferred**, not shipped. There is a real
conflict: ADR-0047 D7 called for a sidebar entry, but `AuthedSidebar` was
subsequently built to *deliberately funnel all per-feature management through
the Hub* ("management funnels through the Hub rather than sprouting a per-feature
icon in the rail" — its own comment). Reminders are a daily-driver "what's due"
surface, which is the argument FOR a rail entry; the implemented convention is
the argument against. Rather than unilaterally override that convention, R1 uses
the Hub link and the rail-entry call is left to the maintainer (add it, or keep
Hub-only). This supersedes ADR-0047 D7's sidebar wording.

### D6 — Reuse map

The live HTTP wrapper (`request`/`ApiError`), TanStack Query (`useQuery`/
`useMutation`/`invalidateQueries`), the page shell (`MainArea`/`TopBar`/
`Breadcrumb`/`MainPane`), the snapshots-panel topology (panel + dialogs),
`ContextMenu`/`ConfirmDialog`/`Button`/`Panel`, and `formatSignedAmount`/
`formatLedgerDate`. New: `lib/api/reminder.ts`, `lib/types/reminder.ts`,
`lib/recurrence.ts`, and the `routes/ledgers/reminders/*` components. R2 reuses
the bank postings sub-form (extracted from `TxnRowEdit`) and the investment
draft/fields layer (`useInvestmentTxnDraft`, `legsToDraft`).

### D7 — Calendar-popover rework (supersedes the R1 side-rail agenda)

R1's first cut paired a read-only calendar with a separate actionable agenda
side-rail. That drew every occurrence **twice** (a dead calendar chip + an
agenda row) with no visual correlation between them — the calendar was
decoration and the agenda didn't need it. The rework makes the **calendar the
single surface**: one full-width month grid with taller cells, where each chip
IS its occurrence (payee + signed, colour-coded amount; `●` un-fired vs `✓`
posted). Clicking an un-fired chip opens a **left-click detail CARD** anchored
under it — a small `Popover` primitive (`role="dialog"`; Esc / outside-click /
edge-flip) holding the occurrence detail (payee + amount + date) and **Post** /
**Skip** action **buttons**. Deliberately a card with buttons, NOT a dropdown
menu: a menu of text actions reads as a *right-click* context menu, which is
incongruous from a left-click — every calendar app (Google / Outlook /
Fantastical) opens a detail card on left-click. `+N more` expands a busy day
inline; posted occurrences are read-only chips (no card). `Popover` is a new
~25-line primitive whose positioning mirrors `ContextMenu` (the right-click
action-list surface, left untouched); extracting a shared hook is a follow-up if
a third anchored surface appears (rule of three). The **List** toggle remains
the linear/management view.

### D8 — Catch-up: acting clears the earlier backlog (Post + Skip)

Post or Skip on an occurrence also marks every EARLIER un-acted occurrence
(within `[start, date)`) as skipped, so the calendar + next-due cursor never
strand months of overdue chips; the cursor then lands on the first slot after
the acted one. An earlier ALREADY-FIRED occurrence is preserved — real cash,
never re-skipped. The user confirmed **Post cascades like Skip**; because a
Post-cascade can abandon un-posted past *income*, the confirm warns and a
post-action notice reports the exact count + how far back
(`skippedEarlierCount` / `skippedEarlierFrom` on the fire/skip responses).
Backend only (`CascadeSkipEarlierAsync`, `recurring_occurrence_exceptions` rows,
no migration); refines ADR-0047 §9.2.

### D9 — Adjust-at-post backend: per-shape fire routes (reuse, not copy)

Three fire routes, symmetric per shape:
- `POST /fire` — clone the template verbatim (no edits; shape-agnostic; for the
  future auto-commit worker).
- `POST /fire/bank` (`FireBankReminderRequest`) — adjust-at-post for a bank
  series: the edited transaction (source + N postings, **incl. splits**).
  `FireBankAsync` **reuses** the live `TransactionsRepository.CreateAsync`.
- `POST /fire/investment` (`FireInvestmentReminderRequest`) — adjust-at-post for
  an investment series. `FireInvestmentAsync` **reuses** the live
  `InvestmentTransactionsRepository.CreateAsync` (holdings + lots + FIFO).

Both live `CreateAsync` methods gained an occurrence-stamp parameter + an
ambient-transaction check, so each fire path runs the live create + catch-up
cascade + cursor advance in ONE atomic transaction — reuse, not a copy of the
leg/holdings/lot logic. Both stamp `(recurring_transaction_id, occurrence_date)`,
are idempotent + catch-up-aware, and shape-guarded (the bank route on an
investment series — or vice-versa — is a 422); the endpoints validate the edited
postings/shape exactly like a live create. `seriesNextDue` is surfaced on each
occurrence so the form can show the catch-up line inline.

### D10 — The occurrence dialog (built)

Left-click a reminder chip → `ReminderOccurrenceModal`: a centered dialog that
fetches the reminder detail + register context (accounts / payees), branches on
shape, and hosts the LIVE editor prefilled from the template legs —
`TxnRowEdit` (new-mode + an occurrence-date `postedAt` prefill + the
`reminderBankPrefill` posting split, splits included) for bank;
`InvestmentTxnRowEdit` in a new `kind:'fire'` mode (seeded via `legsToDraft`,
whose leg param was widened to a structural `InvestmentLegView` so reminder legs
satisfy it without a cast) for investment. The editor's Save commits the EDITED
occurrence (→ `/fire/bank` or `/fire/investment`); **Skip** sits in the editor
footer's leading slot as a peer of Cancel/Save; the catch-up line shows inline.

Reuse seams added to the editors (all additive, default to register behavior):
`cancelOnOutsideClick` (off inside the modal — the dialog owns its dismissal, so
clicking Skip no longer trips the register's outside-click-cancel),
`footerLeading` (the Skip slot), bank new-mode `postedAt`. `ReminderDetail`
gained `sourceAccountId` so the SPA can split bank legs into postings + identify
the investment brokerage.

**Editor harmonization (ADR-0023 §P):** the investment register editor's
Cancel/Save moved from top-right of its first row to a bottom footer built with
the shared `Button` primitive, matching bank — fixing a long-standing
inconsistency in the live register editors and giving both a consistent footer
(with the leading slot).

### D11 — Skipped occurrences stay visible (read-only trail) + cursor-horizon fix

Two refinements after the catch-up cascade (D8) landed in practice (acting on a
years-overdue imported reminder skipped dozens of occurrences):

- **Skipped slots are shown, not hidden.** `GET /upcoming` previously dropped
  skipped `(series, date)` slots, so a catch-up that cleared months of overdue
  occurrences left the calendar with silent gaps. It now emits them as
  `kind="skipped"` — a read-only, struck-through chip (`⊘`) alongside `✓`
  Scheduled — so the cascade leaves a legible trail of what was cleared. The
  chip is non-actionable (un-skip is not built); the legend gains a Skipped key.
- **Next-due cursor horizon fix.** `ComputeNextDueAsync` bounded its RRULE
  expansion at `start + 2 years`. For a series that started years ago, a
  catch-up consumes that whole window, so the cursor found no open slot and
  stranded `next_due_date` at NULL. The horizon is now anchored to the LATER of
  the series start and the most recent consumed (fired/skipped) occurrence, then
  extended two years — so the window always reaches past the consumed backlog to
  the first open slot. No clock is introduced (the codebase has none); the
  anchor is derived from committed data, keeping the computation deterministic
  and testable.

Re-import is safe for both: the catch-up skips live in
`recurring_occurrence_exceptions` (Coffer-internal, absent from the Moneydance
export), and the importer upserts the `recurring_transactions` row in place
(`ON CONFLICT (external_id) DO UPDATE`, id preserved) so the `ON DELETE CASCADE`
never fires. The upsert does reset `next_due_date` to NULL (the importer always
sends null), which the next fire/skip recomputes via the fixed cursor.

## Consequences

- Parity: calendar + agenda + amounts + per-occurrence act, matching MD with the
  ADR-0021/0023 treatment.
- Zero dependency-hygiene surface added.
- `fire` materializes a committed transaction → the fire flow invalidates
  `accounts`/`holdings`/register caches, not just reminders.

## Deferred

- Authoring (R2), including **adjust-at-post** (Post opens the pre-filled
  editor → a `fire`-with-override, for varying bills), hard-delete, and the
  recurrence picker. The general future-transaction calendar and the Home
  dashboard (separate ADRs). Print.


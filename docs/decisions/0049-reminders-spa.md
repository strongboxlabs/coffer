# 0049 — Reminders SPA (calendar + agenda, parity with a better UX)

* Status: Accepted; D3 and D10 amended 2026-11-14 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (D3: an estimating
  transfer between currencies sets its other side through the one rate rule for each due
  date, both figures carry "≈", and with no rate its fire is refused, and (2026-11-16, D15)
  each estimate rounds once at its account's currency's places, half away from zero;
  D10: a fixed-amount series between currencies seeds both amounts from the template);
  D10 amended 2026-11-17 (an estimating series' occurrence opens on its estimate, in one
  currency too)
* Date: 2026-06-15
* Related: ADR-0047 (reminders backend + mutation API), ADR-0048 (live/template
  view layer), ADR-0023 (modern-web UX conventions), ADR-0043 (account/category
  picker), ADR-0037 (snapshots SPA panel — the structural precedent), ADR-0021
  (design tokens/primitives). Backend: PRs \#199–#202.

## Context

The reminders backend is complete (\#202): list / upcoming / detail / create /
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

## Decisions

### D1 — One route, calendar + agenda together (parity centerpiece)

`/ledgers/$ledgerId/reminders` (code-based route in `router.ts`, sibling of
`settingsRoute`; ledger id via `useParams`). The page hosts a **List / Upcoming**
view toggle. **Upcoming** = a full-width month-grid calendar over
`GET /reminders/upcoming` whose chips ARE the occurrences; clicking an un-fired
chip opens an action popover (see D7). The Home dashboard remains OUT (separate
ADR); the upcoming endpoint is built to be reusable by it.

*Amended 2026-11-06 ([ADR-0109](0109-overview-is-an-attention-first-home.md)'s third
slice): the route takes `?reminder=<id>`, which opens that series' editor as the list's
Edit does, so a link elsewhere (the home screen's Cash ahead) can name the reminder it is
about.* The parameter holds the dialog open: closing it clears the parameter in place,
so Back does not reopen it, and focus goes to the tab of the view showing. An id that
names no series opens nothing and says nothing.

### D2 — Sub-slice plan (each = one PR)

- **R1 — View & act:** the route + nav + the `reminders` client/types + the
  **recurrence humanizer**, the **full-width month calendar** (chips = the
  occurrences) + the **click-chip action popover** (D7), and the read detail;
  plus the three shape-agnostic actions — **Record/fire**, **Skip**,
  **Disable/Enable**. No authoring. Ships value over imported reminders
  (once re-import materializes them).
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

The agenda, calendar day-popover, and detail show each occurrence's **signed
amount** (`formatSignedAmount`). The backend already supplies it (\#202); no
per-row detail fetch. This was the key gap vs MD and is the most prominent datum
in its view.

*Amended 2026-10-31: the amount is muted, not colour-coded.* An occurrence is
unposted, so its amount is not news: `text-text-muted`, never red or green
(ADR-0023 §H).

*Amended 2026-11-04: each amount is in its series' source account's currency.*
An occurrence carries that currency (`GET /reminders/upcoming`), and the
management list's series now carries it too (`GET /reminders` adds
`currencyCode`, read whether or not the account is active). A series with no
single source has no currency, and its amount is the template's 0, which is no
figure: the list prints a dash and a calendar chip prints no amount, never one
in dollars. Before this, every amount on the page was printed in dollars
whatever the account's currency.

*Amended 2026-11-06: an estimating series' amount is marked "≈" wherever it is shown.*
A series set to estimate its amount carries `estimate` on each occurrence. Every surface
that prints such an amount marks it "≈": the Reminders list, its calendar chips, the
occurrence dialog, the home screen's Bills rows (upcoming and overdue) and Cash ahead's
lowest figures. The mark holds whether or not an estimate could be made yet. Until the
series has posted, the amount is the one entered on the reminder, which stands in for an
amount not yet known, so it is no more certain than an estimate. One predicate decides it
(`lib/reminderEstimate.ts`), and its title says which case applies: estimated from recent
occurrences, or the amount entered until an estimate can be made. A series with a fixed
amount carries no mark.

*Amended 2026-11-14 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) D13: an estimating transfer between currencies
estimates both sides.* Its source side is estimated as before. Its other side is set for
each occurrence from the source estimate at the rate `RateDay.Of` gives the due date
(ADR-0114 D7, D9: a day before the server's UTC today reads its own rate, today or later
the latest, and a lapsed pair has none), derived from the two accounts' stored rates
against the reporting currency (ADR-0114 D13), and `fx_rates` is never written. The other
side, and the reason it has none, are resolved per occurrence (series and due date), not
per series as the existing reasons are. With no rate the occurrence has no estimate,
under a new reason code in `EstimateUnavailable`, and its fire is refused
(`EstimateUnavailableForFire`), as an estimate with no history is. Both figures are
estimates, and both carry the "≈" mark above. With no rate, the mark takes the existing
title for an estimate that cannot be made yet (`estimateTitle`); no new wording is
added. Under Manual approval the occurrence dialog shows both figures, and the second
amount can be corrected before posting. *(Extended 2026-11-16, ADR-0114 D15.)* Every
estimate, the source side's and, between currencies, the other side's, is rounded once
at its own account's currency's places, half away from zero; the source estimate
rounded half to even at two places before (settled by Claude, 2026-11-16).

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

### D5 — Nav: a Hub link now; sidebar entry deferred

R1 ships a **Reminders link on the Ledger Hub header row** (next to Bank feeds /
Settings) — the on-convention discovery path.

A top-level **sidebar rail entry** is **deferred**, not shipped. There is a real
conflict: ADR-0047 D7 called for a sidebar entry, but `AuthedSidebar` is built to
*deliberately funnel all per-feature management through the Hub* ("management
funnels through the Hub rather than sprouting a per-feature icon in the rail" —
its own comment). Reminders are a daily-driver "what's due" surface, which is the
argument FOR a rail entry; the implemented convention is the argument against.
R1 keeps the convention and uses the Hub link; a rail entry stays an open
decision (add it, or keep Hub-only). This supersedes ADR-0047 D7's sidebar wording.

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

A read-only calendar paired with a separate actionable agenda side-rail draws
every occurrence **twice** (a dead calendar chip + an agenda row) with no visual
correlation between them — the calendar is decoration and the agenda doesn't
need it. The rework makes the **calendar the
single surface**: one full-width month grid with taller cells, where each chip
IS its occurrence (payee + signed amount in the chip's own colour; `●` un-fired vs `✓`
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
never re-skipped. **Post cascades like Skip**; because a
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

*Amended 2026-10-24: `POST /fire` is removed.* The API serves only the UI, and the UI
posts every occurrence through the per-shape routes with the values its dialog shows.
The verbatim clone remains only as the auto-post job's write
([ADR-0097](0097-reminder-auto-post.md), 2026-10-24 amendment): it leaves earlier
occurrences open instead of cascading, and refuses an investment template.

*Amended 2026-10-27 ([ADR-0108](0108-memo-and-tags-per-side.md) D3, D14): what the two fire routes refuse, and the bank
body's notes.* Both answer 422 `reminder-template-has-no-lines` for a template
with no lines, which names no account to post from. Both refuse an inactive
account as a live create does: `/fire/bank` with `account-inactive`,
`/fire/investment` with the investment create's own inactive-account codes.
`FireBankReminderRequest` carries the memo and tags in a `notes` member, the
live create's (ADR-0025, amendment of this date), written from its source
account.

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

**Editor footer (ADR-0023 §P):** both register editors put Cancel/Save in a
bottom footer built with the shared `Button` primitive, with the leading slot.

*(Amended 2026-11-14, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D14.)* A fixed-amount series between currencies
seeds both amounts from the template, as the reminder series editor does, and neither is
refilled from a rate. `reminderBankPrefill`, which this dialog and the series editor
both seed from, builds each posting from the source leg's amount alone and drops the
counterpart leg's (`reminderOccurrenceDraft.ts`). One fix serves both hosts: it carries
the counterpart's amount as the posting's second amount. While the second amount
shows, the dialog layout gains one track for it (ADR-0025, 2026-11-14 amendment).

*(Amended 2026-11-17, decided by the owner.)* An occurrence of an estimating series opens
on its estimate, not on the template's amount, whatever its currencies. The template legs
still seed the rest of the editor. When the occurrence has an estimate, `reminderBankPrefill`
sets the posting's amount to it, and sets the second amount only when the estimate carries
the other side's (ADR-0114 D13). So the Amount field starts at the figure the dialog's
subtitle prints with "≈" (D3), the one auto-post writes ([ADR-0097](0097-reminder-auto-post.md)).
Before, only an estimating transfer between currencies opened on its estimate, and a series in
one currency opened on the template's amount. With no estimate yet, the series opens on the
template's amount as before. The field stays editable, and the occurrence posts what it holds.

### D11 — Skipped occurrences stay visible (read-only trail) + cursor-horizon fix

Two refinements for the catch-up cascade (D8), which on a years-overdue imported
reminder skips every occurrence in between:

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


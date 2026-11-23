# 0051 — Reminder CRUD + next-due cursor correctness

* Status: Accepted (slices A–C implemented); D3 amended 2026-11-14 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (the
  second amount and its rate line reach the series editor through the shared editor)
* Date: 2026-06-16
* Related: ADR-0047 (reminders backend / recurring transactions), ADR-0048
  (live-template data-layer views), ADR-0049 (reminders SPA), ADR-0043
  (account/category picker), ADR-0029 (investment transaction editor), ADR-0030
  (domain-pure code organization), ADR-0050 (account editor + loan amortization).

## Context

The reminders SPA (ADR-0049) leaves two gaps for imported Moneydance reminders:

1. **`next_due_date` is NULL on imported reminders.** The importer set the
   recurrence (RRULE, start, end, MD's acknowledged date) but left the cursor
   null — it had no recurrence expander (that lived in the API, `src/Api/`, which
   the importer cannot reference). A naive recompute would be worse: with no
   fired/skipped history in Coffer, an imported reminder running for years would
   strand the cursor on its *first* occurrence and, on first fire, cascade-skip
   years of phantom "overdue" slots.
2. **There is no reminder-creation UI.** The SPA can fire / skip / list / show a
   calendar, but a series can only be created by the importer — there's no way to
   add a reminder in-app, edit one, or turn an existing transaction into a
   reminder. The create/edit endpoints (`POST` / `PATCH /reminders`, bank +
   investment) already exist from ADR-0047; only the UI is missing.

## Decisions

### D1 — `next_due` is anchored to the acknowledged floor

`next_due` = the first RRULE occurrence **strictly after**
`last_acknowledged_date` (Moneydance's `ackdt`, already carried on import). MD
moves `ackdt` forward as each occurrence is recorded, so for a reminder in use
this lands the cursor on the genuine next payment. Occurrences **on or before** the floor are
treated as already handled **everywhere** that walks the series:

* the **cursor** (`ComputeNextDueAsync`) skips them, and
* the **catch-up cascade** (`CascadeSkipEarlierAsync`) does not mark them
  skipped — so firing an imported reminder never writes a phantom pre-import
  backlog.

`next_due` is a **derived cursor**, not user-owned metadata: the importer
recomputes and **refreshes** it on every import (the upsert already does
`next_due_date = EXCLUDED.next_due_date`), which backfills existing NULLs on
re-import. (Contrast ADR-0050 D10's seed-once rule, which governs *user-editable*
metadata like account names + loan terms.)

### D2 — Recurrence math is shared, not duplicated

`RecurrenceExpander` moves from `src/Api/Reminders/` to a new pure project
**`Coffer.Domain.Reminders`** (mirroring `Coffer.Domain.Investment`, ADR-0030),
joined by a new pure **`NextDueCalculator`**. Both the API (recompute on
fire/skip/edit) and the importer (seed on import) compute occurrences + the
cursor through this one implementation — so a freshly imported reminder's cursor
matches exactly what the API would compute. `Ical.Net` moves to the shared
project; the API picks it up transitively.

### D3 — Reminder editor: account-derived kind, reusing the existing transaction form

A new `ReminderEditorDialog` (web) creates and edits a reminder **series**. Shape
decisions:

* **The source account determines the kind.** The user picks the source account
  first; its type derives whether the reminder is bank or investment. There is
  **no manual kind toggle**. On edit, the kind is fixed by the existing series, and
  so is the source account: the editor offers no source picker, and the API refuses
  an edit that names another source (`reminder-source-immutable`) and writes nothing.
* **Reuse the existing new-transaction form** for the transaction shape — once
  the account is picked, embed the SAME editor the register uses (`TxnRowEdit`
  for bank, `InvestmentTxnRowEdit` for investment), NOT a bespoke posting editor.
  The reminders occurrence dialog (ADR-0049) already embeds these, so the reuse
  pattern is proven.
  *(Amended 2026-11-14, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D14: the second amount and its rate line reach
  the series editor through this shared editor, as one extra track while they show.
  The prefill and the rate line belong to the editor, whatever the row came from; the
  import and feed write paths never derive an amount from a rate.)*
* **Schedule section** wraps the form — a recurrence builder (Daily / Weekly /
  Monthly / Yearly + interval + day-of-month/week + start + optional end)
  producing the RRULE (validated by the shared expander), plus
  auto-commit-days-before.
* **The source account shows in the dialog title** ("Edit reminder · Checking")
  — which register a reminder posts to is otherwise invisible in these forms
  (the embedded transaction editor assumes its register context). Applied
  CONSISTENTLY across every reminder CRUD dialog (this create/edit editor AND the
  occurrence-post modal, ADR-0049); on a from-scratch create it's the account
  picker until one is chosen.

The dialog combines the reused form's transaction body with the schedule into ONE
save → existing `POST /reminders[/investment]` (create) / `PATCH` (edit). Bank +
investment ship in ONE PR (rather than a bank-first split).

### D4 — Three entry points into the editor

* **New reminder from scratch** — a button on the reminders list panel.
* **Edit reminder** — a per-row action on the reminders list (prefill from
  `GetDetail`; kind locked).
* **Add reminder from this transaction** — a "Create reminder" item in the
  register row menu of **both** the bank and investment registers, opening the
  editor prefilled from the transaction (source account, payee, postings — or the
  inverted investment draft) with the **schedule left blank** for the user to
  fill (chosen over a guessed default). The prefill reuses each register's
  existing Duplicate mapping (`rowsToDuplicatePrefill` / `legsToDraft`).

### Amendment, 2026-10-27 — the editor carries memo and tags per side (ADR-0108 D1, D14)

* **Template lines keep their identity.** The reminder read contract
  (`ReminderDetail`) exposes each leg's id (`ReminderLegDto.LegId`). A bank edit
  names each line it keeps by the id of that line's leg on the series source, and
  the line keeps its memo and tags only through it; a line sent without one is new
  and starts blank, and an id that names no such leg, or names one twice, is
  refused. There is no fallback matching by posting and account.
* **The read contract carries the stored tags**, the template header's own and each
  leg's own, and the editor seeds its Tags field from them, so a picked tag restates
  the stored set instead of replacing one the user never saw.
* **The form has a Tags field.** It is the embedded transaction editor's (D3), now
  wired through save, load and fire.
* **An edit keeps a deactivated account the template already uses**, bank and
  investment alike, and refuses a newly chosen inactive one; a create stays strict.

## Consequences

- Imported reminders get a correct, MD-aligned cursor; the calendar/agenda show
  the real next payment; firing no longer threatens a phantom catch-up cascade.
- Recurrence date math has a single home; the importer and API can never drift.
- Reminders become a first-class in-app object (create / edit / from a posted
  transaction), not just an import artifact — a step toward MD retirement.

## Implementation slices (each = one PR, green on CI)

A. **`next_due` correctness (backend)** — shared `Coffer.Domain.Reminders`
   (moved expander + `NextDueCalculator`); importer seeds the cursor; API
   cursor + catch-up honor the acknowledged floor. **(Implemented.)**
B. **Reminder editor (create + edit) + list actions** — `ReminderEditorDialog`
   (bank + investment in ONE PR) per D3: account-picker derives the kind, embeds
   the existing `TxnRowEdit` / `InvestmentTxnRowEdit`, + a `RecurrenceBuilder`;
   "New reminder" + per-row "Edit" on the list. **(Implemented.)**
C. **Add reminder from a transaction (both registers)** — the D4 "Create
   reminder" row-menu entry on the bank AND investment registers, prefilled with
   the schedule blank, reusing slice B's editor via `fromTransaction` /
   `fromInvestmentTransaction`. Shipped with editor polish: a shared
   `ReminderDialogShell` (overlay / header / escape-to-close, de-duplicating the
   editor + occurrence modal) and two `RecurrenceBuilder` fixes — the "On"
   end-date radio was a no-op, and weekly allowed an empty `BYDAY` that silently
   fell back to the start-date weekday — plus a short-month day-of-month hint.
   **(Implemented.)** *(2026-11-03: the shell is now `components/ui/EditorDialog`,
   the same chrome shared with every dialog that hosts a transaction editor,
   with its own style-guide section.)*

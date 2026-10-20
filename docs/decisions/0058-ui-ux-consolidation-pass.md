# 0058 — UI/UX consolidation pass: shared overlay/state primitives, sidebar destinations, settings consolidation, register polish

Status: Accepted

## Context

After the feature build-out the SPA had accumulated drift that a feature-by-feature
view never surfaces:

- **Dialogs** were each hand-rolled — every one its own backdrop, Esc handler,
  and (inconsistent) focus handling; several had no focus trap or return-focus.
- **Navigation was split** three ways: a per-ledger destinations strip lived as
  emoji links in the Overview header, a `⋯` control hopped to the "hub", and Bank
  feeds was a standalone `/feeds` route — none of it in the persistent sidebar.
- **Register chrome** carried redundant bands (a 3-tile account band duplicating
  the breadcrumb + filter-tab counts; an always-on "N rows loaded" footer) and the
  investment portfolio table grew unbounded above the register.
- **Inlined patterns** — the all-caps field-label span (~20 files), hand-rolled
  empty states, and `err instanceof ApiError ? err.detail : '…'` formatters
  (~11 files) — had drifted in spacing/size/copy.

This pass consolidates the surface; it refines (does not supersede) ADR-0021,
0023, 0045, 0037, 0054, 0056.

## Decision

1. **Shared primitives.** `Modal` owns one backdrop + focus-trap + Esc +
   backdrop-dismiss + return-focus; `EmptyState`/`EmptyStateInline`, `FieldLabel`,
   `Checkbox`, and `errorMessage(err, fallback)` replace the inlined variants.
   `ConfirmDialog` is rebuilt on `Modal`; all 9 hand-rolled dialogs migrate onto it;
   3 native `confirm()` calls become `ConfirmDialog`.

2. **Navigation into the sidebar.** Per-ledger destinations (Overview / Accounts /
   Securities / Reminders / Activity / Settings) render in the persistent sidebar;
   the Overview emoji header-nav and the `⋯`-to-hub control are retired. The
   account filter tabs sit directly below the destinations. The sidebar account
   list renders **one section per account type** (Banking / Cash / Credit cards /
   Investments / Assets / Liabilities / Loans), labelled + ordered by a shared
   `lib/accountTypes` metadata module the Ledger Hub also consumes — so the two
   can't drift, and the catch-all "Other" bucket is gone. (Resolves the
   follow-up "Loan / Asset / Liability deserve their own sections".)

3. **Sync affordances.** Ledger-wide "Sync all" is an icon button beside the
   ledger picker (shown only when the ledger has connections); the per-account
   register button is relabeled "Sync account". Three scopes, three homes.

4. **Settings consolidation.** One tabbed surface: **General** (ledger name +
   balance-health maintenance + danger-zone delete — rename/delete are inert
   stubs pending their API endpoints) / **Snapshots** / **Bank feeds** (the former
   standalone `/feeds` page; route retired) / **Quotes** (renamed from "Market
   data") / **Dashboard**. "Verify balances" moved out of Bank feeds into General —
   it's a ledger-wide integrity sweep, not feed-specific.

5. **Register polish.** Bank register drops the 3-tile band (account name is in the
   breadcrumb, counts are on the filter tabs, currency is in row amounts). The
   always-on footer is hidden when idle (it surfaces only for an active selection or
   while loading). The investment portfolio band becomes a one-line `PortfolioBar`
   (Total · Portfolio · Cash · Unrealized) plus an Activity / Holdings view toggle
   (a quiet text link) — the per-security table lives in the Holdings view with its
   own scroll, so a long holdings list never pushes the register down. The
   investment register gains the bank's missing initial-error state, and its
   row badges now resolve status the way the bank does — a future-dated row
   shows the **S** (scheduled) badge instead of the raw persisted recon ring
   (the Scheduled tab count was already correct; only the badge lagged).
   `scrollbar-gutter: stable` on the main scroll pane removes the horizontal jump
   when content does/doesn't overflow.

6. **Conventions.** Dismissive buttons = `secondary`; destructive primaries =
   `danger` variant; form-field labels unify on `FieldLabel`'s size. Primary-action
   verbs match what the action does — the reminder occurrence dialog (which fires
   the occurrence as a real transaction) labels its button **Post**, not "Save",
   via an optional `submitLabel` override on the shared transaction editors.

## Consequences

- One overlay / empty-state / label / error vocabulary across the app; the Hub and
  sidebar share account-type rendering and can't diverge again.
- Register surfaces are denser; the investment portfolio detail is one click away
  rather than always-stacked.
- Rename-ledger and delete-ledger were visible-but-disabled stubs until their API
  endpoints existed; **now shipped** — `PATCH`/`DELETE /api/ledgers/{id}` (owner-only;
  delete via the complete-wipe `fn_ledger_delete`, migration 141), wired into
  Settings → General with a typed-name confirm. The landing page gained a
  **Create ledger** action so deleting your last ledger isn't a dead end.
- Non-form-label uses of the all-caps style (KPI captions, register column headers)
  intentionally keep their own treatment — `FieldLabel` is for form fields only.

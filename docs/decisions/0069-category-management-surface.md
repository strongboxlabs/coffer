# 0069 — Category management surface (Settings tab + full paths)

* Status: Accepted (Slice A shipped 0.10.0)
* Date: 2026-06-29
* Related: ADR-0017 (categories ARE accounts — the model this manages), ADR-0068
  (the merge / delete / reparent repo methods this exposes over REST — its D5
  anticipated this SPA consumer), ADR-0050 (account editor — create / rename reuse
  its endpoints), ADR-0030 (register row strategies — where full category paths now
  render), ADR-0043 (AccountCategoryPicker — reused for parent / target selection),
  ADR-0021 (the `/` path separator, mig 021)

## Context

Categories (`account_type='category'`, ADR-0017) were only reachable inline in the
transaction editor's counterparty picker — there was no surface to SEE the whole
income / expense hierarchy or curate it (rename, re-parent, merge duplicates left by
stacked imports, delete unused). ADR-0068 added merge / delete / reparent repo methods
but exposed them only over MCP. Two gaps for a non-AI user: (a) no REST / UI to manage
categories; (b) registers showed only a category's leaf name (`Groceries`), losing the
parent chain that disambiguates duplicate leaf names.

## Decisions

### D1 — Manage categories in Settings, not a new top-level surface
A "Categories" tab in per-ledger Settings (alongside General / Snapshots / …).
Categories are administrative metadata, not a daily-driver register, so Settings — where
the other per-ledger curation lives — is the right home.

> **Amended 0.13.0 (nav swap) — reversed.** Categories is promoted to a top-level nav
> destination (`/ledgers/$ledgerId/categories`, after Accounts); Activity (the
> provider-run timeline, ADR-0055) moves the other way — into a Settings tab. In practice
> categorization proved a daily-driver surface (curating the hierarchy + opening a
> category as a register, Slice B), while the activity log is occasional ops review with
> more affinity to Settings. The Settings tab is now URL-addressable (`?tab=`, so the
> Overview's "View activity" link deep-links to it). Slices A/B (`CategoriesPanel` + the
> category register) are reused unchanged — only the entry point moved.

### D2 — REST over the ADR-0068 repos; create / rename reuse the accounts endpoints
New `/api/ledgers/{id}/categories` endpoints: `GET` (list + hierarchy + usage), `PATCH
/{id}/parent` (reparent), `POST /{id}/merge`, `DELETE /{id}` — thin handlers over the
existing `AccountsRepository` methods (ADR-0068 D5: no new logic, same RLS + ledger-
visibility gate as the accounts endpoints). Create + rename go through the existing
accounts endpoints (ADR-0050) since a category IS an account; this surface only adds the
hierarchy operations. A new read `ListCategoriesWithUsageAsync` returns, per category,
the transaction-leg count, child count, and the signed leg-sum total — the counts mirror
the delete gate so the UI can pre-disable Delete while the server stays authoritative.

### D3 — Two kind-sectioned trees; right-click actions; reuse the picker
Income and Expense render as separate trees, each headed by a kind icon. Row actions
(Add sub-category / Rename / Move / Merge / Delete) are on **right-click**, matching the
registers + sidebar (the app-wide `ContextMenu` convention) — not a per-row kebab.
Parent and merge-target selection reuse `AccountCategoryPicker` (ADR-0043) — the same
type-ahead the transaction + loan editors use — rather than a parallel picker. Since
that picker has no null option, "Top level (no parent)" is an explicit checkbox beside
an always-visible picker.

### D4 — Delete is gated; merge is the escape hatch
`DELETE` succeeds only on a category with zero referencing legs and zero children
(server-enforced; the UI pre-disables via the usage counts). An in-use category can't be
deleted — the dialog explains and offers Merge instead. Merge repoints every leg +
reparents the source's children to the target, then **deactivates** (not deletes) the
source — reversible. Move / Merge exclude the node's own subtree (no cycles; the server
rejects them too).

### D5 — Per-kind sign normalization for displayed totals
A category's stored leg-sum is raw double-entry signed (expense nets positive, income
nets negative). The panel negates income for display so both read as positive magnitudes
under their section headers, and each section header shows the kind total. Negative zero
(`-0`, from negating a zero) is collapsed to `0` in the shared money formatters
(`lib/money.ts`) so `-$0.00` never renders anywhere — a genuine tiny negative keeps its
sign.

### D6 — Registers show the full parent→child path
Register category / counterparty chips render the full slash path (`Food/Groceries`, the
ADR-0021 / mig-021 separator) instead of the bare leaf, app-wide (bank txn + split-leg;
investment category / transfer / fee). Threaded via a new `accountPaths` field on the
register row context (`RegisterRowBodyCtx`), built once per page from the accounts list
with the existing `buildAccountPathMap`; a `displayAccountPath` helper does the id→path
lookup with a leaf-name fallback (so a path-less or not-yet-loaded account still renders).

## Scope

Slice A (this ADR): manage — everything above.

Slice B — the **category register** (open a category like an account to see / edit the
transactions posted to it, MD parity). The design pass found this needs **no new
register domain** (correcting this ADR's initial projection): `RegisterRouter` already
falls through to `BankRegisterPage` for every non-investment account type, categories
included; the register read (`RegisterRepository.GetPageAsync`) is keyed purely by
`account_id` with no type guard; `BankRow` already carries everything a category row
needs (counterparty = the source account, amount, date, and a running-total balance,
computed for categories too). So `/ledgers/{id}/accounts/{categoryId}` already renders a
category's register. Slice B is therefore just the **entry point**: each category name in
the manage tree links to that route. Reached from the tree and — since the 0.13.0 nav
swap (see D1) — the sidebar's Categories destination.

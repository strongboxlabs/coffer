# 0090 — Ledger management is not a system option, and breadcrumbs are not navigation

* Status: Accepted
* Date: 2026-08-03
* Relates: [ADR-0021](0021-ui-layout-and-principles.md) (sidebar shell + UI principles), [ADR-0060](0060-whole-db-backup-and-admin-role.md) (`/system`), [ADR-0069](0069-category-management-surface.md) (nav-swap amendment — tabs as URL state), [ADR-0088](0088-setup-asks-one-question.md) (the hub is the post-setup home)

## Context

Opening the gear landed on `/system` with a breadcrumb reading
**All ledgers / System**, and the only way back to the ledger list was clicking
that crumb.

Three separate problems, one of which was invented by the fix attempts and is
recorded here so it is not tried again.

**1. The breadcrumb asserted a parent that does not exist.** `/system` is a
*sibling* of `/` in the router — both children of `authedRoute` — and its scope
is the whole install. `SystemSettingsPage` hardcoded an `All ledgers` crumb
anyway. `/account/security` did the same, and it is per-*user* (passkeys,
recovery codes). Neither lives under the ledger list.

**2. There was no first-class route to ledger management.** `AuthedSidebar`
contained no link to `/`, and the "Coffer" wordmark was an inert `<span>`, so the
universal click-the-logo gesture did nothing. That left a breadcrumb crumb as the
only way to reach `/` — navigation smuggled into a location indicator.

**3. `All ledgers` was hardcoded in 11 page files** with no shared helper, so the
label and the implied hierarchy were re-decided per page. That is how `/system`
and `/account/security` drifted into claiming a parent in the first place.

`/` also called itself two different things at once: **"All ledgers"** in the
crumb and **"Your ledgers"** in the heading directly below it.

## Decision

**Ledger management belongs with the ledgers. Breadcrumbs state location only.**

1. **"Manage ledgers…" is an item in the ledger dropdown**, below the ledger
   list. `/` is the manage-ledgers surface — create, import, open — so its entry
   point sits in the control that already owns the ledger domain.

2. **`/` has one name.** "Manage ledgers" in the crumb *and* the heading,
   matching the dropdown item that leads there.

3. **No `All ledgers / …` root crumb, anywhere.** Removed from all 11 files.
   Ledger pages keep the real hierarchy (`Demo / Settings`); only the invented
   root goes. `/system` is just `System`; `/account/security` is just `Security`.

4. **The wordmark links to `/`.** Named by its visible text ("Coffer"), *not*
   `aria-label="All ledgers"` — that would give two links the same accessible
   name, indistinguishable to a screen reader.

5. **The no-ledger picker state is a real control.** It was an inert
   `<span>No ledger selected</span>`; it is now a button reading
   "Manage ledgers" that opens the same dropdown, so a fresh install with no
   ledgers still has a way in from the rail.

6. **The `/system` tab is URL state**, via the route's `validateSearch`, the same
   contract the per-ledger settings route has used since ADR-0069: absent or
   invalid → About, carried as a clean URL with no `?tab`.

## What was tried and rejected

**Hiding the ledger section of the rail on non-ledger pages.** The reasoning was
that the rail says "you are inside Demo" while `/system` says "deployment-wide
settings", so the ledger chrome should disappear. Three attempts, all worse than
the problem:

- Replacing it with an install-wide rail listing About / Backups / MCP / Users
  **duplicated the page's own tabs** — the same four items twice on one screen,
  beside a mostly-empty column.
- Blanking it left the user card floating up under the header (the footer is not
  pinned; it followed the collapsed nav) and left "Show inactive accounts" — a
  per-ledger account filter — sitting on a deployment page.
- Hiding the ledger *picker* along with it **stranded ledger management**:
  "Manage ledgers…" lives in that dropdown, so `/system` ended up with no route
  to ledgers at all.

The rail's `ledgerId` falls back to last-viewed, then first-visible, and
[AuthedSidebar.tsx](../../src/Web/src/components/AuthedSidebar.tsx) already
documented why: so "the destinations + account nav stay put" on non-ledger
surfaces. That is a deliberate trade-off — a stable rail over a strictly honest
one — and it was made before this ADR. **The rail is left exactly as it was.**
The complaints that started this (no first-class entry, nonsense breadcrumb) were
both fixable without touching it.

## Consequences

**A latent bug is fixed.** The System tab was component state seeded *only* from
`?tab=backups` (added for the Google Drive OAuth callback). Every other value
fell through to About, so `?tab=mcp` and `?tab=users` silently rendered the wrong
tab, and switching tabs left the URL stale — a reload or a shared link never
landed where you were. All four tabs now deep-link and survive refresh.

**Breadcrumbs are no longer load-bearing.** Anything reachable *only* via a crumb
is a navigation gap; the crumb is a location indicator that happens to be
clickable. Two entry points now cover `/`: the wordmark and the dropdown item.

**`All ledgers` is still hardcoded per page — just correctly.** This ADR removed
the wrong root from 11 files but did not introduce a shared breadcrumb helper. A
helper is the obvious follow-up if the hierarchy drifts again; it was out of
scope here because the fix was subtraction, not abstraction.

**Deliberately not fixed: the "Settings" / "System settings" name collision.**
The ledger rail's *Settings* and the gear's *System settings* are different
scopes one word apart. Renaming navigation people already know is a separate UX
call.

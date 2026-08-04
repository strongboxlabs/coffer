# 0077 — Tag management surface (Tags v1)

Status: Accepted
Date: 2026-07-15
Relates: [ADR-0009](0009-tags-at-txn-level.md), [ADR-0069](0069-category-management-surface.md), [ADR-0076](0076-register-filter-single-source-of-truth.md)

## Context

Tags existed only in fragments: the Moneydance importer unioned MD's
per-leg + per-txn tags into the `tags` table + `txn_header_tags` junction
(ADR-0009, header-level), the transaction editor could set them via a
free-text chip input, and the register filter accepted one exact tag
name. There was **no way to manage the dictionary** — rename a tag,
recolour it, merge two, delete one, or clear orphans — and no
autocomplete, so a typo minted a near-duplicate tag silently. The `tags`
table already carried an (unused) `color` column.

This is the tag analogue of the category-management surface (ADR-0069):
a per-ledger dictionary that was only reachable inline needs a management
home + a shared, discoverable picker.

## Decisions

### D1 — Scope: dictionary management + shared picker + coloured chips

A per-ledger `/api/ledgers/{id}/tags` REST surface (EF/LINQ repository,
no raw SQL) with: list-with-usage (`GET`), rename/recolour (`PATCH`),
merge (`POST …/{id}/merge`), delete (`DELETE …/{id}`), and cleanup-unused
(`DELETE …/tags/unused`). **Assigning** tags to a transaction is
unchanged — it stays on the transaction `PATCH` (ADR-0009), which already
does case-insensitive create-on-first-use. This ADR is dictionary admin
only.

The UI adds: a shared `TagCombobox` autocomplete (colour swatch + usage +
"Create '<new>'") replacing the free-text inputs in the editor and the
register filter; colour on the register's tag chips; and a Tags
management panel.

### D2 — Delete is allowed even in use (confirmed)

Unlike categories (which block a delete with references, ADR-0069),
deleting a tag is permitted while in use: the `txn_header_tags.tag_id` FK
is `ON DELETE CASCADE`, so the tag is removed from every transaction in
one statement (the transactions themselves are untouched). The UI shows a
confirm naming the usage count ("on N transactions; they'll be
untagged"). A tag is a label, not structure — losing it loses no
transaction data, so the friction of a mandatory merge-first isn't
warranted.

### D3 — Rename-to-existing offers a merge

A rename whose new name matches another tag in the ledger
(case-insensitive) is rejected `422 tag-name-exists`; the edit dialog
then offers "Merge '<source>' into '<existing>' instead". A case-only
self-rename (`work` → `Work`) is allowed. Tag names are matched
case-insensitively everywhere (assignment's resolve and this rename
check), so **no writer can mint a case-only duplicate** — the DB unique
index stays `(ledger_id, name)` (case-sensitive) with no migration
needed, because the application layer is the effective guard.

### D4 — Colours: gray default + fixed palette, set-only

A tag with `color = NULL` renders as the theme's default gray. The picker
offers a fixed 10-swatch palette (`src/Web/src/lib/tagPalette.ts`).
Recolour is **set-only in v1** — there's no "clear back to gray" action,
because `PATCH` treats an absent colour as "unchanged" and we didn't add
an absent-vs-null sentinel. The server validates the `#rrggbb` *shape*
(not palette membership, so the palette can evolve) and stores it
lower-cased. Register chips are coloured **client-side**: the resolved
view is unchanged (ADR-0076 keeps one filter definition and no view
churn), so a `TagColorsProvider` joins tag name → colour from the shared
`['tags', ledgerId]` query and a `TagChip` tints each chip. Investment
register rows aggregate per holding and don't render header-level tags, so
coloured chips are a bank-register concern only.

### D5 — Orphan cleanup is manual

Removing a tag from a transaction leaves the tag row in the dictionary
(it may be used elsewhere; and re-adding it should reuse the same row +
colour). The panel surfaces a "Remove N unused" action that deletes every
zero-usage tag on demand. No automatic/on-write cleanup.

### D6 — Filter stays single-tag

The register tag filter remains a single exact-match dimension, now with
autocomplete (existing tags only — no "create"). Multi-tag (`ANY`/`ALL`)
and an "untagged" filter are deferred (follow-ups).

### D7 — Placement: a top-level destination, not a Settings tab

Tags is a top-level per-ledger nav destination (`/ledgers/{id}/tags`,
`TagsPage` → reused `TagsPanel`), sitting beside Categories in the
sidebar. It mirrors Categories, which itself graduated from a Settings tab
to a top-level destination in the ADR-0069 nav swap, and the other
dictionary/destination pages (Securities, Reminders). The panel component
is placement-agnostic, so moving it into Settings later is a one-line
change.

## Consequences

- **No migration.** The `tags` table, `txn_header_tags` junction (with
  the cascade FK), and the `color` column all pre-existed; `TagRow.Color`
  became mutable for the recolour path.
- The Tags dictionary is now the shared `['tags', ledgerId]` query behind
  the editor autocomplete, the filter autocomplete, the chip colours, and
  the management panel — one fetch, one cache, and a mutation there
  repaints all of them.
- **Deferred:** bulk-tag from the register selection (still blocked on the
  Phase 6 override-write surface), multi-tag / untagged filtering, and
  clear-colour-to-gray. Tracked in `docs/follow-ups.md`.

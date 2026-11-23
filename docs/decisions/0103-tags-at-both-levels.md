# 0103 — Tags at both levels: the transaction keeps its own, the posting gets its own

* Status: Accepted (amends 0009; amended 2026-10-27 by [ADR-0108](0108-memo-and-tags-per-side.md): tags per side, filters follow the row's perspective)
* Date: 2026-09-30

## Context

[ADR-0009](0009-tags-at-txn-level.md) put tags at transaction level and rejected
split-level tags while no split-level pattern needed them.

A payment split across two projects, each line tagged with its project, needs them. One header, two postings, and the only question the tags exist to answer — how
much went to Project B — is the one the current model cannot answer.

**Before this ADR the importer discarded the distinction.**
[`TransactionMapper.ExtractTags`](../../src/Importer.Moneydance/Mappers/TransactionMapper.cs)
unioned the transaction's own `tags` with every split's `tags` into one flat set and
attached it to the header. Moneydance records the distinction; import discarded it.
ADR-0009 accepted that as the cost of a simpler model, a judgement the case above
overturns.

**The deferral was never in the ADR trail.** ADR-0025's "Open questions" reads "None",
and ADR-0009 stands unamended, so the only written record that per-leg tags were
deliberately postponed is one line in
[`bank-edit/README.md`](../../src/Web/src/routes/ledgers/bank-edit/README.md#L140).
A decision recorded only in a component README is a decision the next person rediscovers
by reading component code. This ADR closes that gap as much as it decides anything.

The editor rendered the promise in the meantime: `TagsPlaceholder` reserved a
compact chip beside each leg's category picker, kept deliberately at 30px a row so that
adding real tag editing does not reshuffle the form.

## Decision

### D1 — Both levels, and they mean different things

`txn_header_tags` is unchanged. A new `txn_leg_tags` carries per-posting tags.

A header tag is a statement about the transaction — *Holiday*, *Tax 2026*, *Reimburse
me*. A leg tag is a statement about one posting — *Project B*. These are not the same
claim at different resolutions; they are different claims, and collapsing them in
either direction loses information that the user put there on purpose.

This is **not** the union model ADR-0009 named, and the reason is what the union model
does to a transaction tag: it stops being a fact and becomes a derived artifact of its
legs. Add a leg later and the transaction's tags silently change. Every existing header
tag has to be fanned across its legs, rewriting data that is correct today. ADR-0009's
own objection — "adds query complexity everywhere" — was accurate: every read that
wants transaction tags would have to aggregate to get them.

The cost of two levels is that every question about tags now has to say which level it
means. D5 is the one place that deliberately refuses to.

### D2 — The schema is forced, not designed

`txn_leg_tags` follows [migration 072](../../db/migrations/072_rls_flatten_remaining_child_tables.sql)
exactly, because three things fail closed otherwise:

| element | why it is not a choice |
|---|---|
| `ledger_id UUID NOT NULL` | `fn_snapshot_write_part` chunks on a generic `WHERE t.ledger_id = $1`. A snapshot-captured table without that column cannot be captured at all. |
| FK `(leg_id, ledger_id)` → `txn_legs(id, ledger_id)` | Composite, `ON DELETE CASCADE`. This is what makes the denormalized `ledger_id` provably equal to the parent's, which is the entire justification for the flat RLS predicate below. |
| FK `(tag_id, ledger_id)` → `tags(id, ledger_id)` | Same, on the other parent. A leg in one ledger cannot be tagged from another's vocabulary. |
| RLS: `ledger_id IN (SELECT ledger_id FROM user_ledger_grants WHERE user_id = current_app_user_id())`, `FOR ALL`, repeated in `USING` and `WITH CHECK` | Migration 072's flattened form, the same one `txn_header_tags` has carried since. |

Both parents already have the composite `UNIQUE (id, ledger_id)` these FKs need —
`txn_legs` from migration 049, `tags` from migration 072 — so no parent changes.

### D3 — The snapshot decision lands in the same migration

`SchemaDriftGuardTests.Snapshot_payload_classifies_every_ledger_scoped_table` fails the
moment a table with `ledger_id` exists and is neither captured nor excluded. That is
the gate, and it is the right one: it makes "we will decide the snapshot later"
impossible.

`txn_leg_tags` **is** ledger data and is captured. Six function bodies change:
`fn_ledger_snapshot_part_names`, `fn_ledger_snapshot_insert_order` (after both
`txn_legs` and `tags`), `fn_ledger_snapshot_payload`, `fn_ledger_snapshot_restore`,
`fn_ledger_snapshot_clear`, and `fn_ledger_delete`.

### D4 — The register row still shows only the transaction's own tags

A transaction row shows the transaction's tags. Leg tags appear on the leg, where the
posting they describe is.

Showing the union on the row would undo D1 in the one place most people look. A reader
would see *Project B* on a row and reasonably conclude the transaction is about
Project B.

What the row shows is unchanged; **how** it shows it is not — see D7.

### D5 — Filtering spans both levels

"Show me everything tagged Project B" matches a transaction if the tag is on the
header **or** on any leg. Someone asking where money went does not care which level
recorded it, and a filter that matched only headers would leave leg tags
undiscoverable — which is most of what they are for.

This is the deliberate exception to D1, and it is safe in the direction it errs: the
filter over-includes rather than hiding.

### D6 — The importer is fixed here; the installs that already imported are not

`ExtractTags` stops collapsing: a transaction's own tags go to the header, each split's
go to its leg. Anything imported after this ships is attributed correctly, which is what
makes the Moneydance side of this item done.

An install that imported **before** this ships keeps its collapsed tags, and there is no
second import to fix them — Moneydance import is a one-shot bootstrap, so the only route
is a backfill over `provider_raw_payload`. That is deliberately out of scope here, and
not because it is small: the honest version DELETES tags, since a tag that reached the
header only because one split carried it does not belong on the header under D1.
Removing it changes data someone may already filter on. It was tracked as FU-050, now closed
(the closed-work log).

### D7 — One control on every tag surface, and it is not chips

Chips wrap. That makes row height a function of how many tags a transaction carries and
how long their names are, which is survivable on a full-width header and fatal in a 30px
leg row — that row's fixed height is what lets the split list scroll instead of the
editor growing. It is also not a tail case: the write surfaces accept up to 20 tags on a
transaction (`TagValidation`) and names up to 64 characters (`TagName.MaxLength`), so a
row of chips can wrap to several lines, and the amount column stops lining up, which is
the only reading pattern a register has.

So the same control serves the header, the posting, and the investment event, in two
parts:

* **Resting** — a comma-separated line, truncated, with the TOTAL count beside it and
  every name on the title. The count is not "+N hidden": how many names fit depends on
  the column's pixel width, which a component rendered on every register row cannot know
  without measuring, and a "+2" that is really "+4" is worse than no number.
* **Editing** — a dropdown of the ledger's vocabulary with CHECKBOXES, filter focused on
  open, and create at the foot. Square boxes, because round ones say "pick one". No
  "select all" row: that is a filter idiom, and here it would mean tagging one posting
  with the entire vocabulary.

The strip and the button that opens the panel are **separate targets**. Clicking the
strip must not open the editor, or a value too long for the field cannot be read. That
is also why the caption beside it on an editor surface is a plain `FieldLabel` pointing
at the button by `htmlFor`, and not a `<label>` WRAPPING the control: a wrapping label
forwards a click from anywhere inside it, which would hand the strip the behaviour this
paragraph just denied it.

**The control is a field, and is sized like the fields around it.** 28px on an editor
surface, matching the date, payee, category and amount inputs; 24px only under
`compact`, in the splits grid, whose leg rows are 30px and have no room for more. At
24px everywhere it reads as something dropped into the form rather than laid out in it,
and the fields only agree when every one of them, payee and category included, takes
the same height. Padding matches too: `px-2`, so the tag text begins where the
category's does directly above it.

The panel is wider than the control it hangs off — a name near the 64-character cap has
to be legible — and it is **fixed to the viewport, always opening downward, closing on any
scroll outside itself**.

Each of those three rules out a placement that fails.

*Fixed, not absolute.* An absolutely positioned panel stays inside the scroller, whose
argument is that a dropdown can then never be visible while its own input is not. The
argument is sound; the mechanism is not. An absolutely positioned child counts toward
its scroller's scrollable content, so opening the panel from the last leg of a long
split transaction GROWS the grid's scroll range and leaves a dead band under "Add
split". It is also clipped by that scroller, and it loses a stacking fight with the
grid's sticky header and footer — both `z-20` — and renders sliced in half. Closing on
scroll keeps the guarantee without any of this: the panel cannot outlive the view it
was opened from.

*Always down.* Choosing a side by whichever has more room flips every control past the
window's midpoint, so two adjacent legs in one grid open opposite ways. "Slightly more
space the other way" is not a reason to surprise the reader. The panel opens upward only
when downward genuinely will not fit a minimum list, and then it anchors its BOTTOM to
the control's top — never `top = control.top − estimatedHeight`, which guesses a full
list and a Create row and leaves short panels floating two rows above their control.

*Not every scroll.* The close listener is on `window` in the CAPTURE phase, because the
scroll that matters happens on an ancestor and scroll events do not bubble. That also
catches the panel's own list, which would close it mid-drag on its own scrollbar, so
scrolls originating inside the control are ignored. The listener is attached one frame late
for the same class of reason — focusing the filter can itself scroll an ancestor, and
the panel would close on the scroll that opening it caused.

`AccountCategoryPicker` uses this placement too, so the two controls in a leg row behave
alike. With a fixed panel nothing needs to scroll the row to the top of its viewport to
make room, which would read as the whole grid lurching when you click a field. Its
`compact` prop is not load-bearing — just a shorter ceiling for a container where a tall
list overwhelms the row.

Colour survives as a **2px vertical rule** before each name, in the list as well as the
line. Not as text colour: the palette hexes are chip FILLS, paired with black or white
by `readableOn`, and nothing makes them legible as foreground — slate `#64748b` on the
dark theme's surface is about 2:1. Vertical rather than horizontal because a horizontal
bar costs 7px plus its margin per tag, which at seven tags is most of a chip's width in a
field with about 150px to give, and because inside a comma list it reads as a hyphen.

A tag with no colour gets no rule, rather than a grey stand-in that would claim it had
one.

## Consequences

Purely additive in the DATA. No existing tag row moves and no snapshot payload is
rewritten. The register does not look identical, though — D7 replaced the chips there,
which is a visible change for every ledger, tagged postings or not. That is the main thing
this shape buys over the union model, and it is why the migration carries no data
rewrite to get wrong.

`TagsPlaceholder` and `TagsInput` are both gone, replaced by `TagSelect` (the control)
and `TagLine` (its read-only half, which the register uses on its own). The slot in the
leg row was reserved at a fixed width for exactly this, so landing it did not reshuffle
the form.

Both follow-ups FU-004 names do resolve: the inert placeholder, and the importer's
per-leg tag collapse. What does not follow is the history — an install that imported
before this shipped keeps its collapsed tags, which is an upgrade concern rather than an
importer one, and was FU-050, now closed
(the closed-work log).

Two levels means two places to look, and the burden lands on whoever writes the next
tag-shaped feature: reports, the MCP tag tools, and bulk tagging each have to state
which level they operate on. D5 is the precedent for the answer when the honest answer
is "both".

## Alternatives considered

**Leg-only, transaction tags derived as the union.** ADR-0009's own named path, and the
faithful model of what Moneydance stores. Rejected on what it does to an existing
transaction tag: it becomes derived, so adding a leg later changes it with nothing
saying so, and every header tag in every install has to be fanned across its legs —
a rewrite of correct data to reach a model that is not more correct, only more uniform.

**Leg-only, with no transaction-level view at all.** The simplest schema and the
simplest queries. Rejected: ADR-0009 established that people attach tags to
transactions because that is how they think about them, and this strands every existing
header tag with nowhere to land.

**Delete the placeholder and close the item.** The honest move while no ADR is
scheduled — shipping a permanent "coming soon" is worse than shipping neither. Rejected
because this ADR builds the feature.

## Amendment, 2026-10-27 — tags per side (ADR-0108)

ADR-0108 places every memo and tag set on a side of the transaction. Both levels stay, and
D2, D3 and D7's control stand; these statements above no longer hold:

- **D1, "`txn_header_tags` is unchanged."** Header tags exist only on a transaction or
  event with several postings (ADR-0108 D1). A one-posting transaction's tags are per
  side, on its legs; 264 Step C moved existing ones there (ADR-0108 D11).
- **D2, the snapshot row.** `fn_snapshot_write_part` filters each captured table on
  `ledger_id` and keyset-chunks it on its primary key (migration 264; ADR-0108 D10).
- **D4, "The register row still shows only the transaction's own tags."** A row shows its
  stored presentation (ADR-0108 D1, D5). On a one-split, a side shows its own tags, else
  the other side's. On the side a split belongs to, the transaction row shows the header's
  tags and each line its own. On a line's opposite side, the line shows its own leg's
  tags, else its other leg's, and never the header's. An investment event shows one set,
  on its brokerage leg (one posting) or its header (several).
- **D5, "Filtering spans both levels."** Replaced by ADR-0108 D2: a filter matches only
  the tags in play for the row's own leg, which are exactly what the row shows
  (`RegisterQuery`); on the split side those are the header's together with the line's.
  Reports take the real account's perspective. The filter no longer over-includes.
- **D6, the importer and the earlier installs.** The Moneydance import places tags per
  side through the write primitive (`MoneydanceNotes`): on a one-split the home leg gets
  the split's tags and the other leg the transaction's; on a split each line's home leg
  gets its own joined with the transaction's set, because a line's other side is read-only;
  an event's tags land on its editable side. The installs that imported earlier are no
  longer out of scope: 264's data steps re-derive every Moneydance row still as an
  importer vintage wrote it from its `provider_raw_payload`, and leave an edited row as
  the user left it (ADR-0108 D11). FU-050 is settled by that and closed
  (the closed-work log).
- **D7, "accept up to 20 tags on a transaction (`TagValidation`)".** The limit is per set,
  header or leg (`AnnotationPlanner.MaxTagsPerSet`), and only growing a set past it is
  refused; a system merge may exceed it, and nothing is dropped (ADR-0108 D12).
  `TagValidation` checks names only.
- **Consequences, "D5 is the precedent".** The precedent for which level a feature reads
  is ADR-0108 D2's perspective rule: what the row shows in the register it is read from,
  and in a report the real account's view.

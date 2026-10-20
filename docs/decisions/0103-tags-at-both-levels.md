# 0103 — Tags at both levels: the transaction keeps its own, the posting gets its own

* Status: Accepted (amends 0009)
* Date: 2026-09-30

## Context

[ADR-0009](0009-tags-at-txn-level.md) put tags at transaction level and wrote its own
reopening condition into its alternatives:

> **Tag splits, derive transaction-level tags as the union.** Faithful to Moneydance
> but adds query complexity everywhere. *Rejected unless real use surfaces a
> split-level tag pattern that matters.*

A rental property split across three units is that pattern. One header, three
postings, and the only question the tags exist to answer — how much went to Property B
— is the one the current model cannot answer.

**The loss is live, not hypothetical.**
[`TransactionMapper.ExtractTags`](../../src/Importer.Moneydance/Mappers/TransactionMapper.cs#L245)
unions the transaction's own `tags` with every split's `tags` into one flat set and
attaches it to the header. Moneydance records the distinction; import discards it.
ADR-0009 called that "rare in practice and acceptable", which was true of the data it
was written against and is not true of the case above.

**The deferral was never in the ADR trail.** ADR-0025's "Open questions" reads "None",
and ADR-0009 stands unamended, so the only written record that per-leg tags were
deliberately postponed is one line in
[`bank-edit/README.md`](../../src/Web/src/routes/ledgers/bank-edit/README.md#L140).
A decision recorded only in a component README is a decision the next person rediscovers
by reading component code. This ADR closes that gap as much as it decides anything.

The editor has been rendering the promise in the meantime: `TagsPlaceholder` reserves a
compact chip beside each leg's category picker, kept deliberately at 30px a row so that
adding real tag editing does not reshuffle the form.

## Decision

### D1 — Both levels, and they mean different things

`txn_header_tags` is unchanged. A new `txn_leg_tags` carries per-posting tags.

A header tag is a statement about the transaction — *Holiday*, *Tax 2026*, *Reimburse
me*. A leg tag is a statement about one posting — *Property B*. These are not the same
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
would see *Property B* on a row and reasonably conclude the transaction is about
Property B.

What the row shows is unchanged; **how** it shows it is not — see D7. The original
wording here said the row was unchanged outright, which stopped being true the moment
the chips came off it.

### D5 — Filtering spans both levels

"Show me everything tagged Property B" matches a transaction if the tag is on the
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
Removing it changes data someone may have been filtering on for months. It is tracked as
FU-050 and wants measuring on a real imported ledger before anyone commits to a shape.

### D7 — One control on every tag surface, and it is not chips

Chips wrap. That makes row height a function of how many tags a transaction carries and
how long their names are, which is survivable on a full-width header and fatal in a 30px
leg row — that row's fixed height is what lets the split list scroll instead of the
editor growing. It is also not a tail case: Moneydance users tag heavily and names run
to 50 characters. A register row with eight tags rendered three lines tall, and the
amount column stopped lining up, which is the only reading pattern a register has.

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
`compact`, in the splits grid, whose leg rows are 30px and have no room for more. It
first shipped at 24px everywhere, which read as something dropped into the form rather
than laid out in it — and the same pass found the payee at 32px and the category at an
accidental ~26px derived from padding, so "the tag control is the odd one" was really
"nothing here agreed". Padding matches too: `px-2`, so the tag text begins where the
category's does directly above it.

The panel is wider than the control it hangs off — a 50-character name has to be
legible — and it is **fixed to the viewport, always opening downward, closing on any
scroll outside itself**.

Each of those three is a correction of something that was tried first.

*Fixed, not absolute.* The panel began as `position: absolute`, following the category
picker beside it, whose argument for staying inside the scroller was that a dropdown can
then never be visible while its own input is not. The argument is sound; the mechanism
was not. An absolutely positioned child counts toward its scroller's scrollable content,
so opening the panel from the last leg of a long split transaction GREW the grid's scroll
range and left a dead band under "Add split". It was also clipped by that scroller, and
it lost a stacking fight with the grid's sticky header and footer — both `z-20` — and
rendered sliced in half. Closing on scroll keeps the guarantee without any of this: the
panel cannot outlive the view it was opened from.

*Always down.* Choosing a side by whichever had more room flipped every control past the
window's midpoint, so two adjacent legs in one grid opened opposite ways. "Slightly more
space the other way" is not a reason to surprise the reader. The panel opens upward only
when downward genuinely will not fit a minimum list, and then it anchors its BOTTOM to
the control's top — never `top = control.top − estimatedHeight`, which guessed a full
list and a Create row and left short panels floating two rows above their control.

*Not every scroll.* The close listener is on `window` in the CAPTURE phase, because the
scroll that matters happens on an ancestor and scroll events do not bubble. That also
catches the panel's own list, so dragging its scrollbar closed it mid-drag; scrolls
originating inside the control are now ignored. The listener is attached one frame late
for the same class of reason — focusing the filter can itself scroll an ancestor, and
the panel would close on the scroll that opening it caused.

`AccountCategoryPicker` was moved onto this placement too, so the two controls in a leg
row behave alike. Its `onOpenChange` prop went with the change: its only purpose was to
let `SplitsGrid` scroll the row to the top of its viewport to make room, which with a
fixed panel is unnecessary and reads as the whole grid lurching when you click a field.
Its `compact` prop survives, no longer load-bearing — just a shorter ceiling for a
container where a tall list overwhelms the row.

Colour survives as a **2px vertical rule** before each name, in the list as well as the
line. Not as text colour: the palette hexes are chip FILLS, paired with black or white
by `readableOn`, and nothing makes them legible as foreground — slate `#64748b` on the
dark theme's surface is about 2:1. Vertical rather than horizontal because a horizontal
bar cost 7px plus its margin per tag, which at seven tags is most of a chip's width in a
field with about 150px to give, and because inside a comma list it read as a hyphen.

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
importer one, and is FU-050.

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

**Delete the placeholder and close the item.** The standing recommendation until the
2026-08-28 ranking, and the honest move while no ADR was scheduled — shipping a
permanent "coming soon" is worse than shipping neither. The ranking reversed it.

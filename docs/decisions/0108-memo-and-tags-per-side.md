# 0108 — Memo and tags per side: each side keeps its own, and what a row shows is stored when it is written

* Status: Accepted (amends 0009, 0022, 0024, 0025, 0028, 0036, 0077, 0080, 0103); D6
  amended 2026-11-05 by [0112](0112-one-write-lock-per-ledger.md) (a save that recomputes
  balances or holdings runs in its writer's own transaction, opened with the ledger write
  lock; the fallback transaction serves saves that recompute neither); D6 amended
  2026-11-18 by [0072](0072-recover-misfiled-transactions.md) D3 (the single-row move
  route is removed, and a move of one row is a bulk move of one)
* Date: 2026-10-27
* Related: [0009](0009-tags-at-txn-level.md) and [0103](0103-tags-at-both-levels.md) (where
  tags live), [0022](0022-txn-headers-and-legs.md) (headers and legs),
  [0032](0032-triggers-as-last-resort.md) (no trigger),
  [0036](0036-originating-vs-target-register-entries.md) (which side owns a row),
  [0037](0037-snapshots-and-backups.md) (snapshot capture and the schema-version posture),
  [0060](0060-whole-db-backup-and-admin-role.md) (whole-database restore),
  [0077](0077-tag-management-surface.md) (tag merge, rename and delete),
  [0081](0081-mcp-write-control-plane.md) (`set_transaction_tags`).

## Context

A transaction has two sides, and before this release they shared one memo and one tag set.
The header held both; a register showed a leg's own memo, else the header's
(`resolved_transactions`), and on a one-posting row the header's tags. That model had
four defects:

- **One side's note overwrote the other's.** A transfer between two accounts carries what
  is said about it from each of them. With one memo, whoever saved last decided what both
  registers said. Moneydance keeps the two apart (a one-split's opposite side has its own
  memo and tags), so an import had to collapse them.
- **The side was guessed.** Which account a transaction belonged to was inferred from
  posting counts. That cannot tell the two sides of a one-split apart, nor the two sides
  of a split whose every line goes to one category, so "which side may edit this" had no
  stored answer.
- **Readers disagreed.** The register, its filter function, reports, search and MCP each
  derived what a row shows in their own SQL. A filter could match a tag the row did not
  show (0103 D5 over-included by design), and a one-posting leg's own tags were filtered on
  but not shown on its row.
- **Tags that differed only by case were different tags.** Two spellings of one name were
  two rows, matched by several folding paths that did not agree with each other or with the
  importer.

## Decision

### D1 — Each side keeps its own; the header exists only where there are lines

- **One-split** (one posting, two legs): the header never carries a memo or tags. Each
  side's leg holds its own. A side with nothing of its own shows the other side's,
  **inherited**, until it writes its own. Whoever enters the transaction fills their leg.
  Blank always means inherit; there is no explicit "none", as in Moneydance.
- **Multi-split, split side** (the account on every line): the header carries the
  transaction's memo and tags, shown on the transaction row. Each line shows its own
  value; a line with none stays blank and does not fall back to the header.
- **Multi-split, opposite side** (each line's other account): a one-split view of that
  line. It shows its own leg's value, else that line's other leg's. The header is never
  in play there.
- **Investment events** show one memo and one tag set per event. A one-posting event (an
  Xfr) follows the one-split rule, with the value on the brokerage leg. A multi-posting
  event (a buy with a commission) keeps it on the header. Legs on the event's investment
  side (the brokerage and its holdings account; for an in-kind transfer every brokerage on
  it and theirs) never carry a value. An opposite row whose other leg is on the investment
  side inherits the header.
- **Read-only rows** (a split line seen from its opposite side, an event's counterpart
  rows) only display. Edit and Duplicate are not offered on them. Delete is not offered
  either, as before this release: on a read-only row both registers' row menus offer only
  Show other side and Show raw data. Bulk actions on them follow D14.
- **A header with no legs** (a reminder template with no postings) has no side, so no
  register shows it and the rule places nothing: the header keeps its own memo and tags.
  The upgrade leaves them there (Step 0 still clears a blank memo), and the reminder's
  list, agenda and editor show them as written. The editor opens with them in its fields,
  so a save does not clear them.

The rule is written once, in C# (`MemoTagRules` in `Coffer.Domain.Annotations`), and
every reader uses its result (D5). Every write goes through one primitive,
`AnnotationPlanner`, persisted by `AnnotationWriter`.

### D2 — A row filters and reports on exactly what it shows

- **Filters and search** match what the register shows for the row and nothing else. A
  category register filters on its own perspective; the split side filters on the header
  together with each line. A filter never matches something the row does not show.
- **Reports** take the real account's perspective. A transaction is evaluated as the real
  account's register shows it; categories and tags are filters and groupings. On a
  multi-split the transaction's tags count for every line's money, and a line's own tags
  count for its line. A category-side override is shown and filtered in that category's
  register only and never counts in a report: counting the union would include entries the
  real account does not show.

This replaces 0103 D5's "header or any leg" filter, which over-included by design.

### D3 — The originating account is stored

`txn_headers.originating_account_id` (migration 263) names the account a transaction was
entered from: a one-split's entering side, a multi-split's split side, an event's
brokerage (an in-kind transfer's source brokerage). It is NULL exactly when the header has
no legs.

- **A reshape hands it over.** Splitting a one-split from side X makes X the owner.
  Setting an action in the brokerage register makes the brokerage the owner. Collapsing
  keeps the owner; only the split side can collapse. A move, recategorize or merge that
  leaves no leg on the owner hands it to the account the legs moved to.
- **Ingested rows** belong to the account they were imported into. Once in the ledger,
  feed rows and hand-entered rows follow the same rules.
- **The backfill** derives the owner from the current shape first (the brokerage on every
  posting of an event, the account on every line of a multi-split), then narrows the
  candidates by a fixed chain: the Moneydance home account, the reminder's source, a real
  account over a category, the outflow side, the lower account id. The chain exists in
  263 only and is never a runtime rule. A wrong pick on an ambiguous row cannot change what
  a register shows; it shows in the editor caption and decides which side may edit a
  symmetric split.

### D4 — A change of shape moves the notes and changes nothing a register shows

Except for the lines the user removes:

- **Split** (one-split to multi-split, from side X): what X showed is copied to the header
  and to line 1's X leg; the other side's own value stays on line 1's opposite leg; new
  lines start blank. Afterwards the copies are independent.
- **Delete a line** from a split that keeps two or more: the line and its values go;
  nothing else changes.
- **Collapse to one line**: X's memo becomes the header memo joined with the line's
  ("header · line") when they differ, else whichever exists; its tags are the union. The
  header is cleared in the same write. The remaining line's opposite side, if it has no
  value of its own, inherits the collapsed value: the one accepted exception to this
  section's rule, as a one-split always inherits. The editor shows the joined memo and
  merged tags in its fields before Save; there is no separate notice.
- **An investment action change that changes the posting count** moves the event's value
  between the brokerage leg and the header. An opposite side's own value survives if its
  leg survives. Values on legs that end up on the event's investment side join the event's
  value, with every memo merged onto it joined at once by the event rule (D11, Step D):
  each distinct memo once.

### D5 — What the rule produces is stored when a writer changes an input

The rule runs at **write time** over every leg of a header at once (`StoredNotes.Compute`).
Its output is stored:

- **Ten columns on `txn_legs`** (`notes_kind`, `notes_is_read_only`,
  `notes_groups_by_header`, `notes_memo`, `notes_memo_inherited`, `notes_inherited_memo`,
  `notes_inherit_from_account_id`, `notes_tags_inherited`, `notes_header_memo`,
  `notes_header_in_play`): the leg as its own account's register presents it. The check
  `ck_txn_legs_notes_whole` makes a leg either fully unfilled or fully filled. An unfilled
  leg is a state no writer leaves behind, and readers do not tolerate it.
- **`txn_leg_note_tags`**, one row per (leg, tag) with the roles the tag plays: `shown`,
  `in_play` (filters and reports), `inherited`, `transaction_level`. Tags are stored by id,
  so a rename touches nothing. Its `ledger_id` and composite foreign keys follow
  `txn_leg_tags`; members read and owners and editors write. `coffer_app` has `SELECT`,
  `INSERT` and `DELETE` only: a row whose roles change is deleted and inserted again.

Readers read plain columns. `resolved_transactions` exposes the ten columns for the row's
own leg and, as `counterparty_notes_*`, for the posting's other leg, from the two leg rows
it already joins. Migration 266 drops the view's old memo and tag columns and the
register's SQL filter and entry-key functions, which were a second definition of what a
row shows.

The inputs are the header's memo, action, originating account and tags; every leg's
account, posting, memo and tags; which legs exist; and the holdings link of the brokerages
involved. Nothing else.

### D6 — The recompute commits with the write

- **Tracked saves** reach the recompute through `LegNotesRecomputeInterceptor`, with no
  call in the writer. It reads the touched headers off the change tracker before the save
  (including a deleted row's original values and every header a deleted tag was on) and
  recomputes in the save's transaction. When the caller holds no transaction it opens one
  and commits after every other interceptor's work, so the balance, holdings and
  trade-price recomputes commit or roll back with the save too. It is registered last. A
  change to a brokerage's holdings link recomputes every header with a leg on the account or
  owned by it. (Amended 2026-11-05, below: a save that recomputes balances or holdings
  brings its own transaction, opened with the ledger write lock.)
- **Set-based writes** (the move, category merge, tag merge and delete) call
  `LegNotes.RecomputeAsync` for the headers they touched, in their own transaction.
  (Amended 2026-11-18: the single-row move route is removed, and a move of one row is a
  bulk move of one, ADR-0072 D3.)
- **The Moneydance import and demo provisioning** recompute the whole ledger inside the
  import's transaction, through its `beforeCommit` hook (`LegNotes.RecomputeLedgerOnAsync`),
  so no reader sees an imported ledger without its stored values.
- **Locks.** A recompute locks its headers in id order (`fn_lock_txn_headers`) before it
  reads the inputs, so two recomputes of one header run one after the other and two over
  overlapping sets cannot deadlock. A writer that reads which headers carry a tag, or have
  a leg on an account, before changing that tag or account locks it first (`fn_lock_tags`,
  `fn_lock_accounts`), so a link or leg added in between waits. All three refuse any
  isolation level but READ COMMITTED: reading after the lock sees the latest committed rows
  only there.
- **Only what changed is written**: a leg's columns where they differ, tag rows by
  difference.
- **No SQL twin and no trigger.** No SQL function computes the rule (0032). Only `LegNotes`
  writes the stored values, apart from the upgrade's backfill (D11) and the verbatim restore
  copy (D10).

### D7 — New legs are inserted with their stored values

Every tracked create stamps its new legs before the insert:
`AnnotationWriter.ApplyManyAsync` calls `StampNewLegsAsync`, which builds the header's
inputs from the plan as the save will store them and sets the legs' insert-only `notes_*`
properties through `LegNotes.Stamp`. The recompute after the save then finds nothing to
change on those legs and writes only their tag rows, so a new leg is not inserted and then
updated. This covers bank and investment creates, reminder template creates, the auto-post
clone, posting a reminder by hand, file and feed ingest, and the new legs of a reshape. The
Moneydance import inserts through Dapper and is filled by its whole-ledger recompute.

**Production never refuses a save over a missing or stale stamp.** The recompute in the
same save writes the right values either way. That every write path stamps, and that what
it stamps equals what the rule computes, is held where it costs a user nothing: by a
fail-closed guard over every write path in the test suite, which CI runs, and in
production by the monitor's `stored_notes` check (D9).

### D8 — Fail-closed guards

| Guard | Fails on |
|---|---|
| `StoredNotesWriteCatalogTests` | The rule's input records, the columns the recompute reads, the columns the interceptor watches and the backfill's projection disagreeing; an input entity type the interceptor does not handle; a set-based write or mapped database function that changes an input with no recompute after it; an import call that does not compute in its transaction; a migration from 264 on that changes an input without a recompute step after it; anything but `LegNotes` writing the stored columns or tag rows |
| `AnnotationReaderCatalogTests` | A read of a raw memo or tag link outside the rule's inputs, the writers' own values for an edit, and the tag dictionary; a model other than the header, leg and tag-link entities and the backfill's frozen projection mapping a raw memo column or tag link table |
| `AnnotationWriteCatalogTests` | A memo, tag pairing or originating account written outside the write primitive, or a name resolved to a tag without `tag_name_key`; a memo or the originating account with a setter outside its entity; in the Moneydance importer, notes placed anywhere but `ImportAnnotations.Stamp` and its persistence methods, or a header or leg row built outside the listed mappers or without its notes as null |
| `AnnotationSqlCatalogTests` | A database function or view whose SQL names a raw memo or tag link table, other than the snapshot's capture, verbatim restore and clear and the ledger delete; a memo column or transaction tag table the guards do not catalogue, or catalogued in a role that disagrees with the write catalog's inputs and 264's stored columns |
| `StoredNotesRuleVersionTests` | A change to the stored output over the rule's fixture matrix with no matching recompute step |
| `StoredNotesTestSeedTests` and the teardown check | A test that writes an input past the writers and does not settle the stored values; any stored value that differs from what the rule computes, over every ledger of every test database |
| `MigrationPlaceholderTests` | A migration still carrying the data-step placeholder marker (D11) |

Not covered: writes through dynamic SQL and direct database access. The monitor sees both.

### D9 — The monitor warns on drift, and Repair recomputes

The stored values are one more projection of the consistency monitor, `stored_notes`. It
compares stored with computed in one REPEATABLE READ snapshot, so a write in flight cannot
look like drift, and re-checks the headers that differ under the ordered header lock before
reporting them. It warns like the other projections; a mismatch names its header, never a
memo or a tag.
The check runs on a schedule, so what it holds at its peak must not grow with the ledger. Both
reads compare a fixed number of headers at a time, the first paging the ledger's headers on
their id inside the snapshot, and only the ids of the headers that differ outlive a chunk; the
comparison is on the values, and no text is built for a difference. A stored tag row that
still names a header its leg has left counts against that header as well as the leg's own,
so which headers are reported does not depend on where the chunks fall.
Repair is never automatic: the consistency panel's Repair action runs the writers' own
recompute over the drifted headers only. This check is on derived values. The upgrade's
invariants (D11) still have no runtime check.

### D10 — Snapshots capture the stored values and restore them verbatim

The `notes_*` columns ride along with the `txn_legs` rows, and `txn_leg_note_tags` is a
captured table of its own, inserted after legs and tags. A restore copies both and does not
recompute. That is sound because a restore refuses any snapshot whose `schema_version`
differs from the live database (0037), so a restored row was computed by the rule this
database runs.

The tag-role table grows with the legs, so the snapshot writer no longer writes any table in
one piece: `fn_snapshot_write_part` keyset-chunks every captured table on its primary key,
read from the catalog whatever its shape, and refuses a captured table without one. The
one-piece path and its row ceiling are gone.

A whole-database restore treats a journal entry this build does not ship as a newer
build's. The C# migration steps count as known names alongside the SQL files
(`MigrationRunner.KnownScriptNames`), so a backup taken after this upgrade restores.

### D11 — The upgrade: 263, 264, the backfill step, 266

**263** adds the originating account (D3), `tag_name_key` (D12) and the saved-report repoint
functions, rebuilds in-kind transfers that name no source brokerage as zero-gain sales (D18),
and fills the originating account for every existing header.

**264** (`264_annotation_data.sql`) runs in one transaction: a failure leaves the database as
it was and journals nothing. It creates the stored columns, the tag-role table, the view
columns, the lock functions and the chunked snapshot writer, then moves every existing memo
and tag to where the per-side rule reads it.

- **Step 0.** Blank header and leg memos (empty, or .NET white space only) become NULL, on
  live rows, templates and occurrences. `txn_header_originals` is the feed's archive and is
  left alone.
- **Step A: Moneydance bank-route rows and reminder templates.** A row is a candidate only
  while its originating account is its resolved Moneydance home, its legs are still the
  postings the importer emitted, and its action is what the importer and migration 260 left.
  A field is rewritten only while it is exactly what one importer vintage wrote for the row's
  payload. The memo matches when the header holds the memo as written and the legs hold what
  the vintage put there (nothing on a one-split, each line's text on both legs of a split).
  The tags match when the stored placement, compared by name, is one vintage's placement of
  the payload's names:

  | Vintage | Placement | Names |
  |---|---|---|
  | V1 | every name from the transaction and its splits on the header; no leg tags | trimmed |
  | V2 | the transaction's names on the header; each line's on its home leg | trimmed |
  | V3 | as V2 | trimmed, cut at 64 UTF-16 units, trimmed again |

  Vintages are recognised by state, per field, never by when a row was written. No vintage
  imported a template's tags, so a template's only matching state is no tags. A matching
  field is rewritten to what the importer writes now: on a one-split the home leg gets the
  memo and the split's tags, and the other leg gets the split's text where it is the user's
  own (not blank, not the payee, not the memo) and the transaction's tags; on a split the
  header gets the memo, each line's home leg its text and its tags joined with the
  transaction's, and the other legs nothing, so they inherit their line's value; on a
  one-posting brokerage cash move the brokerage leg gets the memo joined
  with that text, and every tag. A row whose owner, shape or action an edit changed is left
  to Step C, or as it is if it has several postings.
- **Step B: Moneydance investment rows.** A candidate is owned by its resolved home brokerage
  and carries an action. Its memo is rewritten while the header holds the memo as written and
  no leg holds one, to that memo then, in split order, each non-security split's text that is
  the user's own, joined by the event rule (Step D). Its tags are written only while the event
  has none anywhere, as every name the transaction or a split carries. Both land on the
  brokerage leg of a one-posting event and on the header of a multi-posting one.
- **Step M: case-variant tags.** Within each ledger, tags that share a `tag_name_key` merge
  into the earliest created, then the lowest id. Links move to the survivor, and one that
  would duplicate an existing link is dropped, so every transaction keeps the tag. Saved
  reports of every owner filtered on a variant are repointed to the survivor, with their
  `updated_at` untouched. The variants are deleted, then the unique index
  `uq_tags_name_key_per_ledger` is created. Step M runs after Steps A and B, which resolve a
  name to the same survivor, so the merge cannot turn an unedited row into an edited one.
- **Step C: every remaining one-posting header with a memo or tags** (native rows, the
  Moneydance fields Steps A and B left, templates, occurrences). With X the originating
  account's leg, Y the other leg and H the header: X's memo is its own, else H; Y's is its
  own, else H where X has its own, else nothing (Y then inherits). X's tags are H's and its
  own; Y's are H's and its own, or none where neither leg had its own. The header is
  cleared. Each register keeps what it showed, except the changes in D16.
- **Step D: multi-posting events with values on their investment side.** The header's memo
  becomes every distinct memo on the header and those legs, so no memo is lost: the header's
  first, then the brokerage's first leg's (what the investment register showed when the header
  had none), then the rest in posting order, joined by the event rule. The legs' tags join the
  header's. Those legs are then cleared. A funding brokerage is not on the investment side and
  is left alone; an action-less multi-split on a brokerage is not an event and is not touched.

  **The event rule** joins an event's memos wherever several meet: in the upgrade (here and
  in Step B), in the import (`MoneydanceNotes.InvestmentEvent`) and in an edit
  (`AnnotationPlanner`), when a change of shape such as setting an action gathers postings'
  or investment-side legs' memos onto an event (D4) and when convert in-kind joins the sell's
  value with the buy's. Values that arrive at one place are joined all at once, never pair by
  pair: each memo is trimmed, a blank one is dropped, one equal to a memo already
  kept is dropped, and the rest are joined with " · " in order. Equality is ordinal over the
  trimmed text, so case is kept apart, and a memo that already holds " · " is compared whole.
  So "a", "b", "a" join to "a · b". The C# `AnnotationText.JoinDistinctMemos` and the SQL
  `mig264_join_distinct` are one rule, held equal by a test.
- **Invariants**, over every header of every ledger: (a) a one-posting header holds no memo
  or tags; (b) an actioned multi-posting event's investment-side legs hold nothing; (c) the
  originating account is on a leg of its header, NULL exactly when it has none; (d) an
  actioned event other than an in-kind transfer is owned by a brokerage on every posting; (e)
  an in-kind transfer by its source brokerage; (f) an action-less multi-split on which some
  account is on every line is owned by one of them; (g) no stored memo is blank; (h) no two
  tags of a ledger share a key; (i) no saved report names a tag Step M merged away. A
  violation fails the upgrade, naming the count per invariant, and changes nothing.

Every step is deterministic. A tag 264 creates takes the id `md5(ledger_id || '/' || name)`
and the `created_at` of the earliest header that uses it; a moved link keeps its own; no clock
is read. A second run over the result changes nothing. Each step counts what it did in a
`RAISE NOTICE`, with no names or amounts.

**`264_stored_leg_notes_backfill`** is a C# step (`LegNotesBackfill`), journaled directly
after 264, that fills every leg of every ledger in batches, in header id order. It reads
through a private projection of only the columns the rule reads and writes through a private
store mapped exactly to 264's columns, so a later change to the application's model cannot
break an upgrade that spans it. It reads stored rows only, so a rerun after a partial run
converges to the same values. A failure journals nothing, and the next start runs it again.
It does not compact `txn_legs`, which would take an exclusive lock. Registration fails closed:
`MigrationRunner` refuses a migrations directory that holds a script sorting after 264 but
not 264 itself.

**266** drops the view's old memo and tag columns and the register's SQL functions. It first
refuses the upgrade, naming each one, if a string-bodied function still reads a dropped
column.

**A later change to the rule** ships as a numbered C# step that recomputes everything; 264's
backfill is the first such step. It also retires older snapshots, because the schema version
changes. When one upgrade spans several recompute steps, only the last one computes.

**The placeholder.** While its data steps were being written, 264 carried a guard, marked
`coffer:migration-placeholder`, that refused any database holding data. `MigrationPlaceholderTests`
fails on any migration still carrying the marker, and `release.sh` refuses such a tree, so it
cannot ship.

**First start after the upgrade takes longer**, and the time grows with the size of the
ledgers on the install: 264's data steps and the backfill run over every header. The API
applies every migration and step before it starts listening, so nothing answers, `/readyz`
included, until they finish. The install and upgrade scripts wait for `/readyz` for up to
`COFFER_READY_TIMEOUT_SECONDS`, default 600 seconds, which is `MigrationRunner`'s limit on
one migration step. A timeout ends only the wait; the API carries on migrating, and the
script exits with an error naming `docker compose logs -f api` to follow it. An
`upgrade.sh` from before this release does not wait itself, but it runs this release's
`install.sh`, which does.

### D12 — One tag name key, enforced by an index

- **`tag_name_key(name)`** (263) is the only definition of "the same tag name": lower() of
  the name with leading and trailing .NET white space removed, under the database collation.
  Every name lookup goes through it, the importer's included; no name is folded in .NET.
- **`uq_tags_name_key_per_ledger`** on `(ledger_id, tag_name_key(name))` (264 Step M) keeps a
  second tag of one key from existing.
- **A lost race retries once.** Two saves that create one new name at the same time both add
  it; the index refuses the later one. `AppDbContext` then re-resolves the save's new tags by
  key (`TagDictionary.ReResolvePendingAsync`), moves their pending links to the tag the other
  writer committed, and saves once more. A second refusal is the caller's. A rename refused by
  either tag-name index is `NameExists`.
- **The importer reads back.** `EnsureTagsAsync` inserts with `ON CONFLICT` on the key index,
  and reads a name another writer created in between back by its key in a second statement.
- **A tag merge repoints saved reports.** The user's merge in Settings repoints every member's
  saved-report tag filters through `saved_reports_repoint_tag`, a narrow `SECURITY DEFINER`
  function that checks the caller's owner or editor grant on the ledger. The response counts
  only the reports the caller can see. Deleting a tag recomputes every header that carried it.
- **Limits.** A tag set holds at most 20 tags. Only growing a set past the limit is refused;
  an unchanged or smaller over-limit set saves. System merges (the Moneydance union, an
  event's tags, the upgrade, a collapse, convert in-kind) may exceed it, and nothing is ever
  dropped. A tag name holds at most 64 UTF-16 units (`TagName.MaxLength`). Where Step A
  rewrites a row whose name an uncapped vintage stored longer than that, the row links to the
  name cut at 64, as a fresh import writes it, and the long-named tag stays in the ledger with
  nothing linked to it; 264 deletes no tag except Step M's variants. A cut that falls inside a
  two-unit (surrogate-pair) character keeps the characters before it, as a fresh import does.

### D13 — Memos are trimmed, and blank is NULL

Every memo is trimmed of leading and trailing white space, Moneydance imports included, and a
blank one is stored as NULL, which means inherit. One normaliser (`AnnotationText`) does it
for every writer; 264 Step 0 does it for existing data.

### D14 — The register and the API contract

- **Entry key.** On the owning side (the originating account, an event's investment side) an
  entry is keyed by its header; on every other side by its leg, a one-split's other side
  included.
- **The opposite side never groups.** Each line of a split is its own read-only row in the
  opposite account's register. The target-split clusters ("▸ N splits") and
  `regroupTargetSplits` are removed.
- **`GET /transactions` requires `account_id`**, refused with a 422 like its siblings. The
  ledger-wide branch is removed.
- **A bulk selection is one register's.** The selection summary and the bulk recon-status,
  delete, unhide and move endpoints refuse a selection without `accountId` with 422
  `account-not-in-ledger`; there is no ledger-wide selection.
- **Bulk ownership is the stored read-only flag.** A select-all delete, unhide or move, and
  its summary's `count`, cover only the headers with a leg in the register's scope whose
  `notes_is_read_only` is false, the flag the register marks a read-only row by; the
  posting counts do not decide it (`BulkTransactionsRepository`). An explicit selection
  that names a row the register shows read-only is refused whole by delete, unhide and
  move, before anything is written, with 422 `selection-includes-read-only`. An id that
  names no live transaction of the ledger is ignored, not refused.
- **Reconciliation reaches read-only rows.** A row's recon status is the account's own leg
  (0082), so bulk recon status, explicit or select-all, sets it on read-only rows as well
  as editable ones.
- **The footer counts every selected row.** Read-only rows are included and named beside the
  count: `15 selected · 3 read-only`, or `N selected` when none is read-only. A select-all's
  counts are the summary's `totalCount` and `readOnlyCount`. An explicit selection's total is
  the ids it holds, and its read-only part is the summary's `readOnlyCount`, counted from the
  loaded rows' `isReadOnly` only while that summary is in flight. The count describes the
  selection, not what an action reaches.
- **The Σ sums every selected row.** The summary's `sumOnAccount` covers the rows
  `totalCount` counts, read-only rows included, in both selection modes, each by its own legs
  in the register's scope, so it always matches the count beside it.
- **Saves return `{ entries }`**: PATCH, approve and merge return every entry the header has
  in the scoped register, and rail buckets and status counts count entries as the page shows
  them.
- **Writes carry a `notes` member** (`side`, `memo`, `tags`, `lines` by index) on the bank
  create, PATCH and reminder bodies, replacing every top-level and per-posting memo and tag
  field. Presence decides: an omitted key leaves the value, and `null`, `""` or `[]` clear it.

### D15 — The register marks what is inherited and what is read-only

An inherited memo or tag set is drawn with a small corner arrow before it, in the subtle text
colour (`InheritedMarker`); the account it comes from is its accessible label and tooltip. Own
values render as before. Brackets copy into text as part of the memo, and italic is already
how a scheduled row reads, so neither was used.

A read-only row (D1) carries a badge, "↗ Split" on a split line seen from its opposite side
and "↗ Investment" on an event's counterpart row, whose tooltip names where the row is edited.
In every register the badge sits in the slot after the date, never in the payee cell, which
keeps its whole width for the payee and memo. In the bank and category registers that slot is
the check-number track, 6rem wide so the badge is not cut, and the badge sits above the check
number; a row without a badge renders the cell unchanged. In the investment register it sits
on a line of its own above the action chip, in the same column, so the chip keeps the left
edge every other row's chip has. The badge and the shortened row menu (D1) are the whole
read-only distinction: a read-only row takes no other tone or styling.

In the editor, while a field inherits, its caption reads "from *other side*" and the field
shows the inherited value as its placeholder; typing or picking tags creates this side's own
value. With an own value the caption is the label alone, and emptying the field reverts. The
caption, the marker and the placeholder show the inherited value as the save will leave it
(both amended 2026-11-04, below). A tag pick while inheriting starts from the inherited set.
The split editor's
captions are "Memo (whole transaction)" and "Tags (whole transaction)", and expanded split
lines show their own tags; a line with no memo of its own shows "—".

### D16 — Accepted display changes on upgrade

Each is stated in the upgrade notes, without counts:

- **A blank memo no longer hides one.** A row whose blank leg memo masked the transaction's
  memo now shows the value it inherits (Step 0).
- **A one-split's other side inherits.** A one-split with no header memo and a memo on the
  entered side's leg now shows that memo, inherited, on the other side. A one-split leg's own
  tags, which only the header's were shown over before, are now shown (Step C).
- **Split lines stop falling back to the transaction.** Seen from its opposite side, a split
  line with no memo of its own is now blank, native and Moneydance lines alike. Its tags stop
  falling back the same way, and the tag filter follows, except where Step A rewrites a
  Moneydance split's tags: each line's home leg then carries the transaction's tags joined
  with its own, so the line still shows them.
- **A memo no side showed is kept.** A one-split whose two legs each held their own memo and
  whose header held a third, shown nowhere, has that third joined onto the originating side as
  "header · own" (Step C).
- **An event's memos are joined.** A multi-posting event whose investment-side legs held
  memos now shows every distinct one on the header, joined with " · " (Step D); a Moneydance
  event's split texts join its memo the same way (Step B), as a new import joins them.
- **Case variants merge.** Tags that differ only by case or surrounding white space become the
  earliest-created one, and rows show its spelling. Saved reports filtered on a merged variant
  follow it to the survivor (Step M).
- **Moneydance tags are recreated.** Tags no earlier import wrote (on investment events and
  reminder templates) are created by the upgrade, even where a tag of that name was deleted in
  Coffer, because no record of the deletion exists.

### D17 — MCP

- **`set_transaction_tags` requires `accountId`**, the register the tags are written from: on
  a one-posting transaction they are that side's, on a split the whole transaction's. If any
  target is outside the ledger, off that account, or read-only there, the whole call is refused
  and nothing is tagged. `dryRun` reports where each set would land. This is a breaking change.
- **`list_transactions`** returns one line per category line, each read as the register of
  the leg in scope shows it. A transfer between two real accounts reads the outflow leg (a
  zero-amount one, the originating side's), so both of its lines read the same. Each line
  carries `memo` as that register shows it, `transactionMemo` where that register is the split
  side, and the tags in play there.

### D18 — An in-kind transfer with no source brokerage becomes a zero-gain sale

An in-kind transfer must be owned by its source brokerage (D3), the brokerage whose holdings
account the shares leave. One stored shape names no source: a single posting holding the
holdings leg, which carries the shares and their carried cost, and a transfer leg to a category or
another account that is neither a brokerage nor a holdings account. No brokerage leg is on the
header, so neither form of `transfer_shares_source_brokerages` finds one, and the model has
no in-kind transfer whose destination is not a brokerage.

Before its backfill, migration 263 rebuilds that shape as the sale with a transfer the importer
and the investment editor write (`sellx`): the holdings leg and a new brokerage cash leg on
posting 0, a new brokerage cash leg and the transfer leg on posting 1. The sale is priced at
the cost the FIFO walk (`holdings_fifo_walk`) gives the shares it takes, to the cent an amount
is stored at, and the unit price is the editor's, `round(|amount| / |quantity|, 6)`. Its total
realized gain is therefore zero, except that a cost ending in exactly half a cent rounds to a
one-cent gain, as on any sale at that cost. Shares and cost basis are unchanged. Where the
carried cost still matches the lots, so is every amount; where it does not (a purchase of the
shares was edited since), the transfer leg moves to the cost, and its account's balance moves
with it. Where the lots straddle the one-year line at different costs, the walk apportions the
proceeds by share count and the cost by lot, so the long- and short-term parts offset each
other: the same figures the importer's sale of those shares at their cost records. A hidden or
merged transaction, which the walk leaves out, keeps the carried cost and records no gain. As a
save would, the migration then recomputes the rebuilt transactions' posting counts, the running
balances of every account on them, and their holdings.

- **Only the current ledger is read.** The shares, the lots the walk gives them, the transfer
  leg's account and the brokerage that owns the holdings account decide the result. A
  provider payload is never read.
- **Exact shape only.** One posting of exactly those two legs, the holdings account
  belonging to exactly one brokerage of the ledger. Any other shape is left as it is, and
  assertion (c) refuses the upgrade.
- **Zero by construction, not by prediction.** The price is read from the walk that records
  the gain, so no drift, shortfall, split or ordering at a shared instant can leave a gain to
  refuse.
- **Deterministic and idempotent.** The new legs' ids derive from the header id, and their
  `created_at` is the holdings leg's. A rebuilt header is a sale, so a re-run finds nothing.
- **No judgement is asked for.** The rebuilt transaction records the cost the ledger's lots
  already hold, and it is edited like any other sale.
- **The pre-upgrade check counts the one refusal.** `0128-precheck.sql` counts the in-kind
  transfers with no source that the rebuild cannot take.

## Consequences

- **Reads are column reads.** No reader evaluates the rule per row, and every reader of what
  a row shows reads the same stored result (`AnnotationReaderCatalogTests` holds them to it),
  so a filter, a report and the register cannot disagree.
- **Writes do more.** Every write that changes an input recomputes its headers in the same
  transaction, under header locks. A large set-based change (a holdings-link change on a busy
  brokerage, deleting a much-used tag, a large category merge) holds those locks for the whole
  request. A timeout rolls the user's write back whole, so nothing is left stale.
- **Snapshots grow** by the stored columns and the tag-role rows. Snapshots taken before this
  upgrade cannot be restored after it, and a later rule change retires older snapshots again.
- **The first start after upgrading is slower**, in proportion to ledger size (D11).
- **The originating account is a stored fact** that every reshape must hand over. The
  planner assigns it on every write and throws on a result that leaves it off the transaction.
- **One tag per name.** A name that shares a key with an existing tag resolves to that tag,
  so a second spelling that differs only by case can no longer be created.
- **Moneydance export** would need the reverse of the import's merges and is lossy for
  Coffer-only features (transaction-level tags on a split, per-line opposite-side values). None
  exists, and the reader guard forces any future one through the stored presentation.
- **FU-025 and FU-050** are settled by Step A: the importer's counterpart memo and per-line
  tags now reach ledgers imported before them, and an edited row keeps its edit. Both are
  closed
  (the closed-work log).

## Alternatives considered

**Evaluate the rule at read time, in C#.** The same rule composed into every query as an
expression. Rejected: every page, filter, search and report then pays for the posting's other
leg, the investment side through the holdings links, the originating account and three
junction tables on every row, and it did not meet the release's bar for end-to-end response
time.

**A SQL definition of the rule.** A view or function readers could join. Rejected: it is a
second definition of what a row shows, which is the defect this ADR removes. The rule exists in
C# only, and the upgrade fills existing data through a C# step for the same reason.

**A trigger to recompute.** Rejected under 0032. The interceptor and the explicit calls give
the same atomicity, and the write catalog holds every writer to them.

**A separate table for the stored columns.** Rejected: it adds a join to every page and every
ledger-wide read, and paging and ledger-wide reads fell short of the same bar with it. The
columns sit on the leg row the reader already has, with only the tag roles in their own
table.

**Recompute on restore.** Rejected: a restore refuses a snapshot from another schema version,
so the stored rows were computed by the rule the database runs, and a verbatim copy is both
faster and exact.

**An in-kind transfer with no source brokerage, kept as an exception.** Assertion (c) and
the invariant checks would accept a transfer owned by no brokerage. Rejected: the transaction
would sit in the model with no defined side for its memo and tags, and the exception would stay
in every check for good. D18 rebuilds it instead.

**Rebuild it as a sale at the source system's price, then pair it.** The in-kind scrub
(ADR-0065 D4) converts a sale and a matching buy into one transfer. Rejected: the shape D18
rebuilds has no destination brokerage, so no buy can match it, and a sale at a placeholder
price books a gain the ledger never recorded.

**A new "shares out of the ledger" action.** It would name the move more precisely than a
sale. Rejected for this release: it changes the action catalog, the editor, the reports, MCP
and the invariant checks, for a shape a zero-gain sale already records without changing a
number.

**Keep the carried cost and check the gain.** Rejected: where a purchase was edited since,
the carried cost no longer matches the lots, so the upgrade would refuse rows no tool
repairs, and predicting that before the upgrade means replaying the walk's order outside it.

**Require each term of the rebuilt sale to be zero.** Rejected: a sale at cost whose lots
straddle the one-year line at different costs records offsetting long- and short-term parts
on every path that writes it, the importer's included, so the check would refuse the record
the importer writes.

**One sale per term.** Splitting such a transfer into a long-term and a short-term disposal
would make each term zero. Rejected: it writes a shape no other path writes and the editor
does not show as one event, to change two report figures that net to nothing.

**Repair drift automatically.** Rejected: an automatic repair hides the writer defect that
caused the drift. The monitor reports, and the user repairs from the consistency panel as for
every other projection.

## Amendment, 2026-11-03 — a first page of entries across the ledger (ADR-0109)

D14's removal of the ledger-wide branch of `GET /transactions` stands: that route still
requires `account_id`. `GET /transactions/entries` (ADR-0109, 2026-11-03 amendment) is a
different read, for the home screen: one first page, with no cursor, of the entries of
every account a person reads, each keyed, filtered and assembled by the register's own
statements (`RegisterQuery`) over its own account's legs, so each row is the row that
account's register shows, with the account it belongs to.

- **An entry there is named by its account and its key.** One key can name an entry on
  more than one of the accounts: an in-kind share transfer is keyed by its header on both
  brokerages, each of them the event's investment side. So that page groups legs by
  account and key, where a register, whose rows are all one account's, groups by key.
- **Its direction filter reads the amount the row shows** (D2): a header-keyed entry's net
  on its account, a leg-keyed entry's own amount.
- **The per-account review count counts entries too.** `needsReviewCount` on
  `GET /accounts` counts an account's needs-review entries as its status counts do, so a
  transaction with two lines on one other account counts two there.

## Amendment, 2026-11-04 — no control to go back to the other side's value

D15's "Use *other side*'s" is removed from every editor. Emptying the field is the way back:
a side with no memo, or no tags, of its own inherits the other side's (D1), and the editor
then shows that value as the field's placeholder, captioned "from *other side*".

- **Why.** The control did what emptying the field does, so it was a second way to do one
  thing. Pressed where the other side has no value, it left the field empty, the same as
  deleting the text, while its label promised a value.
- **What stays.** The caption's "from *other side*" while the side inherits, the marker in
  the field, and a tag pick that starts from the inherited set. Blank still always means
  inherit.

## Amendment, 2026-11-04 — the editor shows what the save will leave

D15's "from *other side*", the marker in the field and the inherited placeholder show the
inherited value as it will be once the draft is saved, not as the row is stored. The stored
values (D5) name the other side the row is stored with, which is not the one the save writes
once the draft changes it: on a row awaiting review stored against Uncategorized, the
category picked in the field.

- **The bank editor.** Its PATCH keeps a posting's two legs, by id, and a changed counterparty
  moves the other leg to that account with its memo and tags (D4: values follow their legs).
  So the editor shows the stored value from the counterparty the draft has. A posting that
  replaced the stored one has new legs, which carry nothing, so nothing is shown.
- **The investment editor.** Its save rebuilds the event's legs and keeps a stored leg only on
  the same account in the same role, or on the same account for a leg with no role yet (a
  feed row's); D4's "an opposite side's own value survives if its leg survives". So the
  stored value is shown only while the draft saves one posting across from the account it is
  on, in a role that keeps that leg. Across another account, or while the draft names no
  account, nothing is shown, which is what the save leaves. A draft that writes several
  postings (a fee, or an action that writes more than one) shows nothing inherited either:
  the event then owns its memo and tags, and the save copies what the brokerage showed onto
  them (D4), a copy the editor leaves to the server, as the bank editor does on a split.

## Amendment, 2026-11-05 — a save that recomputes brings its own transaction (ADR-0112)

D6's tracked saves are qualified. A save that recomputes balances or holdings runs in its
writer's own transaction, opened with the ledger write lock (`BeginLedgerWriteAsync`,
ADR-0112 D1), and the stored-notes recompute runs inside it, as in any caller's
transaction. The fallback, `LegNotesRecomputeInterceptor` opening a transaction when the
caller holds none, serves saves that recompute neither.

- **Why.** The balance and holdings recompute functions refuse a transaction that has
  written or row-locked without the ledger write lock (ADR-0112 D2). The fallback
  transaction is opened in the save itself, after the interceptors' reads and without that
  lock, so a recomputing save in it would be refused.
- **What stays.** The mechanism: LegNotes is registered last, opens a transaction only
  when none is open, and commits after every other interceptor's work. The header locks
  (`fn_lock_txn_headers`), and `fn_lock_tags` and `fn_lock_accounts`, come after the ledger
  write lock in a transaction that takes it (ADR-0112 D3).

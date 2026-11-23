# 0106 — Folders replace tabs, and the rail shows balances and remembers its shape per browser

* Status: Accepted (amends 0021, 0057, 0058); amended 2026-11-13 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (the Overview's, the Budget page's and Reports' totals convert, and so do the rail's own folder and section totals, converted on the server by `GET …/accounts/subtotals`, so the rail no longer shows "mixed")
* Date: 2026-10-20
* Related: [ADR-0016](0016-moneydance-account-translation.md) (the importer flattens
  real-account parents), [ADR-0017](0017-account-discriminator.md) (category hierarchy
  stays on `parent_id`), [ADR-0032](0032-triggers-as-last-resort.md) (the cycle
  trigger, dropped by migration 261; see the 2026-10-22 amendment), [ADR-0037](0037-snapshots-and-backups.md) (snapshot capture and restore),
  [ADR-0085](0085-reporting-ignores-is-active.md) (inactive accounts still count).
  Closes FU-006, FU-051 and FU-052; FU-007 and FU-028 close as won't-do (D1).

## Context

The rail organised accounts two ways. Sections by account type, and **tabs**:
`user_account_groups` (migration 033), a named subset of a ledger's accounts per
`(user, ledger)`. Selecting a tab filtered the sections down to its members; an account
could sit in any number of tabs.

Three open items asked for more, and each one ran into tabs:

- **FU-006, folders.** A second level inside a type. Folders and tabs answer the same
  question, "how do I group my accounts", in two incompatible ways: a tab is a filter
  that hides what it does not hold, and a folder is structure that holds each account
  once and shows a total. Two organisers on one rail means choosing, every time, which
  one an account belongs in.
- **FU-052, balances.** A folder's balance is the sum of its children, so a rail with
  balances has to know what a folder row shows. A tab has no total to show; it only
  narrows the list.
- **FU-051, collapse and resize.** The entry said to store rail state in
  `user_preferences`, which ADR-0057 makes per `(user, ledger)`.

FU-007 (remember the last tab) and FU-028 (reorder tabs) were refinements of tabs.

FU-006's own entry leaned towards `accounts.is_folder`, a folder stored as an account.

## Decision

### D1 — Tabs are removed

Migration 258 drops `user_account_groups` and `user_account_group_members`, and the API,
the tab strip, and the "Add to tab" and "Remove from active tab" menu items go with them.
Tabs held no financial data: no account, transaction or balance changes. Each user's tab
names and memberships are deleted, not converted to folders. A tab could hold an account
that another tab also held, and a folder holds it once. Tabs were private to one user,
and folders are shared by the ledger, so converting them would show one member's private
grouping to everyone.

**FU-007 and FU-028 close as won't-do.** There is no tab left to remember or reorder.

### D2 — A folder is its own table, not an account

`account_folders` (id, ledger, account type, name, parent) and `account_folder_members`
(one row per account in a folder, keyed on the account).

- **Ledger-wide**, unlike tabs: everyone with access to the ledger sees the same folders.
  Writes are owner and editor, through the role-gated `_read` / `_write` RLS pair and
  `RequireLedgerAccess()`, like every other piece of ledger data.
- **One account type per folder, and at most one folder per account.**
- **Folders nest to any depth.** A folder holds folders, accounts, or both. No depth cap.
- **Names are unique among siblings**, ignoring case, and are 1 to 80 characters.
- **Deleting a folder never deletes what it holds.** In one transaction its folders and
  its accounts move up to its parent, or to the top level of the section, and then it is
  deleted. The cascades only ever fire when a whole ledger is deleted or a snapshot
  restore clears it.
- **Two editors at once.** A delete, a move, a new sub-folder and filing an account each
  lock the folders they touch before reading them, so two writes on one folder run one
  after the other and the second sees what the first did: a delete moves up whatever was
  just put in the folder, under the parent it has now. A race the locks cannot order,
  such as a deadlock, is refused as `account-folder-changed` with nothing written,
  never answered with a server error.

Why not an account (FU-006's options 1 and 2). `fetchAccounts` feeds thirteen pickers
and lists, and every one filters on `accountType` or `isSystem`. A folder stored as an
account would appear in each of them as something you can post to. Each one would need
teaching not to show it, and a trigger would have to refuse postings to it. A separate
table keeps every picker correct with no change, and a folder cannot hold a transaction
because it has nowhere to put one.

ADR-0017 rejected an `account_folders` table, but for **categories**, where a parent
with its own transactions is real data. Real-account folders carry no data at all, which
is why they get a table of their own. Categories keep their tree on `parent_id`.

### D3 — The database guarantees that a folder holds only its own type

Every link carries the account type in a composite foreign key:

- member → account: `(account_id, ledger_id, account_type)` → `accounts`, through a new
  `UNIQUE (id, ledger_id, account_type)` on accounts;
- member → folder: `(folder_id, ledger_id, account_type)` → `account_folders`;
- folder → parent: `(parent_id, ledger_id, account_type)` → `account_folders`.

`account_type` cannot change after an account is created, so these keys make "an account
sits only in a folder of its own type, in its own ledger, and a folder only inside one of
the same type" true for every writer: the API, MCP, snapshot restore, a future importer,
a statement typed by hand. The API checks the same rules first, so a refusal comes back
as a business error with a code (`account-folder-type-mismatch` and its siblings)
rather than a constraint name. The keys are the guarantee.

**A cycle is refused by a trigger**, because no key can express "not its own ancestor".
ADR-0032 allows it: the rule is about the shape of the whole table rather than one
request, the check is idempotent, and it reads only the table it guards. The trigger
takes a per-ledger lock before it walks the ancestors, so two editors moving folders at
the same moment cannot together make a cycle that neither move makes alone.
*Superseded 2026-10-22: ADR-0032 does not allow it, and the API refuses a cycle alone.
See the amendment below.*

**The parent key is a self-reference, so it is deferred.** Snapshot restore replays a
table in chunks, and a child can land in an earlier chunk than its parent. ADR-0037's
first restore invariant makes every such key `DEFERRABLE INITIALLY DEFERRED`, and this
one is no exception.

Both tables are captured in snapshots (members after accounts and folders), and both are
cleared by snapshot restore and ledger delete. Migration 258 moves the schema version,
so snapshots taken before it can no longer be restored (ADR-0037, decision 6).

### D4 — Folders are a sidebar feature

The account pickers, the Accounts page, the dashboard and reports still group by type
only. The rail is where accounts are navigated, so it is where folders are. This is a
deliberate difference, not an oversight.

- **MCP.** `list_accounts` returns each account's `folderId` and `folderName`, so a model
  sees the same structure as the rail. There is no MCP write for folders.
- **Moneydance import is unchanged.** It still flattens real-account parents (ADR-0016).
  It never stored their names, so existing ledgers cannot have them recovered as folders
  either.

### D5 — Moving is drag and drop, and the row menu does the same moves

Drag is the primary way to move. Account rows and folder rows are draggable. A folder
row takes a drop into it, and a section heading takes a drop to the top level of that
section. A target of another account type, and the dragged folder itself or anything
inside it, shows a not-allowed state and does nothing. The drop is optimistic and rolls
back with an error if the server refuses it.

There is no drag on touch and none from a keyboard, so the row menu offers the same
moves (ADR-0021 Rule 10, and the splits grid's precedent). `ContextMenu` has no
submenus, so each target is its own item:

- **On an account:** "Move to: A › B" for every folder of its type, and "Move to top
  level" when it is in one. Nothing else: an account's menu does not make folders.
- **On a folder:** Rename (in place), "New folder inside…", "Move to: A › B" for every
  valid target, "Move to top level", and "Delete folder", whose contents move up.

**A folder is made from where it will live**: a top-level one from its section heading, a sub-folder from "New folder inside…" on its parent.
Each section heading carries a new-folder button: the **folder-with-plus** icon, never a
bare plus, which on a list of accounts reads as "add account". It takes the splits
grid's treatment from ADR-0021 Rule 10, not a hover-reveal: present and reachable by Tab
at rest, quiet until the heading is hovered or focused, then full strength. Its
accessible name and `title` are "New folder in" and the section's name. It opens an
inline name field at the top of the section, the same field Rename uses: Enter or
leaving the field creates, Escape or a blank name cancels.

All of it is for owners and editors. A viewer sees folders and their totals, and gets
no heading button, no move items and no drag.

**Order is by name, with folders first** at every level. There is no manual order, as
there is none for accounts, and so no order to store, sync or repair.

### D6 — The rail shows balances

- **Each account** shows its current balance. An investment account shows cash plus
  market value, the rule the dashboard and the register's portfolio bar already use.
  Liabilities show negative, as the dashboard does.
- **A folder** shows the sum of every account under it at any depth, and **a section
  heading** the total of its type. Inactive accounts count in both (ADR-0085), so a
  total does not change when "Show inactive" is ticked. Where the accounts summed are in
  more than one currency, the row shows the word "mixed" in the subtle text colour
  instead of a number, with a `title` saying there is no single total, since nothing
  converts currencies. **This is new, not a reuse.** The dashboard adds mixed currencies
  together without conversion and says so once, in a caption above its totals. A rail
  row has no room for a caption, and a sum across currencies printed in one currency's
  format would read as a real figure. The dashboard is to follow the rail, not the
  reverse. *(It does since 2026-10-31: every aggregate that spans currencies on the
  Overview, the Budget page and Reports shows the rail's marker, ADR-0109 D9. Since
  2026-11-13 those convert into the ledger's reporting currency instead, ADR-0114 D7.
  So do the rail's own folder and section totals: the server converts and sums them, a
  total that needs a missing rate reads "no rate", and the rail no longer shows
  "mixed".)*
- **One read for the whole ledger**, `GET …/accounts/balances`, a projection of the
  valuation MCP already uses. Not a field on `AccountSummary`: every picker fetches that,
  and each would pay for the valuation it does not show. The SPA keys it under
  `['accounts', ledgerId]`, so every write that already refreshes the account list
  refreshes balances too. Two writers that refreshed nothing now do: creating a bank
  transaction, and editing a security or its prices. *(Since 2026-11-13 the totals are a
  second read, `GET …/accounts/subtotals`: each type's and each folder's total, from the
  overview's valuation, converted (ADR-0114 D7). The browser never adds balances, which
  would add currencies. Each folder's total lists the accounts it covers, and the rail
  shows it only while those are the folder's accounts, so an unanswered move never shows
  the old total. The SPA keys it under `['account-folders', ledgerId]`, so a folder write
  refreshes it, and `invalidateHomeReads` refreshes it beside every accounts refresh.)*

### D7 — The rail's shape is per browser (amends ADR-0057)

The rail's width is resizable by a handle on its right edge, and a button in its header,
or `Ctrl+\`, collapses it to a 56px column of icons. The default is the width the rail
has always had (ADR-0021 Rule 1, 224px), and a resize starts from there. It is held
between 200px and the smaller of 480px and 40% of the window, so a width saved on a wide
screen cannot crowd the main pane on a narrow one. The handle is a `role="separator"`:
arrow keys resize, Enter collapses, double-click resets to the default. Each folder's
open or closed state is remembered too; folders start open.

**All of it is stored per browser in `localStorage`, not in `user_preferences`**, for
three reasons:

- **Width belongs to the screen, not the ledger.** Stored per ledger, the rail would
  change width each time you switched ledgers, and would have nowhere to live before a
  ledger exists.
- **It must apply before first paint.** A server value arrives after the page renders,
  so a collapsed rail would draw expanded and then snap shut. `theme-init.js` sets the
  width before React mounts, as it already does for theme, accent and density, which are
  per device for the same reason (ADR-0021 Rules 4 and 11a).
- **It is the same on every ledger**, which is what a layout choice should be.

ADR-0057's "every preference is per-ledger" now has this exception: presentation of the
app's own chrome is per browser. Preferences about a ledger's content stay in
`user_preferences`.

### D8 — The rail's new rows are built from the parts it already has

The condition on the design: the look, the sizes and the offsets stay
consistent with the rest of the app, and nothing is reinvented. The design gave the
intent; wherever its measurements differ from the app's, the app's win.

- **Sizes come from the rail.** Row height, padding, type, colour, radius, icon size and
  gaps are the existing sidebar rows'. Each folder level indents with the device the
  rail already uses to set a section's accounts off from its heading, an indent under a
  faint left border, so no indent token was added. The one new token is
  `--spacing-fixed-56px`, the collapsed rail's width, a pinned length like every other
  (ADR-0021 Rule 11). It is a spacing token, so the rule that a colour token is defined
  for all four themes and shown on `/__styleguide` does not apply to it.
- **Components are reused, not copied.** Account rows stay the sidebar's nav links, and
  a folder row, which toggles rather than navigates, takes the same row metrics; menus are `ContextMenu` with `RowActionsButton`; the heading button is an
  `IconButton`; type labels and icons come from `lib/accountTypes`; the attention and
  review dots keep their place. The inline name field that tab rename used is lifted into
  one shared field, which Rename and both "New folder" paths use.
- **A balance looks as it does on the dashboard.** The same money helper, the same
  monospace figures and the same sign. The one difference is a total that spans
  currencies: the rail shows "mixed" where the dashboard sums without conversion (D6).
  *(Since 2026-10-31 the Overview, the Budget page and Reports show "mixed" too, ADR-0109 D9;
  since 2026-11-13 their totals convert instead, ADR-0114 D7.)*
- **Drag copies the splits grid.** The HTML5 drag pattern of the split editor's posting
  reorder, and the same rule that every drag has a menu equivalent.
- **`/__styleguide` renders as before.** The rail's new behaviour has defaults that leave
  the page that documents it unchanged.

## Consequences

- The upgrade deletes every user's tabs. Nothing financial is lost; anyone who relied on
  a tab rebuilds that grouping as folders, which everyone on the ledger then shares.
- A rail collapsed or resized on one machine is not collapsed or resized on another.
- On a shared ledger, one editor's folder changes are everyone's. That is the point of
  making them ledger data, and it is why viewers cannot make them.
- The rail and the rest of the app now group accounts differently: folders in the rail,
  type alone everywhere else (D4).
- A move that would put a folder inside itself fails in the database, whichever path
  made it, not only in the API's own check. *Superseded 2026-10-22: it fails in the API
  only; a direct SQL write is the operator's to get right (amendment below).*

## Alternatives considered

- **`accounts.is_folder`, or `account_type = 'folder'`** (FU-006's options 1 and 2).
  Every account picker would list folders, and postings would need a trigger to refuse
  them (D2).
- **Keep tabs and add folders.** Two organisers that overlap, one a filter and one a
  structure, and a choice between them for every account.
- **Convert tabs into folders on upgrade.** Tabs are many-to-many and private; folders
  are one-to-one and shared (D1).
- **Check the type match in the repository only**, as `budget_targets` does. It would
  hold for the API and nothing else.
- **A depth cap.** Not wanted. Each level indents by the same amount (D8) and names truncate,
  with the full path on hover.
- **Rail state in `user_preferences`**, as FU-051 proposed. Per ledger, and late to
  paint (D7).
- **A balance on `AccountSummary`.** Every picker would pay for it (D6).
- **The design's own measurements, including a wider default rail.** The design set the
  intent; the app's existing sizes win (D8), so the default width stays the rail's own.
- **A heading button hidden until hover.** Invisible on touch, where it is the only way
  to make a top-level folder; ADR-0021 Rule 10 already rejects it (D5).
- **"New folder…" on an account's menu.** Declined: a folder is made
  from where it will live, the section heading or its parent folder (D5).

## Amendment, 2026-10-22 — the API alone refuses a cycle, and migration 261 drops the trigger

ADR-0032 does not allow the cycle trigger. Its first gate is that no API path can carry
the rule, and one does: the API walks the ancestors before every move. A backstop "for a
race or a caller that skips the API" is the case engineering-standards §3.3 rules out
("fix the writer"). The trigger and its function are dropped by migration 261.

**What upholds the rule now.** `AccountFoldersRepository.MoveAsync` is the only write that
names a new parent for an existing folder. It reads the folder's account type, which never
changes, then locks every folder of that type in the ledger, its section of the sidebar,
before it reads anything else, then walks the new
parent's ancestors and refuses a cycle as `account-folder-cycle`, the same business error
as before. The lock is the row lock the repository already took on one folder, taken over
the section; a cycle can only form inside one type, since the parent key carries the type.

**Why the section, not the two folders a move names.** Two moves can name no folder in
common and still together close a cycle: A into B, while C, an ancestor of B, goes into D,
a descendant of A. Each passes its walk against the tree as it was. Locking only the
folder and its new parent, as the API did before, left that race to the trigger's
per-ledger lock. Under the section lock the second move waits for the first and then
walks the tree the first left, so it is refused.

**The other writers of a parent.** A new folder has no descendants, so creating one cannot
close a cycle; it keeps its lock on the parent alone. A delete moves its sub-folders up to
its own parent, an ancestor they already had, which removes ancestry and adds none; it
takes the section lock too, because it re-parents those sub-folders, and a lock on the
deleted folder alone could deadlock with a move holding them. Snapshot restore replays a
tree that was acyclic when it was captured, and never relied on the trigger, whose walk
stopped at any parent not yet inserted. MCP has no folder write.

**Consequences.** A direct SQL write that puts a folder inside its own sub-folder is no
longer refused; like the other invariants ADR-0032 moved to the API, that is the
operator's to get right. Moves and deletes in one section of one ledger now run one at a
time; another type's section, and another ledger, are not held up. The API no longer maps
`ck_account_folders_acyclic`, which nothing can raise.

Proven by `AccountFoldersEndpointsTests`: direct and indirect cycles refused, the two-move
race above (deterministic, and failing with the section lock narrowed to one row), a
delete waiting for a move elsewhere in its section, a nested tree surviving a snapshot
restore, and a catalog check that no trigger is left on the table.

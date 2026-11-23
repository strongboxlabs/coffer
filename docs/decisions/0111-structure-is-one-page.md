# 0111 — Structure: accounts, categories and tags are one page

* Status: Accepted (amends 0021, 0050, 0068, 0069, 0077, 0104); D3 and D5 amended 2026-11-04 (the owner's answer is the hook's; the review count counts register entries; a category's figures are in the ledger's display currency); amended 2026-11-13 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (D5: a category's total converts into the reporting currency)
* Date: 2026-11-03
* Related: [0017](0017-account-discriminator.md) (a category's kind is disclosed, never enforced),
  [0021](0021-ui-layout-and-principles.md) (layout, Rules 9 and 10),
  [0050](0050-account-editor-and-loan-amortization.md) (accounts management),
  [0058](0058-ui-ux-consolidation-pass.md) (shared primitives),
  [0068](0068-mcp-write-surface.md) (the category merge),
  [0069](0069-category-management-surface.md) (categories),
  [0077](0077-tag-management-surface.md) (tags),
  [0090](0090-ledger-management-entry.md) (the trail),
  [0099](0099-budget-as-guideposts-and-hand-rolled-charts.md) (what a category merge does to a budget target),
  [0104](0104-reports-are-a-place-you-go.md) (Reports becomes the seventh destination),
  [0106](0106-folders-replace-tabs.md) (folders and the rail),
  [0103](0103-tags-at-both-levels.md) (a tag on a transaction and on a posting),
  [0108](0108-memo-and-tags-per-side.md) (tags per side),
  [0109](0109-overview-is-an-attention-first-home.md) (the "View only" chip).

## Context

Accounts, categories and tags are the three lists that describe a ledger. Each had its own
destination, written by hand, and they drifted:

- Three page shells, three heads (two repeated the page title in the panel), three places to
  create (a page button, one button per kind, none).
- Row actions as a kebab and a context menu on two, a lone "Edit" button on the third.
- "Inactive" said in different words by the rail ("Show inactive accounts"), the two pages
  ("Show inactive") and the Overview ("Include closed accounts"), and loading, empty and error
  states drawn differently each time.
- No check of the reader's role. A reader who cannot edit was shown every create, edit, merge
  and delete control, and the server refused each one.

Some differences are the data's. An account carries history and cannot be deleted or merged;
a category has a kind and a tree; a tag is a flat dictionary whose entries are made when they
are first used. They belong to the lists. Everything else belongs to the page.

## Decision

### D1 — One destination, three tabs

**Structure** is one sidebar entry in place of three, opening on Accounts. The tabs are routes
under a layout with no path of its own, so each keeps the URL it had (`/accounts`,
`/categories`, `/tags`) and every existing link still lands where it did. The layout owns the
trail, the heading, the "View only" chip (D3) and the tab strip, which carries each tab's count
of rows: what the tab lists when it opens, so inactive rows are not in it and "Show inactive"
does not change it. The tab is state in the URL, not in the page.

### D2 — What lights the entry

Structure is current on `/accounts` (exact), `/categories` and `/tags`, and on a tag's page
`/tags/{tagId}` (D6), which sits outside the layout but is reached from the Tags tab; and not on
`/accounts/{id}`, which is a register. A destination link asks the router for an exact match:
the router's own idea of "active" is a prefix match, so the Overview's path would be announced
as the current page on every page in the ledger and `/accounts` on every register.

### D3 — The reader's role is read once and respected

One hook, `useLedgerRole`, answers from the ledger list every destination already reads.
The three tabs, the rail, the Overview, both registers and the ledger's settings (General,
Members, Bank feeds and Notifications) use it, and none derives the answer by hand. Each
rule has one definition, a pure predicate in `ledgerRole.ts` that the hook answers with:
`canWriteLedger` (an owner or editor writes; any other role, or one not yet known, does not)
gives `canEdit`, and `isLedgerOwner` (an owner; nothing else, an unknown role included)
gives `isOwner`, which decides the owner's controls in the ledger's settings and on the
Overview (the Feeds & checks Connect → and Reconnect → links and the owner's get-going
items). The Members list asks
`isLedgerOwner` of each member's role too. No other file compares a role with one of its
names; `LedgerRoleRule.test.ts` parses the SPA's source and fails on one that does. A
reader who is not an owner or editor sees the lists as they are, and no create, edit, merge,
delete or deactivate control, no row menu, and no "Remove unused". The role is unknown until
the ledger list answers, and unknown counts as "cannot edit": a control that appears a moment
late is a smaller fault than one that can only be refused. *(Amended 2026-11-04: the owner's answer joins the hook, and the settings panels
that read it by hand ask the hook.)*

### D4 — One row, one toolbar, one menu

Every tab is built from the same parts:

- **A toolbar:** search, "Show inactive" where the list has an inactive state (accounts and
  categories, not tags), and one "+ New" button. Categories lose the one-button-per-kind
  arrangement; the kind is a field in the dialog.
- **A row:** name, badges, secondary text, one count, an amount (categories), then the kebab.
  The name opens the register for accounts and categories, and the tag's transactions for
  tags. The count and the amount are always there, empty where a tab has nothing for them (the
  amount is held open from a medium screen up), so the count sits the same distance from the
  menu on every tab and flipping between tabs moves nothing. Secondary text is the one column a
  list may leave out.
- **A menu:** the kebab and a right-click open the same menu. Its first item repeats the name
  link, and Delete is last and set apart. Verbs are "Edit…" (accounts, tags) and "Rename…"
  (categories); accounts and categories gain Deactivate and Reactivate; accounts have no
  Delete. A row the user cannot act on (a system account or category) has no menu and keeps
  the width a menu would take, so its amounts line up with its neighbours'. The menu is named
  for its row, exposes the item the arrow keys are on, and hands focus back to where it opened
  from when it closes, whether by Esc, by a choice, or by the dialog a choice opened.
- **States:** loading, empty, no matches and error come from the shared components, with a
  retry on an error. A list that is empty because nothing is made yet offers the "+ New" a
  reader who can edit would have in the toolbar; one that is empty because the search found
  nothing offers to clear it. Ticking "Show inactive" keeps the rows already drawn until the
  longer list lands, so the list does not blink to a skeleton under the box being used. An
  action with no dialog of its own to fail in (Reactivate, Remove unused) says so above the
  list, and a refusal inside a confirm is announced.
- **A group heading:** the accounts of one type and the categories of one kind sit under one
  heading part, so the two lists set the label and the `(3)` count in the same type. What is a
  list's own is a prop: a group that can be folded away takes a toggle (the category kinds, which
  are long, and which start folded), and one with a figure takes a total beside it, outside the
  toggle. The Accounts groups are short, so they are plain and open.
- **Badges:** Inactive and System are one component.
- **Deactivating asks first; Reactivate does not.** Deactivating takes a row out of the pickers
  and the rail, so it is confirmed with what that means. Bringing one back changes nothing a
  reader would not expect, so it is not. A merged-away category is inactive, and Reactivate
  brings it back as an empty category (D8). The row menu (and the sidebar's) is the one way to
  change whether an account is active: the account editor has no Active checkbox, which used to
  deactivate on Save without the question, and saving it never changes the flag.
- **An editor saves only what it has read.** A save sends every text field in full, and an
  empty one clears it. The account editor fills those fields from a second read; if that read
  fails the fields are still the blanks the dialog opened with, so Save stays off and the
  dialog offers to try the read again.

### D5 — Counts

Accounts show the entries waiting for review in each account's register, which the list
already carries; balances stay in the rail. Categories show their transactions and total. A
category has no currency of its own, since its lines carry the paying account's
([ADR-0109](0109-overview-is-an-attention-first-home.md) D9), so every category's total is
formatted in the ledger's display currency, the one the budget month, the home screen and
Reports use, and a kind's total reads the rail's "mixed" marker, with the rail's title, when
the ledger's accounts span currencies, which is when the Budget's month reads "mixed". One
category's total keeps its figure then, as a budget row does. `GET /categories` carries the
currency beside the rows (`currencyCode`, `mixedCurrency`, by the derivation the budget month
uses), so a figure waits on no other read and never wears the code stored on a category's
account row. *(Amended 2026-11-04: the review count is the register's count of entries
awaiting review since the [ADR-0109](0109-overview-is-an-attention-first-home.md) amendment of
2026-11-03, so a transaction with two lines on one other account counts two there, where this
said transactions; and a total is no longer printed in USD, nor in the code stored on its
category, but in the ledger's display currency.)* *(Amended 2026-11-13,
[ADR-0114](0114-benchmarks-and-currency-conversion.md) D7 and D9: each category's total converts into the ledger's
reporting currency, each line at its transaction's date, and is null with
`rateUnavailable` when a rate is missing; `GET /categories` carries `currencyCode` and no
`mixedCurrency`.)*
Tags show **distinct transactions**: the
stored usage figure counts each pairing, so a transaction tagged on the transaction and on a
side counts twice (ADR-0103, ADR-0108), which is the wrong number for a label that now opens a
list of transactions.

The count is the distinct transactions the tag is in play on a line of: read from the stored
presentation (`txn_leg_note_tags`), the one place that says which tags a line shows once its
own tags, the ones it inherits from its category side, and a split's transaction-level ones are
resolved. So it is the number of transactions the tag's own page lists (D6), and it cannot say
more than the page can show. A line with no tags of its own shows its category side's, so those
count; a tag a line's own tags hide (the category side's, when the money line has tags of its
own) is not counted, nor is a hidden or merged-away transaction or a reminder's template, which
no register shows. The stored figure stays on the tag as `usageCount`, because "nothing points at
this tag" is what decides whether it is safe to remove, and the combobox that picks a tag shows
the distinct count too.

### D6 — Tags are made on the tab and open a list

A tag can be created from the Tags tab (`POST /tags`, under the rules rename already has:
trimmed, 64 characters, one name per ledger ignoring case, an optional `#rrggbb` colour; owners
and editors; `201` with the tag as the list reads it). Its name opens a page of the lines it is
in play for across every account (`GET /tags/{tagId}/transactions`): newest first, read-only,
fifty to a page with "Load more", narrowed by payee or memo, account and a date range (inclusive
at both ends, calendar days). A payee opens that transaction in its account's register, scrolled
to it. The page offers the owner or editor the tag's own edit dialog, and a merge made from it
says what it did on that page rather than moving to the list, which could not know a merge was
made here. A tag that no longer exists (deleted, or merged into another) is said so, with the
way back to the list; a tag whose read failed says so and offers to try again.

A row is a **register line**, not a transaction: the line an account's register shows the tag
on (ADR-0077, ADR-0108), read from its own side. A transfer tagged on both sides is a line in
each account, and one whose sides carry different tags of their own is a line under each tag in
the register that shows it, so the page agrees with every register it links to. The ledger-wide
listing the budget and MCP use reads both lines of a transfer from the sending side; this page
asks the query to read each from its own (`PresentFromOwnLeg`), which changes nothing for them.
The count beside the tag's name (D5) counts transactions; the page lists lines, each with its
account. Its "Other tags" column names the tags in play for the line besides this one.

The register is per account and a report is a total. A list that reads across accounts already
exists in the budget's drill-down, by category, and the reporting query behind it (and behind the
MCP `list_transactions` tool) already filters by tag, so the route is that query with a tag, not
a new one.

### D7 — What stays

Folders stay in the rail; the Accounts tab groups by type (ADR-0106 D4 stands). Delete rules
stay each list's own: an unused category, any tag, never an account.

### D8 — A category merge says what it moves and refuses a cycle

A merge moves a number somebody typed. A budget target (ADR-0099) follows the category it was
set on: where both categories hold one for a month the two are added. A category and what is
beneath it cannot both carry a target for a month, so where the merge would cause that, the
target on the lower category is dropped: a target held anywhere beneath the destination (the
merged category's sub-categories, which arrive under it, and the destination's own), for any
month the destination now holds one, and a target that arrived on the destination or beneath
it, for any month a category above the destination holds one. The merge dialog says
so once a destination is chosen, and the response carries `budgetTargetsMoved` (the source's
targets the destination kept) and `budgetTargetsDropped` so a caller does not have to infer it;
the MCP tool reports them too. A preview does not read them, and says zero.

Reactivating the merged-away category brings back an empty one: nothing records what moved, so
nothing moves back, and the dialog, the API and the MCP tool say so. The kinds may differ. A
category's kind cannot be edited (the server refuses it: `category-kind-immutable`), because the
flip would move every posting between the spending and income totals for all of its history, so
a merge into a category of the wanted kind is the one way to reclassify, and ADR-0017 discloses
a category's kind without enforcing it on a posting.

The merge reparents the source's children to the destination, so a destination beneath the
source would end up under itself. The picker leaves out what it can see beneath the source (the
accounts read leaves out a category under an inactive one), and the server refuses the request
(`category-cycle`, `422`) before it counts anything and again inside the merge's transaction. A
request that does not use the picker cannot make the cycle in one request. Two edits of the
tree racing each other are not serialised: the reparent takes no lock either, so a move that
lands between the merge's last read and its write is not caught. The row menu offers Merge
exactly when the dialog has a destination, by one shared rule (`canMergeAway`): an active
category that is neither the source nor beneath it, of any kind.

## Consequences

- The page titles "Accounts", "Categories" and "Tags" are replaced by "Structure" and a tab
  strip; the sidebar loses two entries.
- Guard tests that name the files that stay put (`CategoriesPanel`, `TagsPanel`) are
  unchanged. The two that depended on what moved were retargeted: `pinnedLengths` names
  `StructureRow` for the menu's spacer, and `tabFocusRing` recognises the router-link strip in
  `StructureLayout`. The two page shells (`CategoriesPage`, `TagsPage`) are deleted.
- The tag's `usageCount` stays on `TagDto` beside the new `transactionCount`; a caller that
  reads tags through the API (the web client, the MCP `list_tags` tool) sees both.
- The Tags tab is the only place a tag can be made that is not the act of tagging a transaction
  (the MCP `set_transaction_tags` tool still makes one on first use).
- A tag's page is a new route, `/ledgers/{ledgerId}/tags/{tagId}`, outside the Structure layout:
  it is a place you go from a tag, and its trail leads back to the tab.

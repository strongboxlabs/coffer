# 0091 — One category taxonomy for Demo and new ledgers

* Status: Accepted (amended 2026-11-18: 108 categories, nested to three levels, with a third kind, `adjustment`, carried by a fail-closed `coffer_category_kind` key)
* Date: 2026-08-03
* Narrows: [ADR-0071](0071-install-provisioning-ui-import-authed-restore.md) D5 (starter categories)
* Relates: [ADR-0017](0017-account-discriminator.md) (categories are accounts), [ADR-0088](0088-setup-asks-one-question.md) (the Demo brings its own categories; added from the hub since 2026-11-18, [ADR-0115](0115-the-demo-is-generated-imported-and-seeded-from-the-hub.md))

## Context

Creating a ledger produced a different set of categories from the one you had
just been exploring in Demo.

| Path | Source | Count |
|---|---|---|
| New ledger | hand-written `starter-categories.json` | 61 |
| Demo | `moneydance-export-demo.json`'s own tree | 108 |

Both seeders were faithful — 61 in the catalogue produced exactly 61 rows — so
nothing was truncated. The sets were simply **unrelated taxonomies**: only about
6 of ~22 top-level names overlapped. The starter tree had `Housing`, `Utilities`,
`Transportation`, `Food`, `Health`; Demo had `Bills`, `Automotive`, `Personal`,
`Healthcare`, `Tax`, `Loan`, `Childcare`, `Pet Care`. A user who explored Demo
and then made their own ledger found the vocabulary changed underneath them.

Two things that look like defects are **not**, and must not be "fixed":

- **`Investment` appearing twice is not a duplicate.** One is `expense` (child:
  `Trading Commission`), the other `income` (children: `Dividends`,
  `Interest Received`, and the capital-gains splits). Income and expense are
  separate namespaces; the repeat is correct modelling.
- **`Bank Charge` is not a transaction type.** Bank fees are a legitimate expense
  category, with sensible children (`Interest Paid`, `Service Charges`).

The Moneydance-derived tree is the better of the two: properly
hierarchical, 108 entries, and it covers ground the starter set missed entirely
(tax breakdown, loan interest vs principal, childcare, pet care).

## Decision

**The demo export is the single source of truth. The starter catalogue is
generated from it.**

1. **Adopt the Moneydance-derived tree**, cleaned. `data/samples/starter-categories.gen.mjs`
   projects the export's income/expense accounts into the shape
   `StarterCategoriesSeeder` embeds. Regenerate with
   `node data/samples/starter-categories.gen.mjs`.

2. **Five rows removed, one renamed** — only where the MD model does not survive
   translation to double-entry, or where a name is actively ambiguous:

   | Change | Reason |
   |---|---|
   | drop `ATM Withdrawal` + `Cash`, `Service Charge` | MD books an ATM withdrawal as an *expense*. In Coffer it is a **transfer** (bank → cash), so the category cannot be correct here. Its `Service Charge` child duplicated `Bank Charge > Service Charges`. |
   | drop `Initial Balance` (income) | An opening-balance mechanism. Booked as *income* it overstates income in every report. |
   | drop `Personal > Restaurant` | Duplicated `Personal > Dining`. |
   | rename `Bills > Gas` → `Bills > Natural Gas` | Ambiguous against `Automotive > Fuel`; this one is the utility. |

   **108 → 103.** Deliberately conservative: the per-parent `Misc` children and
   the `Miscellaneous` catch-all are untidy but not wrong, so they stay. Restyling
   a working taxonomy is not the same as fixing it.

3. **Verified safe before removal.** Only 4 of Demo's 108 categories are
   referenced by its 23 transactions (`Investment > Trading Commission`,
   `Investment > Dividends`, `Investment > Interest Received`, `Miscellaneous`) —
   none of them removed. The edit script refuses to delete an account whose id
   appears anywhere outside its own row.

## Consequences

**A generator is not enough, so there is a test.** Nothing stops someone editing
the export and forgetting to regenerate, or hand-editing the JSON.
`StarterCategoryParityTests` compares the two *files* and fails with the exact
difference plus the regeneration command. An injected bogus category fails it —
a parity test that cannot fail is worse than none.

The same test pins the two corrections above: it asserts `Investment` exists
under **both** kinds, so a future "dedupe by name" cannot quietly collapse it.

**Existing ledgers keep their categories.** This changes what *new* ledgers and
*new* Demo imports get. No migration rewrites an existing tree — deleting or
renaming categories a user already has transactions against would be destructive,
and the whole point of ADR-0017 is that categories are accounts with history.
Installs created before this will still show the old 61/108 split.

**Demo is now exemplary, not merely realistic.** It is what a new ledger looks
like, populated — which is the only way "explore Demo, then start your own books"
is an honest onboarding path.

## Amendment, 2026-11-18 — the tree grows, nests to three levels, and has a third kind

The taxonomy lacked shapes an ordinary household ledger needs: a utility line for
broadband beside cable, somewhere for baby supplies under childcare, a home for a
valuation adjustment, and any branch deeper than parent → child. Five rows are
added to `data/samples/moneydance-export-demo.base.json`; none moves or is
renamed, and every correction above stands. **103 → 108.**

| Added | Kind | Note |
|---|---|---|
| `Bills > Internet` | expense | A leaf beside `Cable`, which is not renamed. |
| `Childcare > Baby Supplies` | expense | `Childcare` becomes a parent and still takes postings directly. |
| `Valuation Adjustment` | adjustment | Top level. The third kind (ADR-0017, mig 224): a markup moves an asset's value and is neither income nor spending. |
| `Vacation > Travel > Airfare` | expense | The first third level. `Travel` keeps direct postings. |
| `Vacation > Travel > Car Rental` | expense | Third level. |

**The override key.** Moneydance has no third category kind, so the adjustment
row stays a type `e` category and carries `"coffer_category_kind": "adjustment"`.
The importer reads it (`MdAcct.CofferCategoryKind`) and `AccountMapper` turns that
row's kind from `expense` into `adjustment`. A new type code was rejected: an
unknown code is dropped as `UnknownTypeCode` and its postings are skipped, which
fails open, while a type `e` row is still skipped by the import's balance check as
every category is.

**It fails closed.** The key is allowed only with the value `"adjustment"`, only on
a type `e` row, and only at the top level (no parent, or MD's root). Any other
value, the key on any other row type (checked before a root, a security or an
unknown code is skipped), or the key on a child category throws
`InvalidDataException` and fails the import, so nothing of it is committed.
`starter-categories.gen.mjs` refuses the same cases.

**Nested starter JSON.** `starter-categories.json` was a list of roots with string
children that inherited the root's kind. It is now a tree of nodes, each carrying
its own kind (`income`, `expense` or `adjustment`). The generator emits it
recursively in a fixed kind order (income, expense, adjustment), alphabetically
within a kind at every level; it dies on a tree deeper than three levels, on a
child whose kind differs from its parent's, and on a repeated name within a kind
and parent at any level. `StarterCategoriesSeeder` seeds the tree recursively and
refuses a branch that mixes kinds before writing anything.

**Parity at every depth.** `StarterCategoryParityTests` keys each entry by its full
segment path and its kind, and applies the override on the export side as the
importer does. It also asserts that an `adjustment` entry and
`Vacation > Travel > Airfare` exist: without those, losing the override on both
sides, or flattening both trees, would still compare equal. Parity compares the
generator's output, the export the C# Demo generator writes from the embedded base
(`DemoExportGenerator`, read through the tests' fixed-date short span), with the
starter JSON (`starter-categories.gen.mjs`, projected from the same base). The
Demo side is generated on every run, so it can never be stale: a base edit fails
parity until the starter JSON is regenerated.

Existing ledgers are untouched, as above: no migration adds these rows to a tree a
user already has.

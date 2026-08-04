# 0091 — One category taxonomy for Demo and new ledgers

* Status: Accepted
* Date: 2026-08-03
* Narrows: [ADR-0071](0071-install-provisioning-ui-import-authed-restore.md) D5 (starter categories)
* Relates: [ADR-0017](0017-account-discriminator.md) (categories are accounts), [ADR-0088](0088-setup-asks-one-question.md) (Demo is an opt-in at setup)

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

Two claims made while diagnosing this were **wrong**, and are recorded so they
are not "fixed" later:

- **`Investment` appearing twice is not a duplicate.** One is `expense` (child:
  `Trading Commission`), the other `income` (children: `Dividends`,
  `Interest Received`, and the capital-gains splits). Income and expense are
  separate namespaces; the repeat is correct modelling.
- **`Bank Charge` is not a transaction type.** Bank fees are a legitimate expense
  category, with sensible children (`Interest Paid`, `Service Charges`).

Inspected in full, the Moneydance-derived tree is the better of the two: properly
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
difference plus the regeneration command. It was verified by injecting a bogus
category and confirming the failure, then regenerating — a parity test that
cannot fail is worse than none.

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

#!/usr/bin/env node
// =============================================================================
// starter-categories.gen.mjs — derive the starter category tree from the demo
// sample's curated core, so a new ledger and the Demo ledger cannot disagree
// (ADR-0091).
// =============================================================================
//
// Two separately maintained taxonomies — a hand-written starter list for a new
// ledger, and whatever the demo export happens to contain — would make "the
// categories I saw in Demo" and "the categories my own ledger has" unrelated
// sets.
//
// The Demo's curated core, data/samples/moneydance-export-demo.base.json, is the
// single source of truth: it carries the curated Moneydance-derived tree, the C#
// Demo generator writes it into every Demo, and this script projects it into the
// shape the API's StarterCategoriesSeeder embeds. Regenerate after editing the base:
//
//     node data/samples/starter-categories.gen.mjs
//
// Verifies (and fails loudly) rather than trusting:
//   * every category has a kind of income, expense or adjustment. Moneydance
//     has no third kind, so an adjustment category is a type 'e' row carrying
//     "coffer_category_kind": "adjustment" — allowed only on a top-level 'e'
//     row (no parent, or MD's root), with that one value; the importer
//     applies the same rule
//     (AccountMapper, ADR-0091 amendment of 2026-11-18)
//   * a child has its parent's kind (the seeder refuses a mixed branch)
//   * nesting is at most three levels deep (root -> child -> grandchild)
//   * no duplicate name WITHIN a kind + parent (a name may legitimately appear
//     under both kinds — "Investment" is an expense parent for Trading
//     Commission AND an income parent for Dividends; that is correct modelling,
//     not a duplicate)
// =============================================================================

import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
// The BASE, not a generated Demo: the curated tree lives there, and the Demo
// is generated in C# (src/Api/Provisioning/Demo/) at creation, never committed.
const exportPath = join(here, 'moneydance-export-demo.base.json');
const outPath = join(here, '..', '..', 'src', 'Api', 'Provisioning', 'starter-categories.json');

const die = (msg) => {
    console.error(`starter-categories.gen: ${msg}`);
    process.exit(1);
};

const md = JSON.parse(readFileSync(exportPath, 'utf8'));

// Moneydance account types: 'i' income, 'e' expense. Everything else (bank,
// investment, security, …) is a real account, not a category.
const MD_KIND = { i: 'income', e: 'expense' };
const OVERRIDE_KEY = 'coffer_category_kind';
const KIND_ORDER = ['income', 'expense', 'adjustment'];
const MAX_DEPTH = 3;

const accounts = md.all_items.filter((x) => x.obj_type === 'acct');
const byId = new Map(accounts.map((a) => [a.id, a]));
const isCategory = (id) => Boolean(id && MD_KIND[(byId.get(id) || {}).type]);
// The importer's "top level": no parent, or MD's root (type 'r'). Stricter than
// "parent is not a category", which would also admit a row hung under a real
// account; the importer refuses the key there, so this must too.
const isUnderRoot = (a) => !a.parentid || (byId.get(a.parentid) || {}).type === 'r';

// The override is checked on EVERY account, not just categories: the importer
// refuses the key on a non-'e' row, so the starter tree must refuse it too.
for (const a of accounts) {
    if (!(OVERRIDE_KEY in a)) continue;
    const where = `acct ${a.id} '${a.name}'`;
    if (a.type !== 'e') die(`${where}: ${OVERRIDE_KEY} on a type '${a.type}' row; only a top-level expense category may carry it`);
    if (!isUnderRoot(a)) die(`${where}: ${OVERRIDE_KEY} on a row whose parent is not MD's root; only a top-level expense category may carry it`);
    if (a[OVERRIDE_KEY] !== 'adjustment') die(`${where}: ${OVERRIDE_KEY} '${a[OVERRIDE_KEY]}' is not supported; only "adjustment"`);
}

const KIND = (a) => (OVERRIDE_KEY in a ? a[OVERRIDE_KEY] : MD_KIND[a.type]);
const categories = accounts.filter((a) => MD_KIND[a.type]);

if (categories.length === 0) die('no income or expense category accounts found — wrong export shape?');

// Roots have no parent, or a parent that is not itself a category (MD hangs the
// top level off a root account).
const roots = categories.filter((c) => !isCategory(c.parentid));
const childrenOf = (id) => categories.filter((c) => c.parentid === id);

// Fixed kind order, then alphabetical: stable output, so a regeneration
// produces a reviewable diff rather than a reshuffle.
const byKindThenName = (a, b) =>
    a.kind === b.kind
        ? a.name.localeCompare(b.name)
        : KIND_ORDER.indexOf(a.kind) - KIND_ORDER.indexOf(b.kind);

const emit = (acct, depth, path) => {
    const segments = [...path, acct.name];
    const label = segments.join(' > ');
    if (depth > MAX_DEPTH) die(`"${label}" is ${depth} levels deep; the starter tree allows ${MAX_DEPTH}`);
    const node = { name: acct.name, kind: KIND(acct) };
    const kids = childrenOf(acct.id).map((c) => emit(c, depth + 1, segments)).sort(byKindThenName);
    for (const kid of kids) {
        if (kid.kind !== node.kind) die(`"${label} > ${kid.name}" is ${kid.kind} under a ${node.kind} parent`);
    }
    checkDuplicates(kids, `"${label}"`);
    if (kids.length) node.children = kids;
    return node;
};

// Duplicate check, scoped per kind and per parent (see header note about
// "Investment"), at every level.
function checkDuplicates(nodes, under) {
    for (const kind of KIND_ORDER) {
        const names = nodes.filter((n) => n.kind === kind).map((n) => n.name);
        const dupes = names.filter((n, i) => names.indexOf(n) !== i);
        if (dupes.length) die(`duplicate ${kind} categories under ${under}: ${[...new Set(dupes)].join(', ')}`);
    }
}

const tree = roots.map((r) => emit(r, 1, [])).sort(byKindThenName);
checkDuplicates(tree, 'the top level');

const count = (nodes) => nodes.reduce((n, t) => n + 1 + count(t.children ?? []), 0);
const total = count(tree);

const payload = {
    '//':
        'GENERATED by data/samples/starter-categories.gen.mjs from ' +
        'data/samples/moneydance-export-demo.base.json — do not hand-edit. The Demo\'s curated core is the ' +
        'single source of truth so a new ledger and the Demo ledger get the SAME categories ' +
        '(ADR-0091). Each node carries its kind (income, expense or adjustment); a child has ' +
        'its parent\'s kind. Seeded on the empty-new-ledger paths; the Demo path gets these ' +
        'same categories via the import instead.',
    categories: tree,
};

writeFileSync(outPath, JSON.stringify(payload, null, 2) + '\n', 'utf8');
console.log(
    `starter-categories.gen: wrote ${total} categories ` +
    `(${tree.length} top-level, ${total - tree.length} below) to ${outPath}`,
);

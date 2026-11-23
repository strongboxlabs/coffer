/**
 * Which side of a posting a register is looking at, and the words for it.
 *
 * A register shows the COUNTERPARTY of each leg: an account's register faces
 * categories, and a category's register faces money accounts. This module is
 * the one place that turns that into words.
 *
 * The defect it prevents: a column header, a counterparty field or a sort menu
 * saying "Category" on a register whose counterparties are accounts. A
 * hand-written `facingAccounts ? 'Account' : 'Category'` ternary per file fixes
 * one surface and misses the rest. A surface that names a counterparty takes a
 * `CounterpartyKind` and gets its words here, so a new one cannot hard-code the
 * noun without visibly ignoring the prop it was handed.
 */

export type CounterpartyKind = 'category' | 'account';

export interface CounterpartyWords {
    /** Title case, for a label or heading: "Category" / "Account". */
    noun: 'Category' | 'Account';
    /** Mid-sentence: "category" / "account". */
    lower: 'category' | 'account';
    /** "categories" / "accounts" — irregular, so not derivable from `lower`. */
    plural: 'categories' | 'accounts';
    /** With its article, which also differs: "a category" / "an account". */
    withArticle: 'a category' | 'an account';
}

const WORDS: Readonly<Record<CounterpartyKind, CounterpartyWords>> = {
    category: { noun: 'Category', lower: 'category', plural: 'categories', withArticle: 'a category' },
    account: { noun: 'Account', lower: 'account', plural: 'accounts', withArticle: 'an account' },
};

export function counterpartyWords(kind: CounterpartyKind): CounterpartyWords {
    return WORDS[kind];
}

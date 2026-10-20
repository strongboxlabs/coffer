/**
 * Which side of a posting a register is looking at, and the words for it.
 *
 * A register shows the COUNTERPARTY of each leg: an account's register faces
 * categories, and a category's register faces money accounts. This module is
 * the one place that turns that into words.
 *
 * It exists because the same defect shipped three times. The register's column
 * header, the editor's counterparty field and the sort menu each said
 * "Category" on a register whose counterparties are accounts, and each was
 * fixed on its own — by a hand-written `facingAccounts ? 'Account' :
 * 'Category'` ternary, copied into whichever file had just been caught. Five
 * more surfaces had never been given one. A surface that names a counterparty
 * now takes a `CounterpartyKind` and gets its words here, so a new one cannot
 * hard-code the noun without visibly ignoring the prop it was handed.
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

import { ownMemo, ownTags } from '@/lib/annotations/notes';
import { classifySimpleFinDescription } from '@/lib/ingest/simpleFinDescriptionClassifier';
import type { InvestmentRow, LedgerInvestmentAction } from '@/lib/types';

import { hintToDraft } from './hintToDraft';
import type { InvestmentInheritedNotes } from './inheritedAfterSave';
import { legsToDraft } from './legsToDraft';
import type { InvestmentTxnDraft } from './validation';

/** What the investment editor's edit mode opens with. */
export interface InvestmentEditContext {
    headerId: string;
    initialDraft: InvestmentTxnDraft;
    /** ADR-0031 Phase 3d.2: the provider's ticker for this feed row, so the
     *  save records it against the security the user picks. */
    providerSecurityHint?: { providerKey: string; providerSecurityId: string };
    needsReview: boolean;
    inherited: InvestmentInheritedNotes;
}

/**
 * The investment editor's edit context for one transaction, from its full leg
 * set (GET …/transactions/{id}/legs, every account's legs): the draft the editor
 * seeds once, the provider hint, the review state and the inherited notes.
 *
 * Null when there is nothing to edit: no legs yet (the editor must wait for
 * them, because its draft seeds once), or a manual bank-shape row, which has no
 * place in an investment register and whose PATCH the server refuses.
 *
 * Two shapes:
 *   * An investment-shape row (its legs carry posting roles, ADR-0029): the
 *     legs invert into a draft.
 *   * A feed row that landed bank-shape (ADR-0031 Phase 3): seeded from the
 *     classifier's hint when there is one, with a blank action when there is
 *     not, plus the provider's ticker when it is known.
 *
 * The caller keeps its own read-only gate; this does not check.
 */
export function investmentEditContext(
    headerId: string,
    accountId: string,
    legs: readonly InvestmentRow[] | undefined,
): InvestmentEditContext | null {
    if (!legs || legs.length === 0) return null;
    // The leg on the brokerage being viewed, else the first (the server orders
    // by leg_index). Header-level data, projected per leg by the view, so any
    // leg carries the freshest ingest_* state.
    const canonical = legs.find((l) => l.accountId === accountId) ?? legs[0]!;

    // The brokerage's own memo and tags: the event's on a multi-posting
    // event, the brokerage leg's on a one-posting row — blank while it
    // inherits the other side's, which the editor then shows as the
    // fields' placeholder.
    const headerFields = {
        postedAt: canonical.postedAt,
        transactedAt: canonical.transactedAt,
        payee: canonical.payee,
        memo: ownMemo(canonical),
        checkNumber: canonical.checkNumber,
        tags: ownTags(canonical),
    };
    const inherited: InvestmentInheritedNotes = {
        memo: canonical.inheritedMemo,
        tags: canonical.inheritedTags,
        fromAccountId: canonical.inheritFromAccountId,
        // The role of the leg that value is on, which decides whether a save
        // keeps it (inheritedAfterSave.ts): null for a feed row's leg, which
        // has none yet.
        fromRole: legs.find((l) => l.accountId === canonical.inheritFromAccountId)?.postingRole ?? null,
    };

    if (canonical.investmentAction === null) {
        // A feed row opens with the classifier's action when it has one, and
        // blank when it has none (no classifier knows every institution's
        // format). A manual bank-shape row stays closed.
        const isFeedImported = canonical.origin !== 'manual';
        const hasHint = canonical.ingestActionHint !== null;
        if (!hasHint && !isFeedImported) return null;

        const seedAction = canonical.ingestActionHint as LedgerInvestmentAction | null;
        const draft = hintToDraft(
            seedAction,
            accountId,
            // The brokerage's currency, read off its own leg; with no leg on it,
            // none, and nothing is rounded (ADR-0114 D15).
            legs.find((l) => l.accountId === accountId)?.currencyCode ?? null,
            headerFields,
            legs,
            canonical.ingestSecurityId,
            // Mig 113: the file's shares, unit price and fee, so an OFX
            // investment row opens fully populated.
            canonical.ingestShares,
            canonical.ingestUnitPrice,
            canonical.ingestFee,
            // Mig 228: the file's authoritative total. Without it a
            // cash-neutral row (a reinvest) has its amount rebuilt from
            // shares × price and opens a cent adrift.
            canonical.ingestAmount,
        );
        // Every provider persists its ticker hint at ingest (mig 114), so the
        // Accept records a mapping under the identifier the next ingest looks
        // up. A SimpleFIN row synced before mig 114 has none persisted, and the
        // payee is classified again to recover one. Without a hint no mapping
        // is recorded; the hint is optional on the server, so it never blocks
        // the save.
        const persistedHint = canonical.ingestSecurityTickerHint;
        const fallbackHint = persistedHint === null
            && canonical.providerKey === 'simplefin'
                ? classifySimpleFinDescription(canonical.payee).tickerHint
                : null;
        const resolvedHint = persistedHint ?? fallbackHint;
        const providerSecurityHint = resolvedHint && canonical.providerKey
            ? {
                providerKey: canonical.providerKey,
                providerSecurityId: resolvedHint,
            }
            : undefined;
        return {
            headerId,
            initialDraft: draft,
            providerSecurityHint,
            needsReview: canonical.needsReview,
            inherited,
        };
    }

    // Investment-shape: the legs carry posting roles; invert them.
    const draft = legsToDraft(
        canonical.investmentAction as LedgerInvestmentAction,
        accountId,
        headerFields,
        legs,
    );
    return {
        headerId,
        initialDraft: draft,
        needsReview: canonical.needsReview,
        inherited,
    };
}

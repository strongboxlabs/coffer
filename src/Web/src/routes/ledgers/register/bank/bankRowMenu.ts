import type { BankRow } from '@/lib/types';
import type { ContextMenuItem } from '@/components/ui/ContextMenu';
import { isInvestmentOwnedRow } from './columns';

/** Callbacks the per-row context menu fires. The page owns the
 *  mutations / state these dispatch into. */
interface BankRowMenuActions {
    /** Clear the bank-feed `needs_review` flag (approve as-is). */
    onApprove: (headerId: string) => void;
    /** Open the inline editor on `target`'s header. Same `startEdit` the
     *  row's double-click and the keyboard's Enter both reach. */
    onEdit: (target: BankRow) => void;
    /** Open the new-transaction form prefilled from `target`. */
    onDuplicate: (target: BankRow) => void;
    /** Open the reminder editor prefilled from `target` (ADR-0051 slice C). */
    onCreateReminder: (target: BankRow) => void;
    /** Navigate to the counterparty account's register. */
    onShowOtherSide: (counterpartyAccountId: string, headerId: string) => void;
    /** Open the destructive single-row confirm dialog for `target`. */
    onRequestDelete: (target: BankRow) => void;
    /** Open the diagnostic raw-payload modal for `target`. */
    onShowRawData: (target: BankRow) => void;
}

/**
 * Build the per-row context-menu items for the bank register. `target`
 * is the row the user right-clicked; hidden / merged-into rows are
 * filtered by the API, so everything that reaches here is actionable.
 *
 * Read-only rows (investment-owned OR split counter-side) only get the
 * read-only `Show other side` item — mutating actions belong on the
 * canonical-owner register so the user can see the full impact before
 * confirming.
 *
 * Pure builder (extracted from `BankRegisterPage` verbatim): no JSX,
 * no hooks — the page passes its action callbacks in and renders the
 * returned items via `<ContextMenu>`.
 */
export function buildBankRowMenuItems(
    target: BankRow,
    actions: BankRowMenuActions,
    opts?: {
        /** The target is a multi-leg split-parent that ORIGINATES in
         *  this account (ADR-0036) — editable, NOT a read-only
         *  counter-side. Its `txnGroupId` is non-null (every split
         *  header is), so without this flag the counter-side guard
         *  below would misclassify it as read-only. A split has no
         *  single "other side", so the parent offers Edit (opens the
         *  multi-leg editor), Duplicate (the page passes every leg, so
         *  it clones the whole split), Create reminder and Delete
         *  (removes the whole header). */
        originatingSplit?: boolean;
        /**
         * This register belongs to a CATEGORY, so nothing here may open the
         * new-transaction editor.
         *
         * Duplicate does exactly that, prefilled — and the editor authors
         * against the register's own account, which here is a category. A
         * transaction is created against a money account and categorised,
         * never authored inside a category, so the form would be unfillable.
         * The "+ New transaction" button is hidden for the same reason; this
         * is the other door into the same room.
         */
        noAuthoring?: boolean;
    },
): ContextMenuItem[] {
    const items: ContextMenuItem[] = [];
    if (opts?.originatingSplit) {
        if (target.needsReview) {
            items.push({
                id: 'accept',
                label: 'Accept',
                onSelect: () => actions.onApprove(target.headerId),
            });
        }
        // Edit opens the multi-leg editor on the whole split. Until this
        // item existed, a split parent had NO menu route to its own legs
        // and Enter was a no-op, so the only way in was a double-click.
        items.push({
            id: 'edit',
            label: 'Edit',
            onSelect: () => actions.onEdit(target),
            shortcutHint: 'Enter',
        });
        // Duplicate clones the WHOLE split (the page passes every leg),
        // so it's offered here just like on a single row.
        if (!opts?.noAuthoring) items.push({
            id: 'duplicate',
            label: 'Duplicate',
            onSelect: () => actions.onDuplicate(target),
            shortcutHint: '⌘D',
        });
        items.push({
            id: 'create-reminder',
            label: 'Create reminder',
            onSelect: () => actions.onCreateReminder(target),
        });
        items.push({
            id: 'delete',
            label: 'Delete',
            danger: true,
            onSelect: () => actions.onRequestDelete(target),
            shortcutHint: 'Del',
        });
        return items;
    }
    const isInvestmentOwnedTarget = isInvestmentOwnedRow(target);
    const isSplitCounterTarget =
        !isInvestmentOwnedTarget && target.txnGroupId !== null;
    const isCrossDomainTarget =
        isInvestmentOwnedTarget || isSplitCounterTarget;
    // Slice 2c: Accept appears at the top when the row carries
    // the bank-feed review flag — discoverability for the
    // primary action on a freshly-synced row. Matches the
    // editor's primary button label for the same operation.
    // Cross-domain rows never carry the bank-feed review flag,
    // but the explicit guard documents the layer boundary.
    if (target.needsReview && !isCrossDomainTarget) {
        items.push({
            id: 'accept',
            label: 'Accept',
            onSelect: () => actions.onApprove(target.headerId),
        });
    }
    // Edit, offered wherever the row is editable FROM THIS REGISTER. The
    // split-parent branch above has always had it and the investment register
    // has always had it; a plain bank row — much the commonest row in the app —
    // did not, so the only ways in were a double-click or Enter, neither of
    // which the menu advertised.
    //
    // Gated on cross-domain rather than on `noAuthoring`: that flag is about
    // AUTHORING a new transaction on a category register, which Duplicate does
    // and editing an existing row does not.
    if (!isCrossDomainTarget) {
        items.push({
            id: 'edit',
            label: 'Edit',
            onSelect: () => actions.onEdit(target),
            shortcutHint: 'Enter',
        });
    }
    if (!isCrossDomainTarget && !opts?.noAuthoring) {
        items.push({
            id: 'duplicate',
            label: 'Duplicate',
            onSelect: () => {
                // Open the new-transaction form prefilled with
                // the source's payee / memo / amount /
                // counterparty. posted_at defaults to today in
                // the form's own initial-state derivation.
                actions.onDuplicate(target);
            },
            shortcutHint: '⌘D',
        });
        items.push({
            id: 'create-reminder',
            label: 'Create reminder',
            onSelect: () => actions.onCreateReminder(target),
        });
    }
    items.push({
        id: 'show-other-side',
        label: 'Show other side',
        onSelect: () => {
            if (target.counterpartyAccountId === null) return;
            actions.onShowOtherSide(target.counterpartyAccountId, target.headerId);
        },
        disabled: target.counterpartyAccountId === null,
    });
    // Diagnostic, and offered on EVERY row including the read-only ones — the
    // question it answers ("why did this import like this?") is asked most often
    // of a row you cannot edit from here. Same position as the investment
    // register's: after Show other side, before Delete.
    //
    // It was investment-only, which was never a decision: the modal was declared
    // inside that page and typed on its row. Most feed rows land on a BANK
    // register, so the side that needed it more was the side without it.
    items.push({
        id: 'raw',
        label: 'Show raw data',
        onSelect: () => actions.onShowRawData(target),
    });
    if (!isCrossDomainTarget) {
        items.push({
            id: 'delete',
            label: 'Delete',
            danger: true,
            onSelect: () => actions.onRequestDelete(target),
            shortcutHint: 'Del',
        });
    }
    return items;
}

import type { ContextMenuItem } from '@/components/ui/ContextMenu';

/**
 * The row-menu items that read rather than write, by id: the two every row
 * offers in both registers (bankRowMenu.ts, InvestmentRegisterPage's menu).
 */
const READ_ITEMS: ReadonlySet<string> = new Set(['show-other-side', 'raw']);

/**
 * A register row's menu for the user's role: all of it for an owner or an
 * editor, and only the items that read for a viewer (Show other side, Show
 * raw data). Filtering the built menu, rather than building a second one,
 * keeps the read items' order and wiring the editor's.
 */
export function rowActionsFor(
    items: readonly ContextMenuItem[],
    canEdit: boolean,
): readonly ContextMenuItem[] {
    return canEdit ? items : items.filter((item) => READ_ITEMS.has(item.id));
}

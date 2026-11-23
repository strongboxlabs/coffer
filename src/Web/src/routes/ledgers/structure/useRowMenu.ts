import { useCallback, useState } from 'react';

import type { MenuAnchor } from './StructureRow';

/**
 * Which row's menu is open, and where. A list holds one: `open(row, anchor)` from a row's
 * kebab or right-click, `close()` when the menu closes, and `menu` is what to draw while it
 * is open.
 */
export function useRowMenu<T>() {
    const [menu, setMenu] = useState<{ row: T; anchor: MenuAnchor } | null>(null);
    const open = useCallback((row: T, anchor: MenuAnchor) => setMenu({ row, anchor }), []);
    const close = useCallback(() => setMenu(null), []);
    return { menu, open, close };
}

import { useParams } from '@tanstack/react-router';

import { CategoriesPanel } from '../settings/CategoriesPanel';

/** The Categories tab of Structure: the category tree for the ledger in the URL. */
export function CategoriesTab() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };
    return <CategoriesPanel ledgerId={ledgerId} />;
}

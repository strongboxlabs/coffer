import { useParams } from '@tanstack/react-router';

import { TagsPanel } from '../settings/TagsPanel';

/** The Tags tab of Structure: the tag dictionary for the ledger in the URL. */
export function TagsTab() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };
    return <TagsPanel ledgerId={ledgerId} />;
}

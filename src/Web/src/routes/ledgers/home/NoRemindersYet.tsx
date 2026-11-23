import { Link } from '@tanstack/react-router';

import { useHome } from './homeContext';

/**
 * A home widget's line for a ledger with no active reminder: what reminders are
 * for, and, for an owner or editor, where to create one. Bills and Cash ahead say it
 * in the same words; a viewer reads the sentence alone.
 */
export function NoRemindersYet() {
    const home = useHome();
    return (
        <p className="p-4 text-sm text-text-muted">
            No reminders yet. Reminders turn recurring bills into one-click posts
            {home.canEdit ? (
                <>
                    {' · '}
                    <Link to="/ledgers/$ledgerId/reminders" params={{ ledgerId: home.ledgerId }} className="font-medium text-accent hover:underline">
                        Create reminder →
                    </Link>
                </>
            ) : '.'}
        </p>
    );
}

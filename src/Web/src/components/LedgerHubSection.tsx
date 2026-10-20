import { type ReactNode } from 'react';
import { ChevronDown } from 'lucide-react';

import { cn } from '@/lib/cn';
import { usePersistedDisclosure } from '@/lib/usePersistedDisclosure';
import { Panel, PanelHead } from '@/components/ui/Panel';

/**
 * Reusable collapsible section for the Ledger Hub page (slice A3).
 *
 * Each entity type the ledger owns (Accounts, Categories, Securities,
 * future: Payees, Tags, Rules) renders one of these. The contract:
 *
 *   Header — chevron + title + count badge  |  `Manage … →` action
 *   Body   — caller-supplied content (collapsible)
 *
 * The single `headerAction` slot is the per-section escape hatch
 * to the full management page (or, for entity types whose manage
 * page hasn't shipped yet, a disabled placeholder so the pattern
 * is visible). Keeping the link in the header — not at the bottom
 * — avoids a long scroll past a 100+ row list just to drill in.
 *
 * Collapsed/expanded state persists in localStorage keyed by
 * `${ledgerId}:${sectionKey}` so each ledger remembers its own
 * layout. URL-hash was considered and rejected — gets noisy as
 * sections multiply, and a per-ledger preference is more useful
 * than a per-URL one.
 *
 * Bigger architectural notes live in roadmap.md → A3.
 */
export interface LedgerHubSectionProps {
    /** Stable identifier for the section ("accounts", "securities", …).
     *  Used as the localStorage key suffix; must not change across
     *  renders or the persisted collapsed/expanded state is lost. */
    sectionKey: string;
    /** Ledger scope for the persisted state — two ledgers can have
     *  independent collapse preferences for the same section. */
    ledgerId: string;
    /** Header label. Shown in title case. */
    title: string;
    /** Optional count badge next to the title. Hidden when undefined.
     *  When paired with {@link totalCount}, renders as "loaded / total"
     *  so users see how far they've scrolled through a paginated list. */
    count?: number;
    /** Optional total — when set AND distinct from `count`, the badge
     *  renders as `count / totalCount`. When unset (or equal), just
     *  `count`. */
    totalCount?: number;
    /** Section body — typically the full or top-N list, or a stat block. */
    children: ReactNode;
    /** Header-aligned action (typically `Manage <things> →` link).
     *  Rendered in the header strip so it's reachable without
     *  scrolling past the section body. */
    headerAction?: ReactNode;
    /** Initial collapsed state on first mount (before localStorage
     *  has been read). Defaults to expanded. */
    defaultExpanded?: boolean;
}

export function LedgerHubSection({
    sectionKey,
    ledgerId,
    title,
    count,
    totalCount,
    children,
    headerAction,
    defaultExpanded = true,
}: LedgerHubSectionProps) {
    // The lazy-init + try/catch + persist dance moved to
    // `usePersistedDisclosure` when the Categories screen needed the same
    // behaviour (FU-060). Same key, so existing preferences carry over.
    const [expanded, toggle] = usePersistedDisclosure(
        `coffer.hub.${ledgerId}.${sectionKey}.expanded`, defaultExpanded);
    const bodyId = `${sectionKey}-body`;

    return (
        <Panel>
            <PanelHead>
                <button
                    type="button"
                    onClick={toggle}
                    aria-expanded={expanded}
                    aria-controls={bodyId}
                    className="flex flex-1 items-center gap-2 text-left text-sm font-semibold hover:text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-1"
                >
                    <ChevronDown
                        className={cn(
                            'size-icon-md text-text-muted transition-transform',
                            expanded ? 'rotate-0' : '-rotate-90',
                        )}
                        aria-hidden
                    />
                    <span>{title}</span>
                    {count !== undefined ? (
                        <span className="rounded bg-surface-muted px-1.5 py-0.5 font-mono text-[0.6875rem] font-normal tabular-nums text-text-muted">
                            {totalCount !== undefined && totalCount !== count
                                ? `${count} / ${totalCount}`
                                : count}
                        </span>
                    ) : null}
                </button>
                {headerAction !== undefined ? (
                    <div className="ml-2 flex shrink-0 items-center text-xs">
                        {headerAction}
                    </div>
                ) : null}
            </PanelHead>
            {expanded ? <div id={bodyId}>{children}</div> : null}
        </Panel>
    );
}

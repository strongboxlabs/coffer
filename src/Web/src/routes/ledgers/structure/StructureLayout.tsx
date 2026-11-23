import { useEffect, useRef } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, Outlet, useLocation, useParams } from '@tanstack/react-router';

import { fetchAccounts, fetchTags } from '@/lib/api';
import { useLedgerRole } from '@/lib/useLedgerRole';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';

/**
 * `/ledgers/:ledgerId/accounts`, `/categories` and `/tags` — the Structure destination
 * (ADR-0111): the three lists that describe a ledger, one page with a tab for each.
 * This layout owns what the tabs share, the trail, the heading, the reader's role and
 * the tab strip. Each tab renders its own list into the outlet.
 *
 * The tabs are routes, not state, so each keeps the URL it had before the three pages
 * were joined and every existing link still lands where it did.
 */
const TABS = [
    { id: 'accounts', label: 'Accounts', to: '/ledgers/$ledgerId/accounts', suffix: '/accounts' },
    { id: 'categories', label: 'Categories', to: '/ledgers/$ledgerId/categories', suffix: '/categories' },
    { id: 'tags', label: 'Tags', to: '/ledgers/$ledgerId/tags', suffix: '/tags' },
] as const;

/**
 * What each tab lists when it opens, which is what its count says: inactive rows are left
 * out, the way each tab starts, and the number does not follow a tab's own "Show inactive".
 * A tab whose read has not answered shows no count rather than a zero it has not earned.
 *
 * Two reads, not three. The accounts read also carries every category, so the Categories
 * count comes from it: asking /categories instead would run its per-category usage
 * aggregate, a pass over every leg in the ledger, to print one number. The Accounts tab
 * reads under this same key, so on that tab the two are one request.
 */
function useTabCounts(ledgerId: string) {
    const accounts = useQuery({
        queryKey: ['accounts', ledgerId, { includeInactive: false }],
        queryFn: () => fetchAccounts(ledgerId, { includeInactive: false }),
    });
    const tags = useQuery({
        queryKey: ['tags', ledgerId],
        queryFn: () => fetchTags(ledgerId),
    });
    const rows = accounts.data;
    return {
        accounts: rows?.filter((a) => a.accountType !== 'category').length,
        categories: rows?.filter((a) => a.accountType === 'category').length,
        tags: tags.data?.length,
    } as const;
}

export function StructureLayout() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };
    const { pathname } = useLocation();
    const { ledger, canEdit } = useLedgerRole(ledgerId);
    const counts = useTabCounts(ledgerId);

    // The scroll container belongs to this layout, so it outlives a change of tab; left alone, a
    // tab would open at the previous tab's offset, with the heading and the strip scrolled away.
    const paneRef = useRef<HTMLElement>(null);
    useEffect(() => {
        if (paneRef.current) paneRef.current.scrollTop = 0;
    }, [pathname]);

    const current = TABS.find((t) => {
        const path = `/ledgers/${ledgerId}${t.suffix}`;
        return pathname === path || pathname.startsWith(`${path}/`);
    });

    return (
        <MainArea>
            <TopBar>
                <Breadcrumb
                    items={[
                        {
                            label: ledger?.name ?? 'Ledger',
                            node: ledger ? (
                                <Link
                                    to="/ledgers/$ledgerId"
                                    params={{ ledgerId }}
                                    className="hover:text-text"
                                >
                                    {ledger.name}
                                </Link>
                            ) : (
                                'Ledger'
                            ),
                        },
                        { label: 'Structure' },
                    ]}
                />
            </TopBar>
            <MainPane ref={paneRef}>
                <div className="mx-auto max-w-5xl space-y-4 p-5">
                    <header className="space-y-1">
                        <div className="flex flex-wrap items-center justify-between gap-3">
                            <h1 className="text-xl font-semibold tracking-tight">Structure</h1>
                            {/* Said once, here, for the three tabs below: the Overview words it the same way. */}
                            {ledger && !canEdit ? (
                                <span className="rounded-full bg-chip-neutral px-2 py-0.5 text-xs font-medium text-chip-neutral-text">
                                    View only
                                </span>
                            ) : null}
                        </div>
                        <p className="text-sm text-text-muted">
                            Accounts, categories and tags for this ledger.
                        </p>
                    </header>

                    <nav aria-label="Structure sections" className="flex items-end border-b border-border text-xs">
                        {TABS.map((t) => (
                            <Link
                                key={t.id}
                                to={t.to}
                                params={{ ledgerId }}
                                className="-mb-px border-b-2 px-3 py-1.5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent"
                                activeProps={{ className: 'border-accent font-semibold text-text' }}
                                inactiveProps={{ className: 'border-transparent text-text-muted hover:text-text' }}
                            >
                                <span className="inline-flex items-center gap-1.5">
                                    {t.label}
                                    {counts[t.id] !== undefined ? (
                                        <span className="font-normal tabular-nums text-text-subtle">{counts[t.id]}</span>
                                    ) : null}
                                </span>
                            </Link>
                        ))}
                    </nav>

                    {/* The heading used to say which list this was. It says Structure on all three
                        now, so the outline names the tab here, for a reader who navigates by headings. */}
                    {current ? <h2 className="sr-only">{current.label}</h2> : null}
                    <Outlet />
                </div>
            </MainPane>
        </MainArea>
    );
}

import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from '@tanstack/react-router';

import { fetchVisibleLedgers } from '@/lib/api';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';

import { CategoriesPanel } from './settings/CategoriesPanel';

/**
 * `/ledgers/:ledgerId/categories` — the Categories destination (ADR-0069 nav
 * swap: promoted from a Settings tab to a top-level nav surface). Page shell
 * (breadcrumb + heading) around the reused {@link CategoriesPanel}; mirrors the
 * other per-ledger destinations (Securities, Reminders, …).
 */
export function CategoriesPage() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };
    const ledgersQuery = useQuery({
        queryKey: ['ledgers'],
        queryFn: fetchVisibleLedgers,
    });
    const ledger = ledgersQuery.data?.find((l) => l.id === ledgerId);

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
                        { label: 'Categories' },
                    ]}
                />
            </TopBar>
            <MainPane>
                <div className="mx-auto max-w-5xl space-y-4 p-5">
                    <header>
                        <h1 className="text-xl font-semibold tracking-tight">
                            Categories
                        </h1>
                        <p className="mt-0.5 text-sm text-text-muted">
                            Income and expense categories for this ledger.
                        </p>
                    </header>
                    <CategoriesPanel ledgerId={ledgerId} />
                </div>
            </MainPane>
        </MainArea>
    );
}

import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from '@tanstack/react-router';

import { fetchVisibleLedgers } from '@/lib/api';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';

import { TagsPanel } from './settings/TagsPanel';

/**
 * `/ledgers/:ledgerId/tags` — the Tags destination. Page shell
 * (breadcrumb + heading) around the reused {@link TagsPanel}; mirrors the
 * other per-ledger dictionary/destination pages (Categories, Securities,
 * Reminders, …).
 */
export function TagsPage() {
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
                        { label: 'Tags' },
                    ]}
                />
            </TopBar>
            <MainPane>
                <div className="mx-auto max-w-5xl space-y-4 p-5">
                    <header>
                        <h1 className="text-xl font-semibold tracking-tight">Tags</h1>
                        <p className="mt-0.5 text-sm text-text-muted">
                            Labels you can apply to transactions across this ledger.
                        </p>
                    </header>
                    <TagsPanel ledgerId={ledgerId} />
                </div>
            </MainPane>
        </MainArea>
    );
}

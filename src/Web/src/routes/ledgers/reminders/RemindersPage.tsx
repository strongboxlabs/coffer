import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from '@tanstack/react-router';

import { fetchVisibleLedgers } from '@/lib/api';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';

import { RemindersListPanel } from './RemindersListPanel';
import { RemindersUpcomingPanel } from './RemindersUpcomingPanel';

type ReminderView = 'upcoming' | 'list';

/**
 * `/ledgers/:ledgerId/reminders` (ADR-0049 R1). Moneydance-parity reminders
 * hub: an Upcoming view (a full-width calendar whose occurrence chips open a
 * Post/Skip popover, the default) and a List view (all series, incl. paused).
 * Authoring (create/edit) is R2.
 */
export function RemindersPage() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };
    const ledgersQuery = useQuery({ queryKey: ['ledgers'], queryFn: fetchVisibleLedgers });
    const ledger = ledgersQuery.data?.find((l) => l.id === ledgerId);

    const [view, setView] = useState<ReminderView>('upcoming');

    return (
        <MainArea>
            <TopBar>
                <Breadcrumb
                    items={[
                        {
                            label: ledger?.name ?? 'Ledger',
                            node: ledger ? (
                                <Link to="/ledgers/$ledgerId" params={{ ledgerId }} className="hover:text-text">
                                    {ledger.name}
                                </Link>
                            ) : 'Ledger',
                        },
                        { label: 'Reminders' },
                    ]}
                />
            </TopBar>
            <MainPane>
                <div className="mx-auto max-w-5xl space-y-4 p-5">
                    <header>
                        <h1 className="text-xl font-semibold tracking-tight">Reminders</h1>
                        <p className="mt-0.5 text-sm text-text-muted">
                            Recurring transactions that post on a schedule.
                        </p>
                    </header>

                    <nav role="tablist" aria-label="Reminders views"
                        className="flex items-end border-b border-border text-xs">
                        <ViewTab label="Upcoming" selected={view === 'upcoming'} onSelect={() => setView('upcoming')} />
                        <ViewTab label="List" selected={view === 'list'} onSelect={() => setView('list')} />
                    </nav>

                    {view === 'upcoming'
                        ? <RemindersUpcomingPanel ledgerId={ledgerId} />
                        : <RemindersListPanel ledgerId={ledgerId} />}
                </div>
            </MainPane>
        </MainArea>
    );
}

function ViewTab({ label, selected, onSelect }: { label: string; selected: boolean; onSelect: () => void }) {
    return (
        <button
            type="button"
            role="tab"
            aria-selected={selected}
            onClick={onSelect}
            className={`-mb-px border-b-2 px-3 py-1.5 ${selected
                ? 'border-accent font-semibold text-text'
                : 'border-transparent text-text-muted hover:text-text'}`}
        >
            {label}
        </button>
    );
}

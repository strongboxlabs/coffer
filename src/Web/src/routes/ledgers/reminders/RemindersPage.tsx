import { useRef, useState, type Ref } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router';

import { fetchReminders, fetchSchedule, saveSchedule } from '@/lib/api';
import { useLedgerRole } from '@/lib/useLedgerRole';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { ScheduleControl } from '@/components/ScheduleControl';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';

import { ReminderEditorDialog } from './ReminderEditorDialog';
import { RemindersListPanel } from './RemindersListPanel';
import type { RemindersSearch } from './remindersSearch';
import { RemindersUpcomingPanel } from './RemindersUpcomingPanel';

type ReminderView = 'upcoming' | 'list';

/**
 * `/ledgers/:ledgerId/reminders` (ADR-0049 R1). Moneydance-parity reminders
 * hub: an Upcoming view (a full-width calendar whose occurrence chips open a
 * Post/Skip popover, the default) and a List view (all series, incl. paused).
 * Authoring (create/edit) is R2.
 *
 * A viewer reads the calendar and the list and writes nothing (ADR-0023 §L; the
 * server's row-level security refuses a viewer's reminder writes): no New reminder,
 * Edit, Skip, Disable or Enable, no Post or skip from a chip, no restore of a
 * skipped one, and no auto-post schedule to change.
 */
export function RemindersPage() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };
    const { ledger, canEdit } = useLedgerRole(ledgerId);

    const [view, setView] = useState<ReminderView>('upcoming');

    // `?reminder=<id>` opens that series' editor, as the list's Edit does: a link
    // elsewhere (Cash ahead's footnote) names the reminder it is about. The param
    // holds the dialog open, so closing it clears the param, and Back does not
    // reopen it. An id that names no series opens nothing and says nothing. The
    // editor writes, and nothing presents a series read-only, so for a viewer the
    // param opens nothing either.
    const search = useSearch({ strict: false }) as RemindersSearch;
    const navigate = useNavigate();
    const reminders = useQuery({
        queryKey: ['reminders', ledgerId],
        queryFn: () => fetchReminders(ledgerId),
        enabled: canEdit && search.reminder !== undefined,
    });
    const linked = canEdit && search.reminder !== undefined && reminders.data?.some((r) => r.id === search.reminder)
        ? search.reminder
        : null;
    // The page has no other search to keep, so clearing it is clearing the param.
    const closeLinked = () => void navigate({ to: '.', search: {}, replace: true });
    // Nothing on the page opened the editor, so on close focus goes to the view
    // that is showing, never to the page.
    const selectedTab = useRef<HTMLButtonElement | null>(null);

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

                    {/*
                        HERE rather than in Settings, where every other ScheduleControl
                        lives. The other scheduled jobs — quotes, snapshots, feed sync —
                        have no per-item counterpart, so Settings is the only place they
                        could be. This one does: a reminder carries its own "Auto-post /
                        N days before", and that switch does nothing until the ledger's
                        job is also on. Putting the two on different pages is how a user
                        ticks Auto-post, waits for rent to post itself, and finds out it
                        never did.
                    */}
                    {canEdit ? (
                        <div className="rounded border border-border bg-surface p-3">
                            <ScheduleControl
                                queryKey={['schedule', ledgerId, 'reminder-auto-post']}
                                load={() => fetchSchedule(ledgerId, 'reminder-auto-post')}
                                save={(body) => saveSchedule(ledgerId, 'reminder-auto-post', body)}
                                label="Due reminders post automatically each day"
                                note="only reminders that set their own Auto-post option and can post; one with no lines, an inactive account or an investment shape is skipped and announced"
                                enabledElsewhere
                            />
                        </div>
                    ) : null}

                    <nav role="tablist" aria-label="Reminders views"
                        className="flex items-end border-b border-border text-xs">
                        <ViewTab label="Upcoming" selected={view === 'upcoming'} onSelect={() => setView('upcoming')}
                            tabRef={view === 'upcoming' ? selectedTab : undefined} />
                        <ViewTab label="List" selected={view === 'list'} onSelect={() => setView('list')}
                            tabRef={view === 'list' ? selectedTab : undefined} />
                    </nav>

                    {view === 'upcoming'
                        ? <RemindersUpcomingPanel ledgerId={ledgerId} canEdit={canEdit} />
                        : <RemindersListPanel ledgerId={ledgerId} canEdit={canEdit} />}
                </div>
                {linked ? (
                    <ReminderEditorDialog
                        ledgerId={ledgerId}
                        reminderId={linked}
                        onClose={closeLinked}
                        // The editor refreshes the reminder reads itself, and closes.
                        onSaved={() => undefined}
                        fallbackFocusRef={selectedTab}
                    />
                ) : null}
            </MainPane>
        </MainArea>
    );
}

function ViewTab({ label, selected, onSelect, tabRef }: {
    label: string;
    selected: boolean;
    onSelect: () => void;
    tabRef?: Ref<HTMLButtonElement>;
}) {
    return (
        <button
            ref={tabRef}
            type="button"
            role="tab"
            aria-selected={selected}
            onClick={onSelect}
            className={`-mb-px border-b-2 px-3 py-1.5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent ${selected
                ? 'border-accent font-semibold text-text'
                : 'border-transparent text-text-muted hover:text-text'}`}
        >
            {label}
        </button>
    );
}

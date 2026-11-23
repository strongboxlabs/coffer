import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type DragEvent, type MutableRefObject, type ReactNode } from 'react';
import { Link, useBlocker, useParams } from '@tanstack/react-router';

import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { ContextMenu, type ContextMenuAnchor } from '@/components/ui/ContextMenu';
import { InsertionLine } from '@/components/ui/InsertionLine';
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel';
import { SegmentedControl } from '@/components/ui/SegmentedControl';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';
import { useRowRemovalFocus } from '@/components/ui/rowRemovalFocus';
import { Skeleton } from '@/components/ui/Skeleton';
import { cn } from '@/lib/cn';
import { todayInputValue } from '@/lib/dates';
import { errorMessage } from '@/lib/errorMessage';
import { useLedgerRole } from '@/lib/useLedgerRole';

import { catalogEntry, CATALOG, parseReportTileId, REPORT_TILES_MAX, type Column, type ColumnsMode, type WidgetKey } from './dashboardCatalog';
import {
    columnWidgets,
    defaultLayout,
    hiddenWidgets,
    reportTile,
    sameLayout,
    shownCount,
    withoutWidget,
    type Layout,
    type LayoutWidget,
    type WidgetSettings,
} from './dashboardLayout';
import { HomeContext, widgetDomId, type HomeContextValue } from './homeContext';
import { HomeHeader } from './HomeHeader';
import { useFeedConnections, useRailAccounts, useReminderList } from './queries';
import { resolveTile, tileChoices, useSavedReports } from './reportTiles';
import { moved, restoredWidget, useLayout, withWidget } from './useLayout';
import { WidgetHost } from './WidgetHost';

const COLUMN_OPTIONS = [
    { value: 'wide-narrow' as const, label: 'Wide + narrow' },
    { value: 'equal' as const, label: 'Equal' },
    { value: 'single' as const, label: 'Single' },
];

const DRAG_TYPE = 'application/x-coffer-widget';

/**
 * The ledger home screen (ADR-0109): attention first — what is broken,
 * what is waiting — with where the ledger stands in the narrower column beside it.
 * Each widget reads its own data and fails on its own; the layout is the reader's,
 * changed in place.
 */
export function HomePage() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };
    const today = todayInputValue();
    const { ledger, canEdit, isOwner } = useLedgerRole(ledgerId);
    const prefs = useLayout(ledgerId);
    const accounts = useRailAccounts(ledgerId);

    const [draft, setDraft] = useState<Layout | null>(null);
    // A subject-first widget added from the tray, and the draft from before it was.
    const [pendingAdd, setPendingAdd] = useState<{ id: string; before: Layout } | null>(null);
    // Each open settings panel's draft, by widget id. Here rather than in the widget:
    // the columns are separate stacks, so a widget moved to the other one mounts
    // afresh there, and a panel held inside it would close, losing what was chosen.
    const [panels, setPanels] = useState<Readonly<Record<string, WidgetSettings>>>({});
    const [confirm, setConfirm] = useState<null | 'reset' | 'cancel'>(null);
    const [announcement, setAnnouncement] = useState('');
    const [focusTo, setFocusTo] = useState<FocusTarget | null>(null);
    const editing = draft !== null;
    // Done's save is in flight. Its success clears the draft, so a change made to
    // the draft meanwhile would be dropped: the edit surface refuses changes
    // until it lands (ADR-0023 §L).
    const saving = editing && prefs.save.isPending;
    const layout = draft ?? prefs.layout;
    const dirty = draft !== null && !sameLayout(draft, prefs.layout);
    // What the columns draw: the default, read-only, when the stored layout failed.
    const drawn = useMemo(() => (prefs.query.isError ? defaultLayout() : layout), [prefs.query.isError, layout]);
    const budgetWindowMonths = Number(drawn.widgets.find((w) => w.key === 'budget-pace')?.settings.windowMonths ?? 3);

    // A report tile is called by its report's name, which only the saved reports
    // know for a saved one: read while a saved tile is in the layout, or while the
    // edit is open and "Add a report…" may offer them.
    const savedTiles = layout.widgets.some((w) => w.key === 'report' && parseReportTileId(w.id)?.kind === 'saved');
    const saved = useSavedReports(ledgerId, editing || savedTiles);
    const titleOf = (w: LayoutWidget): string => {
        if (w.key !== 'report') return catalogEntry(w.key)!.title;
        const tile = resolveTile(w.id, saved.data);
        return tile.state === 'found' ? tile.report.name : catalogEntry('report')!.title;
    };

    // Customise and the edit bar take each other's place (ADR-0023 §L).
    // Customise goes as the edit starts, and focus goes to the edit bar's first
    // control. Every way out of the edit (Done, Escape, Cancel, Discard, Reset,
    // the stale layout's answers) takes away the control that held focus, and
    // focus goes to Customise. Passive, so it runs after a closing dialog has
    // given focus back as far as it could; focus the user has put somewhere
    // else stays.
    const customiseRef = useRef<HTMLButtonElement | null>(null);
    const editBarRef = useRef<HTMLDivElement | null>(null);
    const wasEditing = useRef(editing);
    useEffect(() => {
        const changed = wasEditing.current !== editing;
        wasEditing.current = editing;
        if (!changed) return;
        const active = document.activeElement;
        if (active !== null && active !== document.body) return;
        if (editing) editBarRef.current?.querySelector<HTMLElement>(TABBABLE)?.focus();
        else customiseRef.current?.focus();
    }, [editing]);

    // A widget hidden from its menu, or from its edit frame's Hide, takes that
    // control with it: focus goes to the same control on the next widget drawn
    // (the main column, then the side), else the previous one, else Customise
    // (for the menu) or the hidden tray's first Add (for Hide), never to the page.
    const widgetOrder = useMemo(
        () => [...columnWidgets(drawn, 'main'), ...columnWidgets(drawn, 'side')].map((w) => w.id),
        [drawn],
    );
    const trayAdd = useMemo(() => ({ get current() { return document.querySelector<HTMLElement>('[data-widget-add]'); } }), []);
    const actionsControl = useRowRemovalFocus(widgetOrder, customiseRef);
    const hideControl = useRowRemovalFocus(widgetOrder, trayAdd);

    // A panel whose widget is no longer drawn (hidden, an add taken back, an edit
    // discarded) closes with it, so the widget shown again later opens without it.
    useEffect(() => {
        const shown = new Set(drawn.widgets.filter((w) => w.visible).map((w) => w.id));
        setPanels((p) => (Object.keys(p).every((id) => shown.has(id)) ? p : withoutPanels(p, (id) => !shown.has(id))));
    }, [drawn]);

    // The shape the columns had, per browser, so the next load's placeholders
    // match it before the stored layout arrives.
    useEffect(() => {
        if (prefs.query.isSuccess) rememberShape(ledgerId, prefs.layout);
    }, [ledgerId, prefs.query.isSuccess, prefs.layout]);

    // Focus follows a moved widget, goes into a settings panel that opens, and lands
    // on what is left when one closes, once the render that did it is on the page.
    useEffect(() => {
        if (!focusTo) return;
        const target = focusElement(focusTo) ?? document.getElementById(widgetDomId(focusTo.id));
        target?.focus();
        setFocusTo(null);
    }, [focusTo, layout]);

    // Stable callbacks over a ref: the blocker re-registers whenever they change.
    // Written after commit, before the browser paints, so no event sees it stale.
    const dirtyRef = useRef(dirty);
    useLayoutEffect(() => { dirtyRef.current = dirty; }, [dirty]);
    const isDirty = useCallback(() => dirtyRef.current, []);
    const blocker = useBlocker({ shouldBlockFn: isDirty, enableBeforeUnload: isDirty, withResolver: true });

    const startEditing = () => { if (prefs.writable) setDraft(prefs.layout); };
    const leave = () => { setDraft(null); setConfirm(null); setPendingAdd(null); };
    const change = (next: (current: Layout) => Layout) => {
        if (editing) setDraft((d) => (d ? next(d) : d));
        else if (prefs.writable) prefs.save.mutate(next(prefs.layout));
    };
    const otherColumn = (id: string): Column =>
        layout.widgets.find((w) => w.id === id)?.column === 'main' ? 'side' : 'main';
    const saveSettings = (id: string, settings: WidgetSettings) =>
        change((l) => withWidget(l, id, (w) => ({ ...w, settings })));

    const context: HomeContextValue = {
        ledgerId,
        today,
        canEdit,
        isOwner,
        editing,
        layoutSaving: saving,
        layoutWritable: prefs.writable,
        budgetWindowMonths,
        hide: (id) => change((l) => withWidget(l, id, (w) => ({ ...w, visible: false }))),
        remove: (id) => {
            // The frame goes, its Remove with it: focus goes to the next frame, else
            // the previous one, else the tray's "Add a report…", never to the page.
            const at = widgetOrder.indexOf(id);
            const next = at < 0 ? undefined : widgetOrder[at + 1] ?? widgetOrder[at - 1];
            change((l) => withoutWidget(l, id));
            setFocusTo(next ? { id: next, on: 'frame' } : { id: 'report', on: 'add' });
        },
        titleOf,
        actionsControl,
        hideControl,
        moveToOtherColumn: (id) => change((l) => moved(l, id, { column: otherColumn(id) })),
        customise: startEditing,
        saveSettings,
        panelDraft: (id) => panels[id] ?? null,
        openSettings: (id) => {
            const w = drawn.widgets.find((x) => x.id === id);
            if (!w) return;
            setPanels((p) => ({ ...p, [id]: w.settings }));
            // Whatever opened it (a menu that is closing, the gear) holds focus no longer.
            setFocusTo({ id, on: 'settings' });
        },
        setPanelDraft: (id, settings) => setPanels((p) => (id in p ? { ...p, [id]: settings } : p)),
        closeSettings: (id, apply) => {
            const kept = panels[id];
            if (apply && kept) saveSettings(id, kept);
            setPanels((p) => withoutPanels(p, (x) => x === id));
            // Only applying keeps an add that needs a subject.
            const takenBack = pendingAdd?.id === id && !apply;
            if (pendingAdd?.id === id) {
                if (takenBack) setDraft((d) => (d ? restoredWidget(d, pendingAdd.before, id) : d));
                setPendingAdd(null);
            }
            // The panel held focus and is going, so focus moves to what is left: the
            // widget's frame while editing, its actions otherwise, or its Add in the
            // tray when the add is taken back. Left on the page itself, it would be
            // outside the page's own Escape, which ends the edit.
            setFocusTo({ id, on: takenBack ? 'add' : editing ? 'frame' : 'actions' });
        },
    };

    // Edit mode's moves, each announced and, unless focus goes elsewhere, followed by it.
    const announceMove = (next: Layout, id: string, follow = true) => {
        const w = next.widgets.find((x) => x.id === id)!;
        const peers = columnWidgets(next, w.column);
        setAnnouncement(`${titleOf(w)} moved to ${w.column === 'main' ? 'main column' : 'side column'}, ${peers.findIndex((p) => p.id === id) + 1} of ${peers.length}`);
        if (follow) setFocusTo({ id, on: 'frame' });
    };
    const editMove = (id: string, how: Parameters<typeof moved>[2]) => {
        // A drop or Alt+arrow while Done saves is refused, as the move buttons are.
        if (!draft || saving) return;
        const next = moved(draft, id, how);
        setDraft(next);
        announceMove(next, id);
    };

    const noAccounts = accounts.isSuccess && !accounts.data.some((a) => a.accountType !== 'category');
    const visible = useMemo(
        () => new Set<WidgetKey>(layout.widgets.filter((w) => w.visible).map((w) => w.key)),
        [layout],
    );

    function done() {
        if (!draft) return;
        // An add whose settings were never applied is taken back: a widget that needs
        // a subject shows nothing without one.
        const next = pendingAdd ? restoredWidget(draft, pendingAdd.before, pendingAdd.id) : draft;
        setPendingAdd(null);
        if (sameLayout(next, prefs.layout)) { setDraft(null); return; }
        setDraft(next);
        prefs.save.mutate(next, { onSuccess: () => setDraft(null) });
    }

    return (
        <MainArea>
            <TopBar>
                <Breadcrumb items={[{ label: ledger?.name ?? 'Ledger' }]} />
            </TopBar>
            <MainPane>
                <HomeContext.Provider value={context}>
                    <div
                        className="@container/page"
                        onKeyDown={(e) => {
                            // An Escape something inside already handled (a settings
                            // panel's Cancel, a picker closing its list) is not this one.
                            // While Done saves, Escape refuses as Cancel does.
                            if (e.key === 'Escape' && editing && !confirm && !e.defaultPrevented && !prefs.save.isPending) {
                                if (dirty) setConfirm('cancel');
                                else leave();
                            }
                        }}
                    >
                        {editing ? (
                            <EditBar
                                barRef={editBarRef}
                                columns={layout.columns}
                                onColumns={(columns) => setDraft((d) => (d ? { ...d, columns } : d))}
                                onReset={() => setConfirm('reset')}
                                onCancel={() => (dirty ? setConfirm('cancel') : leave())}
                                onDone={done}
                                saving={saving}
                                error={prefs.save.isError && !prefs.stale ? errorMessage(prefs.save.error, 'Could not save the layout.') : null}
                            />
                        ) : null}
                        <div className="mx-auto max-w-fixed-1600px space-y-4 p-4 @2xl/page:p-5">
                            <HomeHeader
                                visible={visible}
                                onCustomise={startEditing}
                                customiseRef={customiseRef}
                                // Unavailable once the read failed; while it runs, Customise
                                // waits on it and is pending.
                                customiseDisabled={prefs.failed}
                                customisePending={prefs.loading}
                                showCustomise={!editing && !noAccounts}
                            />
                            {prefs.query.isError ? (
                                <div role="alert" className="flex flex-wrap items-center justify-between gap-2 border border-state-warning/40 bg-state-warning-soft px-3 py-2 text-sm text-state-warning">
                                    <span>Your Overview layout couldn&apos;t load; showing the default. Customise is unavailable until it loads.</span>
                                    <Button variant="link" size="xs" pending={prefs.query.isFetching}
                                        onClick={() => void prefs.query.refetch()}>Retry</Button>
                                </div>
                            ) : null}
                            {noAccounts ? (
                                <GetGoing />
                            ) : prefs.query.isPending ? (
                                <ShapePlaceholder ledgerId={ledgerId} />
                            ) : (
                                <>
                                    {editing ? (
                                        <HiddenTray
                                            layout={layout}
                                            pending={saving}
                                            saved={saved}
                                            titleOf={titleOf}
                                            onRemove={(id) => {
                                                setDraft((d) => (d ? withoutWidget(d, id) : d));
                                                setFocusTo({ id: 'report', on: 'add' });
                                            }}
                                            onAddReport={(id) => {
                                                // A report hidden earlier comes back with its settings, and
                                                // any other is a new tile; either lands at the end of the
                                                // report's catalog column, as the tray's Add places a widget.
                                                const kept = layout.widgets.some((w) => w.id === id);
                                                const shown = kept
                                                    ? withWidget(layout, id, (w) => ({ ...w, visible: true }))
                                                    : { ...layout, widgets: [...layout.widgets, reportTile(id)] };
                                                const next = moved(shown, id, { column: catalogEntry('report')!.column });
                                                setDraft(next);
                                                announceMove(next, id);
                                            }}
                                            onAdd={(id) => {
                                                const added = layout.widgets.find((w) => w.id === id)!;
                                                const entry = catalogEntry(added.key)!;
                                                const shown = withWidget(layout, id, (w) => ({ ...w, visible: true }));
                                                const next = moved(shown, id, { column: entry.column });
                                                setDraft(next);
                                                // A widget that needs a subject lands with its settings
                                                // open and focus in them; until they are applied the add
                                                // can be taken back.
                                                if (entry.subjectFirst) {
                                                    setPendingAdd({ id, before: layout });
                                                    setPanels((p) => ({ ...p, [id]: added.settings }));
                                                    setFocusTo({ id, on: 'settings' });
                                                }
                                                announceMove(next, id, !entry.subjectFirst);
                                            }}
                                        />
                                    ) : null}
                                    <Columns
                                        layout={drawn}
                                        editing={editing}
                                        saving={saving}
                                        onMove={editMove}
                                        onHide={(id) => context.hide(id)}
                                    />
                                </>
                            )}
                        </div>
                    </div>
                    <div aria-live="polite" className="sr-only">{announcement}</div>
                </HomeContext.Provider>

                <ConfirmDialog
                    open={confirm === 'reset'}
                    title="Reset the Overview to its default layout?"
                    body="Every widget goes back to its default place, visibility and settings."
                    confirmLabel="Reset"
                    isConfirming={prefs.reset.isPending}
                    onConfirm={() => prefs.reset.mutate(undefined, { onSuccess: leave })}
                    onCancel={() => setConfirm(null)}
                />
                <ConfirmDialog
                    open={confirm === 'cancel'}
                    title="Discard your layout changes?"
                    body="The Overview goes back to how it was before you started editing."
                    confirmLabel="Discard"
                    onConfirm={leave}
                    onCancel={() => setConfirm(null)}
                />
                <ConfirmDialog
                    open={blocker.status === 'blocked'}
                    title="Leave without saving your layout?"
                    body="Your changes to the Overview layout are not saved yet."
                    confirmLabel="Leave"
                    onConfirm={() => blocker.proceed?.()}
                    onCancel={() => blocker.reset?.()}
                />
                <ConfirmDialog
                    open={prefs.stale !== null}
                    title="Your layout changed somewhere else"
                    body="Another save came first, perhaps in another tab. Keep the layout you have here, or reload the one that was saved."
                    confirmLabel="Keep mine"
                    cancelLabel="Reload theirs"
                    isConfirming={prefs.save.isPending}
                    onConfirm={() => prefs.keepMine(() => setDraft(null))}
                    onCancel={() => {
                        prefs.reloadTheirs();
                        setDraft(null);
                    }}
                />
            </MainPane>
        </MainArea>
    );
}

/** Where focus goes next: a widget's edit frame, its Add in the hidden tray, the
 *  actions button in its head, or into its settings panel. */
interface FocusTarget { id: string; on: 'frame' | 'add' | 'actions' | 'settings' }

const TABBABLE = 'a[href], button, input, select, textarea, [tabindex]';

function focusElement({ id, on }: FocusTarget): HTMLElement | null {
    if (on === 'frame') return document.querySelector(`[data-widget-frame="${id}"]`);
    if (on === 'add') return document.querySelector(`[data-widget-add="${id}"]`);
    if (on === 'actions') return document.querySelector(`[data-widget-head="${id}"] [aria-haspopup="menu"]`);
    // The panel's first control a Tab would reach, unless that is a picker, whose
    // list opens on focus: then the panel itself, and the next Tab reaches it.
    const panel = document.querySelector<HTMLElement>(`[data-widget-settings="${id}"]`);
    if (!panel) return null;
    const first = Array.from(panel.querySelectorAll<HTMLElement>(TABBABLE))
        .find((el) => el.tabIndex >= 0 && !(el as HTMLButtonElement).disabled);
    return first && first.getAttribute('role') !== 'combobox' ? first : panel;
}

/** The open panels without those `drop` names. */
function withoutPanels(
    panels: Readonly<Record<string, WidgetSettings>>,
    drop: (id: string) => boolean,
): Readonly<Record<string, WidgetSettings>> {
    return Object.fromEntries(Object.entries(panels).filter(([id]) => !drop(id)));
}

function EditBar({ barRef, columns, onColumns, onReset, onCancel, onDone, saving, error }: {
    /** The bar, whose first control takes focus when the edit starts. */
    barRef: MutableRefObject<HTMLDivElement | null>;
    columns: ColumnsMode;
    onColumns: (c: ColumnsMode) => void;
    onReset: () => void;
    onCancel: () => void;
    onDone: () => void;
    saving: boolean;
    error: string | null;
}) {
    // While Done saves, Reset to default and Cancel are pending with it. The save
    // cannot be taken back once sent: a discard would leave it to land under the
    // promise that the Overview goes back to how it was, and a reset sent beside
    // it would race it, two writes to one layout. Columns is pending too: the
    // save's success clears the draft, and a change made meanwhile would be lost.
    return (
        <div ref={barRef} className="sticky top-0 z-20 border-b border-border bg-surface-header px-4 py-2 @2xl/page:px-5">
            <div className="mx-auto flex max-w-fixed-1600px flex-wrap items-center gap-3">
                <span className="text-sm font-semibold">Editing layout</span>
                <SegmentedControl label="Columns" size="md" options={COLUMN_OPTIONS} value={columns} onChange={onColumns}
                    pending={saving} />
                <div className="ml-auto flex flex-wrap items-center gap-2">
                    {error ? <span role="alert" className="text-xs text-state-danger">{error}</span> : null}
                    <Button variant="ghost" size="sm" onClick={onReset} pending={saving}>Reset to default</Button>
                    <Button variant="secondary" size="sm" onClick={onCancel} pending={saving}>Cancel</Button>
                    <Button size="sm" onClick={onDone} pending={saving} pendingLabel="Saving…">Done</Button>
                </div>
            </div>
        </div>
    );
}

/**
 * The hidden widgets in catalog order, each one Add away; added at the end of its
 * catalog column. At the report entry's place, "Add a report…" and any report tiles
 * hidden earlier.
 */
function HiddenTray({ layout, pending, saved, titleOf, onAdd, onAddReport, onRemove }: {
    layout: Layout;
    /** Done's save is in flight: each Add keeps focus and refuses (pending.ts). */
    pending: boolean;
    saved: ReturnType<typeof useSavedReports>;
    titleOf: (w: LayoutWidget) => string;
    onAdd: (id: string) => void;
    /** Show the report tile with this id: a new one, or one hidden earlier. */
    onAddReport: (id: string) => void;
    /** Take a hidden tile whose report is gone off the layout. */
    onRemove: (id: string) => void;
}) {
    const hidden = hiddenWidgets(layout);
    // Each report on the screen at most once, and no more than the most tiles shown.
    const full = shownCount(layout, 'report') >= REPORT_TILES_MAX;
    const row = (widget: LayoutWidget) => {
        if (widget.key === 'report') {
            const tile = resolveTile(widget.id, saved.data);
            if (tile.state === 'missing') {
                return (
                    <li key={widget.id} className="flex flex-wrap items-center gap-x-2 text-sm">
                        <span className="text-text-muted">Report no longer exists ·</span>
                        <Button variant="link" size="xs" onClick={() => onRemove(widget.id)} pending={pending}
                            aria-label="Remove the report that no longer exists">Remove</Button>
                    </li>
                );
            }
        }
        const entry = catalogEntry(widget.key)!;
        const title = titleOf(widget);
        return (
            <li key={widget.id} className="flex flex-wrap items-center gap-x-2 text-sm">
                <span className="font-medium">{title}</span>
                <span className="text-xs text-text-muted">{entry.description}</span>
                <Button variant="link" size="xs" onClick={() => onAdd(widget.id)} pending={pending}
                    // A tile cannot come back past the most shown at once.
                    disabled={widget.key === 'report' && full}
                    aria-label={`Add ${title}`} data-widget-add={widget.id}>Add</Button>
            </li>
        );
    };
    return (
        <section aria-label="Hidden widgets" className="border border-dashed border-border bg-surface px-4 py-2">
            <h2 className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-subtle">Hidden widgets</h2>
            <ul className="mt-1 flex flex-col gap-1">
                {CATALOG.flatMap((e) => {
                    const rows = hidden.filter((w) => w.key === e.key).map(row);
                    return e.key === 'report'
                        ? [<AddReportRow key="add-report" layout={layout} pending={pending} full={full} saved={saved} onAdd={onAddReport} />, ...rows]
                        : rows;
                })}
            </ul>
        </section>
    );
}

/**
 * "Add a report…": a menu of the ledger's saved reports and the catalog's, in the
 * order Reports lists them. A report already on the screen is listed as added, so
 * each appears there at most once; past the most tiles shown, the menu stays shut.
 */
function AddReportRow({ layout, pending, full, saved, onAdd }: {
    layout: Layout;
    pending: boolean;
    full: boolean;
    saved: ReturnType<typeof useSavedReports>;
    onAdd: (id: string) => void;
}) {
    const [menu, setMenu] = useState<ContextMenuAnchor | null>(null);
    const entry = catalogEntry('report')!;
    const onScreen = new Set(layout.widgets.filter((w) => w.key === 'report' && w.visible).map((w) => w.id));
    // "· added" is in the label, not a hint beside it: a hint is hidden from a
    // screen reader, and the item's state is what it has to hear.
    const items = tileChoices(saved.data ?? []).map((r) => ({
        id: r.id,
        label: onScreen.has(r.id) ? `${r.name} · added` : r.name,
        onSelect: () => onAdd(r.id),
        disabled: onScreen.has(r.id),
    }));
    return (
        <li className="flex flex-wrap items-center gap-x-2 text-sm">
            <span className="font-medium">{entry.title}</span>
            <span className="text-xs text-text-muted">{entry.description}</span>
            <Button variant="link" size="xs" aria-haspopup="menu" data-widget-add="report"
                // Waits on the saved reports, which the menu lists first.
                pending={pending || saved.isPending}
                disabled={full}
                onClick={(e) => {
                    const r = e.currentTarget.getBoundingClientRect();
                    setMenu({ x: r.left, y: r.bottom });
                }}>Add a report…</Button>
            <span className="text-xs text-text-muted">{shownCount(layout, 'report')} of {REPORT_TILES_MAX}</span>
            {saved.isError ? (
                <span role="alert" className="text-xs text-state-danger">
                    Couldn&apos;t load saved reports.{' '}
                    <Button variant="link" size="xs" pending={saved.isFetching} onClick={() => void saved.refetch()}>Retry</Button>
                </span>
            ) : null}
            {menu ? <ContextMenu anchor={menu} items={items} label="Add a report" onClose={() => setMenu(null)} /> : null}
        </li>
    );
}

/**
 * The two stacks. Wide (a pane of 56rem and up): main and side side by side, two to
 * one, or equal. Medium: main widgets one under another at full width, then the side
 * widgets two to a row. Narrow, or single: one stack, main then side.
 */
function Columns({ layout, editing, saving, onMove, onHide }: {
    layout: Layout;
    editing: boolean;
    /** Done's save is in flight: every move and hide control is pending. */
    saving: boolean;
    onMove: (id: string, how: Parameters<typeof moved>[2]) => void;
    onHide: (id: string) => void;
}) {
    const main = columnWidgets(layout, 'main');
    const side = columnWidgets(layout, 'side');
    const single = layout.columns === 'single';
    return (
        <div className={cn(
            'flex flex-col gap-4',
            !single && '@4xl/page:grid @4xl/page:items-start',
            !single && (layout.columns === 'equal' ? '@4xl/page:grid-cols-2' : '@4xl/page:grid-cols-3'),
        )}>
            <Stack column="main" widgets={main} editing={editing} saving={saving} onMove={onMove} onHide={onHide}
                className={cn('flex flex-col gap-4', !single && layout.columns !== 'equal' && '@4xl/page:col-span-2')} />
            {/* Two to a row at medium width, except while editing: the drop targets
                between frames are a stack's, and a grid would deal them into cells.
                Back to a column at wide, the items stretch again: a panel is a size
                container, so a shrink-to-fit width would collapse it to nothing. */}
            <Stack column="side" widgets={side} editing={editing} saving={saving} onMove={onMove} onHide={onHide}
                className={cn(
                    'flex flex-col gap-4',
                    !single && !editing && '@2xl/page:grid @2xl/page:grid-cols-2 @2xl/page:items-start @4xl/page:flex @4xl/page:flex-col @4xl/page:items-stretch',
                )} />
        </div>
    );
}

function Stack({ column, widgets, editing, saving, onMove, onHide, className }: {
    column: Column;
    widgets: Layout['widgets'];
    editing: boolean;
    saving: boolean;
    onMove: (id: string, how: Parameters<typeof moved>[2]) => void;
    onHide: (id: string) => void;
    className: string;
}) {
    const [over, setOver] = useState<number | null>(null);
    const drop = (index: number) => (e: DragEvent) => {
        e.preventDefault();
        setOver(null);
        const id = e.dataTransfer.getData(DRAG_TYPE);
        if (id) onMove(id, { column, index });
    };
    const target = (index: number): ReactNode => editing ? (
        <div
            key={`drop-${index}`}
            className="-my-2 py-1"
            onDragOver={(e) => {
                if (!e.dataTransfer.types.includes(DRAG_TYPE)) return;
                e.preventDefault();
                e.dataTransfer.dropEffect = 'move';
                setOver(index);
            }}
            onDragLeave={(e) => {
                // A child entering is not leaving the target.
                if (e.currentTarget.contains(e.relatedTarget as Node | null)) return;
                setOver(null);
            }}
            onDrop={drop(index)}
        >
            {over === index ? <InsertionLine /> : <div className="h-fixed-2px" />}
        </div>
    ) : null;

    return (
        <div className={className} data-column={column}>
            {widgets.map((w, i) => (
                <div key={w.id} className="contents">
                    {target(i)}
                    <WidgetHost
                        widget={w}
                        index={i}
                        count={widgets.length}
                        edit={editing ? {
                            pending: saving,
                            move: (delta) => onMove(w.id, { delta }),
                            toOtherColumn: () => onMove(w.id, { column: column === 'main' ? 'side' : 'main' }),
                            hide: () => onHide(w.id),
                            onDragStart: (e) => {
                                e.dataTransfer.setData(DRAG_TYPE, w.id);
                                e.dataTransfer.effectAllowed = 'move';
                            },
                        } : undefined}
                    />
                </div>
            ))}
            {target(widgets.length)}
        </div>
    );
}

// ---- before the layout loads --------------------------------------------------------

interface Shape { columns: ColumnsMode; main: WidgetKey[]; side: WidgetKey[] }

const shapeKey = (ledgerId: string) => `coffer.home.shape.${ledgerId}`;

function rememberShape(ledgerId: string, layout: Layout) {
    const shape: Shape = {
        columns: layout.columns,
        main: columnWidgets(layout, 'main').map((w) => w.key),
        side: columnWidgets(layout, 'side').map((w) => w.key),
    };
    try {
        window.localStorage.setItem(shapeKey(ledgerId), JSON.stringify(shape));
    } catch {
        // A per-browser convenience; without storage the default shape stands in.
    }
}

function readShape(ledgerId: string): Shape {
    try {
        const raw = window.localStorage.getItem(shapeKey(ledgerId));
        if (raw) {
            const s = JSON.parse(raw) as Partial<Shape>;
            if (Array.isArray(s.main) && Array.isArray(s.side)) return s as Shape;
        }
    } catch {
        // Fall through to the default shape.
    }
    const d = defaultLayout();
    return { columns: d.columns, main: columnWidgets(d, 'main').map((w) => w.key), side: columnWidgets(d, 'side').map((w) => w.key) };
}

/** Placeholder frames in the shape the columns last had, so nothing jumps when the
 *  layout lands and no hidden widget flashes into view first. */
function ShapePlaceholder({ ledgerId }: { ledgerId: string }) {
    const shape = readShape(ledgerId);
    const frame = (key: WidgetKey) => (
        <Panel key={key}>
            <PanelHead><span className="text-sm font-semibold text-text-muted">{CATALOG.find((e) => e.key === key)?.title ?? ''}</span></PanelHead>
            <div className="p-4"><Skeleton className="h-fixed-96px" /></div>
        </Panel>
    );
    return (
        <div aria-busy="true" aria-label="Loading the Overview" className={cn('flex flex-col gap-4', shape.columns !== 'single' && '@4xl/page:grid @4xl/page:grid-cols-3 @4xl/page:items-start')}>
            <div className="flex flex-col gap-4 @4xl/page:col-span-2">{shape.main.map(frame)}</div>
            <div className="flex flex-col gap-4">{shape.side.map(frame)}</div>
        </div>
    );
}

// ---- a ledger with no accounts yet ----------------------------------------------------

/** In place of the columns until the ledger has an account: a checklist that ticks
 *  from real state. */
function GetGoing() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };
    const { isOwner: owner, canEdit: editor } = useLedgerRole(ledgerId);
    const feeds = useFeedConnections(ledgerId);
    const reminders = useReminderList(ledgerId);
    const link = 'font-medium text-accent hover:underline';
    const tick = (done: boolean) => <span aria-hidden className={done ? 'text-text' : 'text-text-subtle'}>{done ? '☑' : '☐'}</span>;
    return (
        <Panel>
            <PanelHead><h2 className="text-sm font-semibold">Get this ledger going</h2></PanelHead>
            <PanelBody>
                <ul className="space-y-2 text-sm">
                    {owner ? <li>{tick(false)} <Link to="/imports/moneydance" className={link}>Import from Moneydance</Link></li> : null}
                    {editor ? <li>{tick(false)} <Link to="/ledgers/$ledgerId/accounts" params={{ ledgerId }} className={link}>Add an account</Link></li> : null}
                    {owner ? (
                        <li>{tick((feeds.data?.length ?? 0) > 0)} <Link to="/ledgers/$ledgerId/settings" params={{ ledgerId }} search={{ tab: 'feeds' }} className={link}>Connect a bank feed</Link></li>
                    ) : null}
                    {editor ? (
                        <li>{tick((reminders.data?.length ?? 0) > 0)} <Link to="/ledgers/$ledgerId/reminders" params={{ ledgerId }} className={link}>Create a reminder</Link></li>
                    ) : null}
                    {!editor ? <li className="text-text-muted">This ledger has no accounts yet; an owner or editor adds them.</li> : null}
                </ul>
            </PanelBody>
        </Panel>
    );
}

import { useEffect, useRef, type ComponentType, type DragEvent, type KeyboardEvent, type ReactNode } from 'react';
import { ArrowDown, ArrowLeft, ArrowRight, ArrowUp, Settings } from 'lucide-react';

import { Button } from '@/components/ui/Button';
import { GripIcon } from '@/components/ui/GripIcon';
import { IconButton } from '@/components/ui/IconButton';
import { pendingChoiceProps } from '@/components/ui/pending';
import { cn } from '@/lib/cn';

import { hasSettings, type Column, type WidgetKey } from './dashboardCatalog';
import type { LayoutWidget, WidgetSettings } from './dashboardLayout';
import { useHome, WidgetFrameContext, type WidgetFrameValue } from './homeContext';
import { WidgetBoundary } from './WidgetPanel';
import { AccountsWidget } from './widgets/AccountsWidget';
import { ActivityWidget } from './widgets/ActivityWidget';
import { BillsWidget } from './widgets/BillsWidget';
import { CashAheadWidget } from './widgets/CashAheadWidget';
import { CashFlowWidget } from './widgets/CashFlowWidget';
import { FeedsChecksWidget } from './widgets/FeedsChecksWidget';
import { InvestmentsWidget } from './widgets/InvestmentsWidget';
import { NetWorthWidget } from './widgets/NetWorthWidget';
import { ProblemsWidget } from './widgets/ProblemsWidget';
import { RecentTransactionsWidget } from './widgets/RecentTransactionsWidget';
import { ReportTileWidget } from './widgets/ReportTileWidget';
import { ReviewWidget } from './widgets/ReviewWidget';
import { ThisMonthWidget } from './widgets/ThisMonthWidget';
import { WatchlistWidget } from './widgets/WatchlistWidget';

const WIDGETS: Record<WidgetKey, ComponentType> = {
    problems: ProblemsWidget,
    bills: BillsWidget,
    review: ReviewWidget,
    'budget-pace': ThisMonthWidget,
    'cash-flow': CashFlowWidget,
    'recent-transactions': RecentTransactionsWidget,
    'data-health': FeedsChecksWidget,
    'net-worth': NetWorthWidget,
    investments: InvestmentsWidget,
    watchlist: WatchlistWidget,
    activity: ActivityWidget,
    accounts: AccountsWidget,
    report: ReportTileWidget,
    'cash-forecast': CashAheadWidget,
};

/** What the edit frame can do to its widget. */
export interface EditControls {
    /** Done's save is in flight: every control here keeps focus and refuses, and
     *  neither a drag nor Alt+arrow moves the widget (ADR-0023 §L). */
    pending: boolean;
    move: (delta: number) => void;
    toOtherColumn: () => void;
    hide: () => void;
    onDragStart: (e: DragEvent) => void;
}

/**
 * One widget on the page: its frame (the settings it renders with, the draft the
 * page holds for it while its settings panel is open), its error boundary, and in
 * edit mode the strip of move controls with its live content made inert.
 */
export function WidgetHost({ widget, index, count, edit }: {
    widget: LayoutWidget;
    index: number;
    count: number;
    /** Present in edit mode. */
    edit?: EditControls;
}) {
    const home = useHome();
    const draft = home.panelDraft(widget.id);
    const Widget = WIDGETS[widget.key];

    const frame: WidgetFrameValue = {
        widget,
        settings: draft ?? widget.settings,
        settingsOpen: draft !== null,
        openSettings: () => home.openSettings(widget.id),
        setDraft: (settings: WidgetSettings) => home.setPanelDraft(widget.id, settings),
        closeSettings: (apply) => home.closeSettings(widget.id, apply),
        position: { column: widget.column, index, count },
    };

    const body = (
        <WidgetBoundary title={home.titleOf(widget)}>
            <Widget />
        </WidgetBoundary>
    );

    return (
        <WidgetFrameContext.Provider value={frame}>
            {edit ? <EditFrame widget={widget} index={index} count={count} edit={edit} onSettings={frame.openSettings}>{body}</EditFrame> : body}
        </WidgetFrameContext.Provider>
    );
}

function EditFrame({ widget, index, count, edit, onSettings, children }: {
    widget: LayoutWidget;
    index: number;
    count: number;
    edit: EditControls;
    onSettings: () => void;
    children: ReactNode;
}) {
    const home = useHome();
    const title = home.titleOf(widget);
    const columnName = widget.column === 'main' ? 'main column' : 'side';
    const label = `${title}, ${columnName}, ${index + 1} of ${count}`;
    const toward: Column = widget.column === 'main' ? 'side' : 'main';

    function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
        // Alt+↑/↓ reorder within the column, as the split editor's lines do. No chord
        // moves columns: Alt+←/→ is the browser's Back and Forward.
        if (!e.altKey || e.target !== e.currentTarget) return;
        if (e.key === 'ArrowUp') { e.preventDefault(); if (!edit.pending) edit.move(-1); }
        if (e.key === 'ArrowDown') { e.preventDefault(); if (!edit.pending) edit.move(1); }
    }

    return (
        <div
            role="group"
            aria-label={label}
            tabIndex={0}
            data-widget-frame={widget.id}
            onKeyDown={onKeyDown}
            className="border border-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        >
            <div className="flex flex-wrap items-center gap-1 border-b border-border bg-surface-header px-2 py-1">
                <span
                    draggable={!edit.pending}
                    onDragStart={(e) => { if (edit.pending) e.preventDefault(); else edit.onDragStart(e); }}
                    // While Done saves the grip offers no drag, and looks as the
                    // pending move buttons beside it do.
                    className={cn(
                        'hidden px-1 text-text-subtle pointer-fine:inline-flex',
                        edit.pending ? 'cursor-default opacity-50' : 'cursor-grab',
                    )}
                    aria-hidden
                >
                    <GripIcon />
                </span>
                <span className="min-w-0 flex-1 truncate text-xs font-medium">
                    {title} <span className="font-normal text-text-muted">· {columnName}, {index + 1} of {count}</span>
                </span>
                <IconButton aria-label={`Move ${title} up`} disabled={index === 0} pending={edit.pending}
                    onClick={() => edit.move(-1)}>
                    <ArrowUp className="size-icon-sm" aria-hidden />
                </IconButton>
                <IconButton aria-label={`Move ${title} down`} disabled={index === count - 1} pending={edit.pending}
                    onClick={() => edit.move(1)}>
                    <ArrowDown className="size-icon-sm" aria-hidden />
                </IconButton>
                {/* One column on a phone, so an arrow toward the other one points nowhere:
                    the column a widget takes on wide screens is a choice there instead. */}
                <span className="hidden @2xl/page:inline-flex">
                    <IconButton aria-label={`Move ${title} to the ${toward === 'main' ? 'main column' : 'side column'}`}
                        pending={edit.pending} onClick={edit.toOtherColumn}>
                        {toward === 'side' ? <ArrowRight className="size-icon-sm" aria-hidden /> : <ArrowLeft className="size-icon-sm" aria-hidden />}
                    </IconButton>
                </span>
                <label className="inline-flex items-center gap-1 text-xs text-text-muted @2xl/page:hidden">
                    Column on wide screens
                    <select
                        aria-label={`${title}: column on wide screens`}
                        className="h-control-28px rounded border border-border bg-surface px-1 text-xs text-text aria-disabled:cursor-not-allowed aria-disabled:opacity-50"
                        value={widget.column}
                        {...pendingChoiceProps<HTMLSelectElement>(edit.pending, {
                            onChange: (e) => { if (e.target.value !== widget.column) edit.toOtherColumn(); },
                        })}
                    >
                        <option value="main">Main</option>
                        <option value="side">Side</option>
                    </select>
                </label>
                {hasSettings(widget.key) ? (
                    <IconButton aria-label={`${title} settings`} pending={edit.pending} onClick={onSettings}>
                        <Settings className="size-icon-sm" aria-hidden />
                    </IconButton>
                ) : null}
                <Button ref={home.hideControl(widget.id)} variant="ghost" size="xs" onClick={edit.hide} pending={edit.pending} aria-label={`Hide ${title}`}>Hide</Button>
            </div>
            <InertWhileEditing>{children}</InertWhileEditing>
        </div>
    );
}

/**
 * What stays usable inside a widget while the layout is edited: its settings panel,
 * which is the edit in progress, and a control that changes the layout rather than
 * acting on the widget's content (a gone report's Remove), marked `data-edit-control`.
 */
const EDITABLE = '[role="group"][aria-label$=" settings"], [data-edit-control]';

/**
 * The widget's live content, with its links and act-in-place controls inert while
 * the layout is edited. React 18 has no `inert` prop, so the attribute is set on the
 * element directly. What EDITABLE names stays usable.
 */
function InertWhileEditing({ children }: { children: ReactNode }) {
    const ref = useRef<HTMLDivElement>(null);
    useEffect(() => {
        const el = ref.current;
        if (!el) return;
        const apply = () => {
            for (const child of Array.from(el.querySelectorAll<HTMLElement>('a, button, input, select, textarea, [tabindex]'))) {
                const inSettings = child.closest(EDITABLE) !== null;
                if (inSettings) child.removeAttribute('data-inert-tabindex');
                else if (!child.hasAttribute('data-inert-tabindex')) {
                    child.setAttribute('data-inert-tabindex', child.getAttribute('tabindex') ?? '');
                    child.setAttribute('tabindex', '-1');
                }
            }
        };
        apply();
        const observer = new MutationObserver(apply);
        observer.observe(el, { childList: true, subtree: true });
        return () => {
            observer.disconnect();
            for (const child of Array.from(el.querySelectorAll<HTMLElement>('[data-inert-tabindex]'))) {
                const original = child.getAttribute('data-inert-tabindex');
                if (original) child.setAttribute('tabindex', original);
                else child.removeAttribute('tabindex');
                child.removeAttribute('data-inert-tabindex');
            }
        };
    }, []);
    return (
        <div
            ref={ref}
            // A click on a link or a control inside does nothing while editing; the
            // settings panel is exempt.
            onClickCapture={(e) => {
                const target = e.target as HTMLElement;
                if (target.closest(EDITABLE)) return;
                if (target.closest('a, button')) { e.preventDefault(); e.stopPropagation(); }
            }}
            // Nor does a right-click open a row's menu: its items are the row's actions
            // and links, chosen by a key no click handler sees. The browser's own menu
            // still opens, as it does over inert content.
            onContextMenuCapture={(e) => {
                const target = e.target as HTMLElement;
                if (target.closest(EDITABLE)) return;
                e.stopPropagation();
            }}
        >
            {children}
        </div>
    );
}

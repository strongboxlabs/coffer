import { useEffect, useId, useRef, useState } from 'react';

import type { RegisterStatusCounts } from '@/lib/api/register';
import { REGISTER_STATUS_VIEWS, type StatusFilter } from './registerStatus';

// Status-view selector (redesign: Option A). Replaces the flat pill strip
// (All / Cleared / Uncleared / … laid out separately) with a single compact
// "Show: <view> ▾" dropdown, so the controls collapse to one dense row instead
// of a sprawling tab bar. One active view at a time — it's a scope selector,
// not a multi-toggle. Both registers render this via RegisterControlsBar, so
// the view vocabulary can't drift between bank + investment.
//
// Styled to match the register's other compact h-7 controls (the Filter
// button); the app's h-10 form primitives are deliberately NOT used here — a
// dense register header uses the tighter register-field scale.

const LABEL_BY_VALUE = new Map(REGISTER_STATUS_VIEWS.map((v) => [v.value, v.label]));

export interface RegisterStatusMenuProps {
    statusFilter: StatusFilter;
    onChange: (next: StatusFilter) => void;
    /** Per-view entry counts (mig 165); null while loading — badges hide. */
    counts: RegisterStatusCounts | null;
}

export function RegisterStatusMenu({ statusFilter, onChange, counts }: RegisterStatusMenuProps) {
    const popId = useId();
    const countFor = (v: StatusFilter): number | null => {
        if (!counts) return null;
        switch (v) {
            case 'all': return counts.all;
            case 'cleared': return counts.cleared;
            case 'uncleared': return counts.uncleared;
            case 'reconciling': return counts.reconciling;
            case 'scheduled': return counts.scheduled;
            case 'needs_review': return counts.needsReview;
            case 'hidden': return counts.hidden;
            default: return null;
        }
    };
    const [open, setOpen] = useState(false);
    const rootRef = useRef<HTMLDivElement>(null);

    // Close on outside click.
    useEffect(() => {
        if (!open) return;
        const onDoc = (e: MouseEvent) => {
            if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
        };
        document.addEventListener('mousedown', onDoc);
        return () => document.removeEventListener('mousedown', onDoc);
    }, [open]);

    const activeLabel = LABEL_BY_VALUE.get(statusFilter) ?? 'All';

    return (
        <div ref={rootRef} className="relative shrink-0">
            <button
                type="button"
                onClick={() => setOpen((v) => !v)}
                aria-expanded={open}
                aria-controls={popId}
                aria-haspopup="listbox"
                title="Filter by reconciliation / review status"
                className="inline-flex h-7 items-center gap-1 rounded border border-border bg-surface px-2 text-xs text-text hover:border-accent"
            >
                <span className="text-text-subtle">Show:</span>
                <span className="font-medium">{activeLabel}</span>
                {countFor(statusFilter) !== null ? (
                    <span className="tabular-nums text-text-subtle">{countFor(statusFilter)}</span>
                ) : null}
                <span aria-hidden>▾</span>
            </button>
            {open ? (
                <ul
                    id={popId}
                    role="listbox"
                    className="absolute left-0 z-30 mt-1 w-44 rounded border border-border bg-surface py-1 text-xs shadow-lg"
                >
                    {REGISTER_STATUS_VIEWS.map((v) => (
                        <li
                            key={v.value}
                            // A hairline above Hidden groups the recon/review
                            // views apart from the soft-hidden view (mirrors the
                            // old tab divider).
                            className={v.value === 'hidden' ? 'mt-1 border-t border-border pt-1' : undefined}
                        >
                            <button
                                type="button"
                                role="option"
                                aria-selected={v.value === statusFilter}
                                onClick={() => { onChange(v.value); setOpen(false); }}
                                className={
                                    'flex w-full items-center justify-between px-3 py-1 text-left hover:bg-surface-hover '
                                    + (v.value === statusFilter ? 'font-medium text-accent' : 'text-text')
                                }
                            >
                                <span>{v.label}</span>
                                <span className="ml-auto flex items-center gap-2">
                                    {countFor(v.value) !== null ? (
                                        <span className="tabular-nums text-text-subtle">
                                            {countFor(v.value)}
                                        </span>
                                    ) : null}
                                    {v.value === statusFilter ? (
                                        <span aria-hidden className="text-accent">✓</span>
                                    ) : null}
                                </span>
                            </button>
                        </li>
                    ))}
                </ul>
            ) : null}
        </div>
    );
}

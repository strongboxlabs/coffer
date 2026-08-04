import { useEffect, useId, useRef, useState } from 'react';

import {
    REGISTER_SORT_COLUMNS,
    defaultDirFor,
    sortColumnLabel,
    type RegisterSortColumn,
    type RegisterSortState,
} from './registerSort';

// Column-sort selector (mig 166). A compact "Sort: <col> <arrow> ▾" dropdown
// sitting next to the status "Show ▾" menu, on the same dense h-7 register
// control scale. Picking a column sorts by it in its natural default direction
// (defaultDirFor); picking the ALREADY-active column flips the direction.
// Investment-only columns (Security / Shares / Price / Action) appear only on
// investment registers — they read off the security leg, so a bank register
// would sort them all-equal. Both registers render this via RegisterControlsBar
// so the sort vocabulary can't drift between bank + investment.

export interface RegisterSortMenuProps {
    sort: RegisterSortState;
    onChange: (next: RegisterSortState) => void;
    /** Show the investment-only columns (Security / Shares / Price / Action). */
    investment: boolean;
}

export function RegisterSortMenu({ sort, onChange, investment }: RegisterSortMenuProps) {
    const popId = useId();
    const [open, setOpen] = useState(false);
    const rootRef = useRef<HTMLDivElement>(null);

    // Close on outside click (mirrors RegisterStatusMenu).
    useEffect(() => {
        if (!open) return;
        const onDoc = (e: MouseEvent) => {
            if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
        };
        document.addEventListener('mousedown', onDoc);
        return () => document.removeEventListener('mousedown', onDoc);
    }, [open]);

    const columns = REGISTER_SORT_COLUMNS.filter((c) => investment || !c.investmentOnly);
    const arrow = sort.dir === 'asc' ? '↑' : '↓';

    function pick(column: RegisterSortColumn) {
        // Re-picking the active column toggles its direction; a different column
        // starts in its natural default direction.
        const next: RegisterSortState =
            column === sort.column
                ? { column, dir: sort.dir === 'asc' ? 'desc' : 'asc' }
                : { column, dir: defaultDirFor(column) };
        onChange(next);
        setOpen(false);
    }

    return (
        <div ref={rootRef} className="relative shrink-0">
            <button
                type="button"
                onClick={() => setOpen((v) => !v)}
                aria-expanded={open}
                aria-controls={popId}
                aria-haspopup="listbox"
                title="Sort the register by a column"
                className="inline-flex h-7 items-center gap-1 rounded border border-border bg-surface px-2 text-xs text-text hover:border-accent"
            >
                <span className="text-text-subtle">Sort:</span>
                <span className="font-medium">{sortColumnLabel(sort.column)}</span>
                <span aria-hidden className="text-text-subtle">{arrow}</span>
                <span aria-hidden>▾</span>
            </button>
            {open ? (
                <ul
                    id={popId}
                    role="listbox"
                    className="absolute left-0 z-30 mt-1 w-40 rounded border border-border bg-surface py-1 text-xs shadow-lg"
                >
                    {columns.map((c) => (
                        <li key={c.value}>
                            <button
                                type="button"
                                role="option"
                                aria-selected={c.value === sort.column}
                                onClick={() => pick(c.value)}
                                className={
                                    'flex w-full items-center justify-between px-3 py-1 text-left hover:bg-surface-hover '
                                    + (c.value === sort.column ? 'font-medium text-accent' : 'text-text')
                                }
                            >
                                <span>{c.label}</span>
                                {c.value === sort.column ? (
                                    <span aria-hidden className="text-accent">{arrow}</span>
                                ) : null}
                            </button>
                        </li>
                    ))}
                </ul>
            ) : null}
        </div>
    );
}

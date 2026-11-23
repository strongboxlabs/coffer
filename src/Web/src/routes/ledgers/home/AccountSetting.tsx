import { useId } from 'react';

import type { ChoiceOption } from './dashboardCatalog';

/**
 * A widget setting that is one of the accounts the widget shows, or none (Cash
 * ahead's chart: the accounts combined, or one of them). The widget names the
 * accounts it offers, in its own order; until it has them, only none is offered.
 */
export function AccountSetting({ label, noneLabel, options, value, onChange }: {
    label: string;
    /** What no account means for this widget ("Combined"). */
    noneLabel: string;
    options: readonly ChoiceOption[];
    value: unknown;
    onChange: (id: string | null) => void;
}) {
    const id = useId();
    const chosen = typeof value === 'string' ? value : null;
    // A choice that names no account offered is shown as none, which is how the
    // widget draws it.
    const shown = chosen !== null && options.some((o) => o.value === chosen) ? chosen : '';

    return (
        <div className="flex flex-wrap items-center gap-2">
            <label htmlFor={id} className="text-xs font-medium text-text-muted">{label}</label>
            <select
                id={id}
                className="h-control-28px max-w-full rounded border border-border bg-surface px-1 text-xs text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                value={shown}
                onChange={(e) => onChange(e.target.value === '' ? null : e.target.value)}
            >
                <option value="">{noneLabel}</option>
                {options.map((o) => <option key={String(o.value)} value={String(o.value)}>{o.label}</option>)}
            </select>
        </div>
    );
}

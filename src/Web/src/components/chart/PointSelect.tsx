import { useRef, type KeyboardEvent } from 'react';

import { cn } from '@/lib/cn';

/**
 * Keyboard and touch selection of a chart's points, as HTML buttons laid over its
 * columns.
 *
 * A chart is `role="img"`, so its marks cannot take focus; these buttons can. One
 * button per column, the full column's width, so a touch anywhere in a column picks
 * it. Focus roves: only one button is in the tab order, ←/→ move between them, and
 * Enter or Space (or a click) selects. Selection is the caller's state, shown in a
 * readout line beside the chart rather than in a hover tooltip, so it works without
 * a pointer.
 */
export function PointSelect({
    labels, selected, onSelect, label,
}: {
    /** One accessible name per column, in order: the column's date, then its value. */
    labels: readonly string[];
    selected: number | null;
    onSelect: (index: number) => void;
    /** The group's name: "Net worth month-ends". */
    label: string;
}) {
    const refs = useRef<(HTMLButtonElement | null)[]>([]);
    const count = labels.length;
    if (count === 0) return null;
    const active = selected ?? count - 1;

    function onKeyDown(e: KeyboardEvent<HTMLButtonElement>, index: number) {
        const next = e.key === 'ArrowRight' ? index + 1
            : e.key === 'ArrowLeft' ? index - 1
            : e.key === 'Home' ? 0
            : e.key === 'End' ? count - 1
            : null;
        if (next === null) return;
        e.preventDefault();
        refs.current[Math.max(0, Math.min(count - 1, next))]?.focus();
    }

    return (
        <div role="group" aria-label={label} className="absolute inset-0">
            {labels.map((name, i) => (
                <button
                    key={`${i}_${name}`}
                    ref={(el) => { refs.current[i] = el; }}
                    type="button"
                    aria-label={name}
                    aria-pressed={selected === i}
                    tabIndex={i === active ? 0 : -1}
                    onClick={() => onSelect(i)}
                    onKeyDown={(e) => onKeyDown(e, i)}
                    className={cn(
                        'absolute inset-y-0 rounded-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent',
                        selected === i ? 'ring-2 ring-inset ring-accent' : undefined,
                    )}
                    style={{ left: `${(i / count) * 100}%`, width: `${100 / count}%` }}
                />
            ))}
        </div>
    );
}

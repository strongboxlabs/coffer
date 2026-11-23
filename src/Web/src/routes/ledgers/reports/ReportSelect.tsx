import { FieldLabel } from '@/components/ui/FieldLabel';

/** A labelled native select, the report bars' one dropdown. */
export function Select({
    label, value, options, onChange, labels,
}: {
    label: string;
    value: string;
    options: readonly string[];
    onChange: (value: string) => void;
    /** Display text per value, where the UI's word differs from the API's. */
    labels?: Readonly<Record<string, string>>;
}) {
    const id = `report-${label.replace(/\s+/g, '-').toLowerCase()}`;
    return (
        <div className="flex min-w-0 flex-col gap-1">
            <FieldLabel htmlFor={id}>{label}</FieldLabel>
            <select
                id={id}
                value={value}
                onChange={(e) => onChange(e.target.value)}
                className="h-control-28px rounded border border-border bg-surface px-2 text-xs text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
            >
                {options.map((o) => <option key={o} value={o}>{labels?.[o] ?? o}</option>)}
            </select>
        </div>
    );
}

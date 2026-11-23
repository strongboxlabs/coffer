import type { ReactNode } from 'react';
import { CornerUpLeft } from 'lucide-react';

import { cn } from '@/lib/cn';

/**
 * The "comes from the other side" marker: a small corner arrow drawn before a
 * memo or a tag set that this side does not own and shows because the other
 * side has it.
 *
 * Chosen over brackets and over italic. Italic is already how a scheduled row
 * reads, so an inherited memo on a scheduled row would have been
 * indistinguishable from an own one; brackets copy into text as if they were
 * part of the memo.
 *
 * It carries the account's name for assistive technology and as a tooltip, so
 * the marker says where the value lives without taking width in the row.
 */
export function InheritedMarker({ from, className }: { from: string | null | undefined; className?: string }) {
    const label = from ? `From ${from}` : 'From the other side';
    return (
        <span
            role="img"
            aria-label={label}
            title={label}
            className={cn('inline-flex shrink-0 items-center text-text-subtle', className)}
        >
            <CornerUpLeft aria-hidden className="size-[0.625rem]" strokeWidth={2.5} />
        </span>
    );
}

/**
 * A memo as a register cell shows it: plain when it is this side's own, and
 * with the marker, in the subtle colour, when it is inherited.
 */
export function InheritedText({
    inherited,
    from,
    className,
    children,
}: {
    inherited: boolean;
    from: string | null | undefined;
    className?: string;
    children: ReactNode;
}) {
    if (!inherited) return <span className={className}>{children}</span>;
    return (
        <span
            data-inherited="true"
            className={cn('flex min-w-0 items-center gap-1 text-text-subtle', className)}
        >
            <InheritedMarker from={from} />
            <span className="min-w-0 truncate">{children}</span>
        </span>
    );
}

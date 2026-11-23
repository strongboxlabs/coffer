import { cn } from '@/lib/cn';

// Skeleton — a placeholder block in the shape of content still loading, so the
// page holds its layout instead of jumping when the data lands. Decorative, so
// aria-hidden; the region that is loading carries `aria-busy` (see SkeletonRows).
// The pulse runs only for readers who have not asked for reduced motion.

export function Skeleton({ className }: { className?: string }) {
    return (
        <span
            aria-hidden
            className={cn('block rounded-sm bg-surface-hover motion-safe:animate-pulse', className)}
        />
    );
}

/**
 * `rows` placeholder lines at a list row's height, in a region marked busy. The
 * row count comes from the widget's own setting, so the loaded list lands where
 * its placeholder was.
 */
export function SkeletonRows({ rows, label, rowClassName = 'h-control-28px' }: {
    rows: number;
    /** What is loading, for the busy region's name. */
    label: string;
    rowClassName?: string;
}) {
    return (
        <div aria-busy="true" aria-label={label} className="space-y-2 p-4">
            {Array.from({ length: rows }, (_, i) => (
                <Skeleton key={i} className={rowClassName} />
            ))}
        </div>
    );
}

import type { ReactNode } from 'react';

import { cn } from '@/lib/cn';

// Breadcrumb — thin row of links separated by `/`. Lives in the
// TopBar. Wrapped in `<nav aria-label="Breadcrumb">` so screen
// readers can jump to it directly, matching the standard
// breadcrumb-component idiom (W3C ARIA APG).

export interface BreadcrumbItem {
    /** Label shown to the user. Last item is treated as the current page. */
    label: ReactNode;
    /** Optional anchor href. Use `node` to inject a router-aware Link. */
    href?: string;
    /** Optional pre-built ReactNode (e.g. a TanStack Router `<Link>`). */
    node?: ReactNode;
}

export interface BreadcrumbProps {
    items: ReadonlyArray<BreadcrumbItem>;
    className?: string;
}

export function Breadcrumb({ items, className }: BreadcrumbProps) {
    return (
        <nav aria-label="Breadcrumb" className={cn('text-xs', className)}>
            <ol className="flex items-center gap-1">
                {items.map((item, index) => {
                    const isLast = index === items.length - 1;
                    return (
                        <li key={index} className="flex items-center gap-1">
                            {index > 0 ? (
                                <span aria-hidden className="text-text-subtle">
                                    /
                                </span>
                            ) : null}
                            {isLast ? (
                                <span
                                    aria-current="page"
                                    className="font-medium text-text"
                                >
                                    {item.label}
                                </span>
                            ) : item.node ? (
                                <span className="text-text-muted hover:text-text">
                                    {item.node}
                                </span>
                            ) : item.href ? (
                                <a
                                    href={item.href}
                                    className="text-text-muted hover:text-text"
                                >
                                    {item.label}
                                </a>
                            ) : (
                                <span className="text-text-muted">
                                    {item.label}
                                </span>
                            )}
                        </li>
                    );
                })}
            </ol>
        </nav>
    );
}

import {
    Children,
    cloneElement,
    forwardRef,
    isValidElement,
    type AnchorHTMLAttributes,
    type ButtonHTMLAttributes,
    type HTMLAttributes,
    type ReactElement,
    type ReactNode,
} from 'react';

import { cn } from '@/lib/cn';

// Sidebar shell primitives — ADR-0021 Rule 1.
//
// The whole authed app frame is `<SidebarLayout> = <Sidebar> + <MainArea>`.
// Each piece is a thin styled wrapper around a semantic element so callers
// retain full prop access (id, aria-*, data-* attrs). Composable, not
// monolithic: a screen builds its sidebar contents by composing
// SidebarHeader / SidebarPicker / SidebarSection / SidebarNavLink /
// SidebarFooter inside <Sidebar>, instead of feeding a `nav={…}` prop.
//
// No screen consumes these yet. PR 5.2 rebuilds AuthedHeader into the
// SidebarLayout shell and routes each page into it. PR 5.1 exists to
// give 5.2 something to consume.

// --- Layout shell ----------------------------------------------------------

export type SidebarLayoutProps = HTMLAttributes<HTMLDivElement>;

/**
 * Root frame: flex row of `<Sidebar>` + `<MainArea>`. Exactly
 * viewport-height (`h-dvh`) with `overflow-hidden` so only the
 * inner `MainPane` scrolls — prevents the body + main-pane double
 * scrollbar that emerges when the shell uses `min-h-dvh` and its
 * children exceed the viewport.
 */
export const SidebarLayout = forwardRef<HTMLDivElement, SidebarLayoutProps>(
    function SidebarLayout({ className, ...props }, ref) {
        return (
            <div
                ref={ref}
                className={cn(
                    'flex h-dvh overflow-hidden bg-surface-muted text-text',
                    className,
                )}
                {...props}
            />
        );
    },
);

// --- Sidebar ---------------------------------------------------------------

export type SidebarProps = HTMLAttributes<HTMLElement>;

/**
 * Fixed-width left rail. 224px (`w-56`) per ADR-0021 Rule 1. The width
 * is settled here so screens don't drift apart. Responsive collapse
 * (icons-only ≤ 1024px, drawer ≤ 768px) lands in PR 5.3.
 */
export const Sidebar = forwardRef<HTMLElement, SidebarProps>(function Sidebar(
    { className, ...props },
    ref,
) {
    return (
        <aside
            ref={ref}
            className={cn(
                'flex w-56 flex-col border-r border-border bg-surface-sidebar',
                className,
            )}
            {...props}
        />
    );
});

// --- Sidebar header / picker / footer --------------------------------------

export type SidebarHeaderProps = HTMLAttributes<HTMLDivElement>;

/**
 * Top block of the sidebar — brand wordmark + optional collapse button.
 * Rendered on a `bg-surface` background so it lifts off the muted
 * `bg-surface-sidebar` body of the rail.
 */
export const SidebarHeader = forwardRef<HTMLDivElement, SidebarHeaderProps>(
    function SidebarHeader({ className, ...props }, ref) {
        return (
            <div
                ref={ref}
                className={cn(
                    'flex items-center justify-between border-b border-border bg-surface px-3 py-2.5',
                    className,
                )}
                {...props}
            />
        );
    },
);

export interface SidebarPickerProps
    extends ButtonHTMLAttributes<HTMLButtonElement> {
    /** Small leading swatch (e.g. ledger color). */
    swatch?: ReactNode;
}

/**
 * Single-row picker (ledger picker, scope picker). Lives between the
 * SidebarHeader and the SidebarNav. The full flyout/popover lands in
 * PR 5.3 — for 5.1 this is just the button shape.
 */
export const SidebarPicker = forwardRef<HTMLButtonElement, SidebarPickerProps>(
    function SidebarPicker({ className, children, swatch, type, ...props }, ref) {
        return (
            <div className="border-b border-border bg-surface px-2 py-1.5">
                <button
                    ref={ref}
                    type={type ?? 'button'}
                    className={cn(
                        'flex w-full items-center justify-between rounded px-2 py-1 text-xs font-semibold text-text',
                        'hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-1',
                        className,
                    )}
                    {...props}
                >
                    <span className="flex items-center gap-1.5">
                        {swatch ?? null}
                        {children}
                    </span>
                </button>
            </div>
        );
    },
);

export type SidebarNavProps = HTMLAttributes<HTMLElement>;

/** Scrollable nav body — wraps SidebarSection + SidebarNavLink children. */
export const SidebarNav = forwardRef<HTMLElement, SidebarNavProps>(
    function SidebarNav({ className, ...props }, ref) {
        return (
            <nav
                ref={ref}
                className={cn('flex-1 overflow-y-auto px-1.5 py-2', className)}
                {...props}
            />
        );
    },
);

export type SidebarSectionProps = HTMLAttributes<HTMLDivElement>;

/**
 * Uppercase tracking-wide group heading (e.g. "Accounts · Banking").
 * Pure visual cue; the surrounding `<nav>` carries the semantics.
 */
export const SidebarSection = forwardRef<HTMLDivElement, SidebarSectionProps>(
    function SidebarSection({ className, ...props }, ref) {
        return (
            <div
                ref={ref}
                className={cn(
                    'px-2 pb-1 pt-2 text-[0.625rem] font-semibold uppercase tracking-wider text-text-subtle',
                    className,
                )}
                {...props}
            />
        );
    },
);

export interface SidebarNavLinkProps
    extends AnchorHTMLAttributes<HTMLAnchorElement> {
    /** Whether this is the current route. Controls active styling. */
    active?: boolean;
    /**
     * Render the child element with the nav-link styling instead of
     * wrapping it in an `<a>`. The child must be a single React
     * element (typically a router-aware `<Link>`). Mirrors Radix's
     * Slot pattern — useful when the consumer needs a non-`<a>`
     * element or a typed router Link.
     */
    asChild?: boolean;
}

/**
 * Single nav row. Renders an `<a>` by default; pass `asChild` to
 * project the styling onto a router-aware child (typically
 * `<Link>`) instead. Active state: `bg-surface` with a 2px inset
 * accent left border — tested against the workflow-dense mockups.
 */
export const SidebarNavLink = forwardRef<HTMLAnchorElement, SidebarNavLinkProps>(
    function SidebarNavLink(
        { className, active, asChild, children, ...props },
        ref,
    ) {
        const composedClassName = cn(
            'flex items-center gap-2 rounded px-2 py-[0.3rem] text-[0.8125rem] text-text-muted',
            'hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-1',
            active &&
                'bg-surface font-semibold text-text shadow-[inset_2px_0_0_var(--color-accent)]',
            className,
        );

        if (asChild) {
            const only = Children.only(children) as ReactElement<{
                className?: string;
            }>;
            if (!isValidElement(only)) {
                throw new Error(
                    'SidebarNavLink asChild requires a single React element child',
                );
            }
            return cloneElement(only, {
                ...props,
                ref,
                'aria-current': active ? 'page' : undefined,
                className: cn(only.props.className, composedClassName),
                // The child controls its own children content.
            } as Partial<typeof only.props> & { ref: typeof ref });
        }

        return (
            <a
                ref={ref}
                aria-current={active ? 'page' : undefined}
                className={composedClassName}
                {...props}
            >
                {children}
            </a>
        );
    },
);

export type SidebarFooterProps = HTMLAttributes<HTMLDivElement>;

/**
 * Bottom block (user card). Same lifted-white treatment as
 * SidebarHeader so the two anchor the rail visually.
 */
export const SidebarFooter = forwardRef<HTMLDivElement, SidebarFooterProps>(
    function SidebarFooter({ className, ...props }, ref) {
        return (
            <div
                ref={ref}
                className={cn(
                    'border-t border-border bg-surface px-2 py-2',
                    className,
                )}
                {...props}
            />
        );
    },
);

// --- Main area + top bar ---------------------------------------------------

export type MainAreaProps = HTMLAttributes<HTMLDivElement>;

/**
 * Right column: holds `<TopBar>` + scrollable `<MainPane>`. Flex
 * column so the top bar stays pinned and the main pane scrolls under
 * it.
 */
export const MainArea = forwardRef<HTMLDivElement, MainAreaProps>(
    function MainArea({ className, ...props }, ref) {
        return (
            <div
                ref={ref}
                className={cn('flex flex-1 flex-col', className)}
                {...props}
            />
        );
    },
);

export type TopBarProps = HTMLAttributes<HTMLElement>;

/**
 * Thin top bar — breadcrumb on the left, actions on the right.
 * Fixed 40px height (`h-10`) per ADR-0021 Rule 1.
 */
export const TopBar = forwardRef<HTMLElement, TopBarProps>(function TopBar(
    { className, ...props },
    ref,
) {
    return (
        <header
            ref={ref}
            className={cn(
                'flex h-10 items-center justify-between border-b border-border bg-surface px-4 text-xs',
                className,
            )}
            {...props}
        />
    );
});

export type MainPaneProps = HTMLAttributes<HTMLElement>;

/**
 * Scrollable content surface. Pages render their content inside
 * here; the scrollbar is on this element, not the body, so the top
 * bar stays pinned.
 *
 * `scrollbar-gutter: stable` always reserves the scrollbar track, so a
 * page (or a Settings tab) that doesn't overflow doesn't widen the pane
 * — otherwise the centered (`mx-auto`) content jumps left/right as the
 * scrollbar appears and disappears between views.
 */
export const MainPane = forwardRef<HTMLElement, MainPaneProps>(
    function MainPane({ className, ...props }, ref) {
        return (
            <main
                ref={ref}
                className={cn(
                    'flex-1 overflow-y-auto [scrollbar-gutter:stable]',
                    className,
                )}
                {...props}
            />
        );
    },
);

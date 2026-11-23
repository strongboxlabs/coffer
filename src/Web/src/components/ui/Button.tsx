import { forwardRef, useCallback, useRef, type ButtonHTMLAttributes, type ReactNode, type RefObject } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';

import { cn } from '@/lib/cn';

import { usePendingButtonProps, useFallbackFocus } from './pending';
import { PendingLabel } from './PendingLabel';

// Hand-built Button primitive following shadcn-ish conventions. We
// don't pull shadcn/ui via its CLI for two reasons: (1) the CLI
// requires npm registry access during scaffolding, which is
// brittle for offline / restricted environments, and (2) maintaining
// ~10 source files we wrote ourselves is simpler than tracking
// upstream-generated files. The variant API is from
// class-variance-authority — the same library shadcn uses — so
// migrating later is trivial if we want to.
//
// Variants reference the semantic tokens declared in src/index.css
// (@theme block) — see ADR-0021. A palette tweak is a one-line CSS
// change, not a codebase-wide find-and-replace.

const buttonVariants = cva(
    // Base classes: focus ring + disabled state + size-stable. A pending
    // button (aria-disabled, see pending.ts) looks as a disabled one does
    // without giving up its pointer events: each variant restates its
    // resting look under aria-disabled:hover, so a pending button shows no
    // hover, while a caller's own hover still replaces the variant's.
    'inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-md text-sm font-medium ' +
        'transition-colors disabled:pointer-events-none disabled:opacity-50 ' +
        'aria-disabled:cursor-default aria-disabled:opacity-50 ' +
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:ring-accent',
    {
        variants: {
            variant: {
                primary: 'bg-accent text-text-inverse hover:bg-accent-hover aria-disabled:hover:bg-accent',
                secondary:
                    'bg-surface text-text border border-border hover:bg-surface-hover aria-disabled:hover:bg-surface',
                ghost: 'text-text hover:bg-surface-hover aria-disabled:hover:bg-transparent',
                // Destructive affirmative — for delete / revoke /
                // drop confirms (see ConfirmDialog).
                danger: 'bg-state-danger text-text-inverse hover:bg-state-danger/90 aria-disabled:hover:bg-state-danger',
                // An action that reads as a link inside a row: accent text,
                // underlined on hover. Only at `xs` (the types refuse any other
                // size): at the default 40px a link-styled control would be a
                // button-sized hit area dressed as inline text.
                link: 'text-accent hover:underline underline-offset-2 aria-disabled:hover:no-underline',
            },
            size: {
                md: 'h-control-40px px-4 py-2',
                sm: 'h-control-36px px-3',
                lg: 'h-control-44px px-8',
                // In-row actions (Post or skip…, Review…): the register's 28px
                // control height, so a row does not grow to fit its button.
                xs: 'h-control-28px px-2 text-xs',
            },
        },
        compoundVariants: [
            // A link keeps a little padding for its focus ring, not a button's.
            { variant: 'link', size: 'xs', class: 'px-1' },
        ],
        defaultVariants: {
            variant: 'primary',
            size: 'md',
        },
    },
);

type ButtonVariant = Exclude<NonNullable<VariantProps<typeof buttonVariants>['variant']>, 'link'>;
type ButtonSize = NonNullable<VariantProps<typeof buttonVariants>['size']>;

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
    /**
     * A request this button waits on is in flight: the button keeps focus and
     * refuses to act until it is not (pending.ts, ADR-0023 §L). `disabled` is
     * for a button that is unavailable for another reason.
     */
    pending?: boolean;
    /**
     * What the button reads while `pending` ("Saving…" for "Save"). Both labels
     * are laid out from the first render, so the button never changes size
     * (PendingLabel, pending.ts); neither the children nor this read the
     * pending state themselves.
     */
    pendingLabel?: ReactNode;
    /**
     * Where focus goes when this button, holding it, becomes disabled: the
     * field it waits on. A success that clears that field, or leaves nothing
     * changed, disables the button it was pressed on, and a browser would drop
     * focus to the page (useFallbackFocus in pending.ts).
     */
    fallbackFocusRef?: RefObject<HTMLElement | null>;
} & (
    | { variant?: ButtonVariant | null; size?: ButtonSize | null }
    | { variant: 'link'; size: 'xs' }
);

/**
 * Button with semantic variants + size scale.
 *
 * Always render an explicit `type` (`button` or `submit`); the
 * default HTML button type is `submit` which surprises forms. The
 * `type` prop is passed through unchanged — callers are responsible
 * for setting it.
 */
export const Button = forwardRef<HTMLButtonElement, ButtonProps>(
    function Button({
        className, variant, size, type, pending, pendingLabel, onClick, fallbackFocusRef, children, ...props
    }, ref) {
        const own = useRef<HTMLButtonElement | null>(null);
        const setRef = useCallback((node: HTMLButtonElement | null) => {
            own.current = node;
            if (typeof ref === 'function') ref(node);
            else if (ref !== null) ref.current = node;
        }, [ref]);
        useFallbackFocus(own, props.disabled === true, fallbackFocusRef);
        const pendingProps = usePendingButtonProps();
        return (
            <button
                ref={setRef}
                type={type ?? 'button'}
                className={cn(buttonVariants({ variant, size }), className)}
                {...props}
                {...pendingProps(pending, onClick)}
            >
                {pendingLabel === undefined
                    ? children
                    : <PendingLabel pending={pending === true} label={children} pendingLabel={pendingLabel} />}
            </button>
        );
    },
);

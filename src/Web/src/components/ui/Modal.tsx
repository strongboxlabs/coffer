import { useEffect, useRef, type ReactNode, type RefObject } from 'react';

import { cn } from '@/lib/cn';

// Modal — the single overlay shell for the app (ADR-0023 §L). Provides the
// pieces every dialog needs and that were previously hand-rolled (and drifting)
// in each one:
//
//   * backdrop + centered panel (one backdrop opacity, app-wide)
//   * Esc to dismiss, backdrop-click to dismiss (each opt-out-able)
//   * focus trap (Tab/Shift+Tab cycle within the panel)
//   * initial focus (an optional ref, else the first focusable, else the panel)
//   * return focus to the trigger on close
//   * role="dialog" aria-modal, labelled by `titleId` (preferred) or `ariaLabel`
//
// Controlled: render when `open`, unmount when not. The shell never closes
// itself — `onClose` fires on Esc / backdrop; the caller decides when to unmount.

const FOCUSABLE =
    'a[href],button:not([disabled]),textarea:not([disabled]),input:not([disabled]),select:not([disabled]),[tabindex]:not([tabindex="-1"])';

export interface ModalProps {
    open: boolean;
    onClose: () => void;
    /** id of the heading the caller renders inside — wires aria-labelledby. */
    titleId?: string;
    /** Accessible name when there's no visible heading to point at. */
    ariaLabel?: string;
    /** Panel sizing / extra classes (e.g. `max-w-md`). */
    className?: string;
    /** Element to focus on open; defaults to the first focusable in the panel. */
    initialFocusRef?: RefObject<HTMLElement | null>;
    dismissOnBackdrop?: boolean;
    dismissOnEsc?: boolean;
    children: ReactNode;
}

export function Modal({
    open,
    onClose,
    titleId,
    ariaLabel,
    className,
    initialFocusRef,
    dismissOnBackdrop = true,
    dismissOnEsc = true,
    children,
}: ModalProps) {
    const panelRef = useRef<HTMLDivElement | null>(null);

    useEffect(() => {
        if (!open) return;
        const trigger = document.activeElement as HTMLElement | null;

        // Initial focus: explicit ref → first focusable → the panel itself.
        const panel = panelRef.current;
        const focusInitial = () => {
            if (initialFocusRef?.current) {
                initialFocusRef.current.focus();
                return;
            }
            const first = panel?.querySelector<HTMLElement>(FOCUSABLE);
            (first ?? panel)?.focus();
        };
        focusInitial();

        function onKeyDown(e: KeyboardEvent) {
            if (e.key === 'Escape' && dismissOnEsc) {
                e.preventDefault();
                e.stopPropagation();
                onClose();
                return;
            }
            if (e.key !== 'Tab' || !panel) return;
            const items = Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE))
                .filter((el) => el.offsetParent !== null);
            if (items.length === 0) {
                e.preventDefault();
                panel.focus();
                return;
            }
            const first = items[0];
            const last = items[items.length - 1];
            const active = document.activeElement;
            if (e.shiftKey && active === first) {
                e.preventDefault();
                last.focus();
            } else if (!e.shiftKey && active === last) {
                e.preventDefault();
                first.focus();
            }
        }

        document.addEventListener('keydown', onKeyDown);
        return () => {
            document.removeEventListener('keydown', onKeyDown);
            // Return focus to whatever launched the modal.
            trigger?.focus?.();
        };
    }, [open, onClose, dismissOnEsc, initialFocusRef]);

    if (!open) return null;

    return (
        <div
            role="presentation"
            onClick={dismissOnBackdrop ? onClose : undefined}
            className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
        >
            <div
                ref={panelRef}
                role="dialog"
                aria-modal="true"
                aria-labelledby={titleId}
                aria-label={titleId ? undefined : ariaLabel}
                tabIndex={-1}
                onClick={(e) => e.stopPropagation()}
                className={cn(
                    'w-full rounded-lg border border-border bg-surface shadow-xl',
                    'focus-visible:outline-none',
                    className ?? 'max-w-md',
                )}
            >
                {children}
            </div>
        </div>
    );
}

import {
    useEffect,
    useInsertionEffect,
    useLayoutEffect,
    useRef,
    type KeyboardEvent as ReactKeyboardEvent,
    type ReactNode,
    type RefObject,
} from 'react';

import { cn } from '@/lib/cn';

import { happenedInside } from './popoverDismissal';
import { successorOf } from './rowRemovalFocus';

// Modal — the single overlay shell for the app (ADR-0023 §L). Provides the
// pieces every dialog needs, which drift when hand-rolled in each one:
//
//   * backdrop + centered panel (one backdrop opacity, app-wide)
//   * Esc to dismiss, backdrop-click to dismiss (each opt-out-able)
//   * focus trap (Tab/Shift+Tab cycle within the panel)
//   * initial focus: where the content put it (an autoFocus field, or content
//     that focused itself), else an optional ref, else the first focusable,
//     else the panel; the same under StrictMode, which runs the open effect
//     twice
//   * return focus to the trigger on close: what had focus before the dialog's
//     content could take it (see triggerRef), or to the host's fallback when
//     the trigger cannot take it back
//   * role="dialog" aria-modal, labelled by `titleId` (preferred) or `ariaLabel`
//   * an overlay that scrolls when the panel is taller than the viewport (the
//     panel itself never clips, so lists inside it overlay its edge)
//
// Controlled: render when `open`, unmount when not. The shell never closes
// itself — `onClose` fires on Esc / backdrop; the caller decides when to unmount.
//
// Escape belongs to the dialog it was pressed in. The panel handles it in its
// own React handler and stops it there, so a dialog opened from inside another
// (Add security from the investment editor) closes alone, and neither the
// dialog behind it nor a page handler ever sees the key. A control that used
// the Escape itself marks it with preventDefault, and the dialog then stays
// open: a second Escape closes it. A list closing on it (a picker) also stops
// it, so it never arrives here; an editor cancelling into onClose marks it
// only, and the check below is what keeps the dialog from closing twice.
// An Escape pressed while focus has left every dialog (the focused control was
// removed) reaches the document instead, and only the newest dialog closes.

const FOCUSABLE =
    'a[href],button:not([disabled]),textarea:not([disabled]),input:not([disabled]),select:not([disabled]),[tabindex]:not([tabindex="-1"])';

/** Every open dialog, oldest first, so a stray Escape closes the newest only. */
const openDialogs: object[] = [];

export interface ModalProps {
    open: boolean;
    onClose: () => void;
    /** id of the heading the caller renders inside — wires aria-labelledby. */
    titleId?: string;
    /** Accessible name when there's no visible heading to point at. */
    ariaLabel?: string;
    /** Panel sizing / extra classes (e.g. `max-w-md`). */
    className?: string;
    /** Element to focus on open, unless the content has already taken focus;
     *  defaults to the first focusable in the panel. */
    initialFocusRef?: RefObject<HTMLElement | null>;
    /**
     * Where focus goes on close when the trigger cannot take it back: it is
     * disabled by then, or gone from the page. Without one, focus would be
     * left on the page (ADR-0023 §L).
     */
    fallbackFocusRef?: RefObject<HTMLElement | null>;
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
    fallbackFocusRef,
    dismissOnBackdrop = true,
    dismissOnEsc = true,
    children,
}: ModalProps) {
    const panelRef = useRef<HTMLDivElement | null>(null);

    // The latest close handler, Esc setting and fallback, read when a key
    // arrives or the dialog closes. The open effect below must not depend on
    // them: a host that passes an inline `onClose` gets a new one on every
    // render, and re-running the effect returned focus to the trigger and then
    // to the first control, pulling it out of whatever field the user was
    // typing in.
    const onCloseRef = useRef(onClose);
    const dismissOnEscRef = useRef(dismissOnEsc);
    const fallbackFocusRefRef = useRef(fallbackFocusRef);
    useLayoutEffect(() => {
        onCloseRef.current = onClose;
        dismissOnEscRef.current = dismissOnEsc;
        fallbackFocusRefRef.current = fallbackFocusRef;
    });

    // What had focus when the dialog opened, given back on close. Read in an
    // insertion effect, which React runs while it puts the dialog on the page,
    // before any layout effect. The dialog's content can take focus before the
    // open effect below runs: a field with autoFocus is focused in the layout
    // phase, and the content's own effects run before this dialog's. Read
    // there, the trigger would be that field, which closing removes, and focus
    // would fall to the page instead of going back to the control the dialog
    // was opened from (a field in the dialog behind, for one opened over
    // another).
    //
    // A dialog that opens as another closes, in the same update, can record
    // nothing usable there. React removes the closing dialog in that update:
    // focus has either fallen to the page already, or is still on a control
    // of the closing dialog that goes later in the same update. The closing
    // dialog gives focus back to its own trigger before the open effect below
    // runs, so when what was recorded is the page or has left it, the open
    // effect takes what has focus then, if it is outside this dialog.
    const triggerRef = useRef<HTMLElement | null>(null);
    // The field the content focused when the dialog opened, kept for the open:
    // React's StrictMode runs the open effect, its cleanup (which gives focus
    // back to the trigger) and the effect again, and an autoFocus field is not
    // focused a second time. The second run opens on that field from here,
    // where reading focus again would find the trigger.
    const ownFocusRef = useRef<HTMLElement | null>(null);
    useInsertionEffect(() => {
        if (open) {
            triggerRef.current = document.activeElement as HTMLElement | null;
            ownFocusRef.current = null;
        }
    }, [open]);
    // The content's own focus, read once the content's layout work is done (an
    // autoFocus field, a layout effect of the content) and before any passive
    // effect runs. A dialog that opens as another closes needs it: the closing
    // dialog gives focus back to its own trigger in its passive cleanup, which
    // runs before the open effect below, so focus read there has already left
    // the field. Only ever set, never cleared: under StrictMode the second run
    // finds focus on the trigger, and the open effect restores the field.
    useLayoutEffect(() => {
        if (!open) return;
        const focused = document.activeElement as HTMLElement | null;
        if (focused !== null && panelRef.current?.contains(focused)) ownFocusRef.current = focused;
    }, [open]);

    useEffect(() => {
        if (!open) return;
        const panel = panelRef.current;
        const usable = (el: HTMLElement | null): el is HTMLElement =>
            el !== null && el.isConnected && el !== document.body;
        const recorded = triggerRef.current;
        const focusedNow = document.activeElement as HTMLElement | null;
        // Focus the dialog's content has already taken: a field with
        // autoFocus, or an effect of the content, both of which run before
        // this one.
        const focusInside = focusedNow !== null && panel !== null && panel.contains(focusedNow);
        if (focusInside) ownFocusRef.current = focusedNow;
        const own = ownFocusRef.current;
        const trigger = usable(recorded)
            ? recorded
            : usable(focusedNow) && !focusInside
                ? focusedNow
                : null;
        const token = {};
        openDialogs.push(token);

        // Initial focus: the content's own wins, so a dialog whose field
        // focuses itself opens on that field; else the explicit ref → the
        // first focusable → the panel itself.
        const focusInitial = () => {
            if (focusInside) return;
            if (own !== null && own.isConnected) {
                own.focus();
                return;
            }
            if (initialFocusRef?.current) {
                initialFocusRef.current.focus();
                return;
            }
            const first = panel?.querySelector<HTMLElement>(FOCUSABLE);
            (first ?? panel)?.focus();
        };
        focusInitial();

        function onKeyDown(e: KeyboardEvent) {
            if (e.key === 'Escape') {
                // An Escape pressed inside a panel never gets here: the panel
                // stops it. This one came from outside every dialog, so it
                // closes the newest.
                if (e.defaultPrevented || !dismissOnEscRef.current) return;
                if (openDialogs[openDialogs.length - 1] !== token) return;
                if (happenedInside(e, panel)) return;
                e.preventDefault();
                onCloseRef.current();
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
            const at = openDialogs.indexOf(token);
            if (at >= 0) openDialogs.splice(at, 1);
            // Return focus to whatever launched the modal. When that cannot take
            // it: a row's control its list took away hands it to the same
            // control on the next row, the previous one, or the list's add
            // control (rowRemovalFocus.ts); otherwise (disabled by now, or gone)
            // it goes to the fallback the host names.
            trigger?.focus?.();
            if (trigger === null || document.activeElement !== trigger) {
                const successor = trigger !== null && !trigger.isConnected ? successorOf(trigger) : null;
                (successor ?? fallbackFocusRefRef.current?.current)?.focus();
            }
        };
    }, [open, initialFocusRef]);

    // This dialog's own Escape. Stopped here whatever happens next, so a
    // dialog behind this one, the editor that opened it, or a page shortcut
    // cannot act on it too.
    function onPanelKeyDown(e: ReactKeyboardEvent<HTMLDivElement>) {
        if (e.key !== 'Escape') return;
        e.stopPropagation();
        // A control inside already used this Escape (a picker closed its list,
        // an editor cancelled into onClose): leave the dialog as it is.
        if (e.defaultPrevented || !dismissOnEsc) return;
        e.preventDefault();
        onClose();
    }

    if (!open) return null;

    // The overlay scrolls, never the panel. A panel that scrolled would clip
    // every absolutely positioned list inside it (a picker's options, the
    // security search); this way a list overlays the panel's edge as it does
    // on a page, and a dialog taller than the viewport scrolls with the
    // overlay. The inner wrapper is at least the overlay's height, so a short
    // panel stays centred; a click on it is a backdrop click.
    return (
        <div
            role="presentation"
            onClick={dismissOnBackdrop ? onClose : undefined}
            className="fixed inset-0 z-50 overflow-y-auto bg-black/40"
        >
            <div className="flex min-h-full items-center justify-center p-4">
                <div
                    ref={panelRef}
                    role="dialog"
                    aria-modal="true"
                    aria-labelledby={titleId}
                    aria-label={titleId ? undefined : ariaLabel}
                    tabIndex={-1}
                    onClick={(e) => e.stopPropagation()}
                    onKeyDown={onPanelKeyDown}
                    className={cn(
                        'w-full rounded-lg border border-border bg-surface shadow-xl',
                        'focus-visible:outline-none',
                        className ?? 'max-w-md',
                    )}
                >
                    {children}
                </div>
            </div>
        </div>
    );
}

/**
 * Auto-grow for a multi-line field (ADR-0023 §N): the textarea's height fits its
 * text, or its placeholder while it is empty, up to `maxPx`, past which the field
 * scrolls.
 *
 * Two things the measurement must get right:
 *
 * - The border. `scrollHeight` is the content and the padding without the
 *   border, and the field's box is border-box (Tailwind's preflight), so the
 *   height is `scrollHeight` plus the top and bottom borders. Set to
 *   `scrollHeight` alone, the box is short by the border, and a field whose text
 *   fits scrolls by that much and shows a scrollbar.
 * - The scrollbar. The height is read while the field cannot scroll. Read while
 *   it can, a field that overflows at `height: auto` draws a scrollbar for the
 *   measurement, its text wraps at the narrower width, and the height comes out
 *   a line taller than the text needs once the scrollbar is gone.
 *
 * The field's own classes set its overflow; the inline value is cleared after.
 */
export function fitTextareaHeight(el: HTMLTextAreaElement, maxPx: number): void {
    el.style.height = 'auto';
    el.style.overflowY = 'hidden';
    const content = el.scrollHeight;
    el.style.overflowY = '';
    const style = getComputedStyle(el);
    const border = (parseFloat(style.borderTopWidth) || 0) + (parseFloat(style.borderBottomWidth) || 0);
    el.style.height = `${Math.min(content + border, maxPx)}px`;
}

/**
 * Fit the field again whenever its width changes; returns the cleanup.
 *
 * A window or rail resize, or a dialog crossing its narrow breakpoint, wraps the
 * text onto a different number of lines, and the height fitted at the old width
 * then scrolls the text or leaves the field taller than it. Only the width is
 * a reason: the fit itself changes the height, and a scrollbar that appears
 * narrows the content box but not the field.
 */
export function refitOnWidthChange(el: HTMLTextAreaElement, maxPx: number): () => void {
    let width = el.offsetWidth;
    let frame = 0;
    const observer = new ResizeObserver(() => {
        if (el.offsetWidth === width) return;
        width = el.offsetWidth;
        // In the next frame, not in this callback: resizing the observed box from
        // its own notification is a ResizeObserver loop.
        cancelAnimationFrame(frame);
        frame = requestAnimationFrame(() => fitTextareaHeight(el, maxPx));
    });
    observer.observe(el);
    return () => {
        observer.disconnect();
        cancelAnimationFrame(frame);
    };
}

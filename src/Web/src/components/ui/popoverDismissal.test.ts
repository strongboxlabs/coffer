import { afterEach, describe, expect, it } from 'vitest';

import { happenedInside } from './popoverDismissal';

/**
 * A press is inside an element when it landed on the element or on anything in
 * it, as the page stood when the press was dispatched (ADR-0023 §L). A control
 * that removes itself as the press goes down was still pressed inside.
 */

afterEach(() => {
    document.body.replaceChildren();
});

/** root > control > option, on the page. */
function page() {
    const root = document.createElement('div');
    const control = document.createElement('div');
    const option = document.createElement('span');
    const elsewhere = document.createElement('button');
    control.append(option);
    root.append(control);
    document.body.append(root, elsewhere);
    return { root, control, option, elsewhere };
}

/** What `happenedInside(event, root)` answered in a listener on the document. */
function askedOnTheDocument(target: Element, root: Element | string | null, before?: (e: Event) => void): boolean {
    let answer: boolean | undefined;
    const onDown = (e: Event) => {
        answer = typeof root === 'string' ? happenedInside(e, root) : happenedInside(e, root);
    };
    if (before !== undefined) target.addEventListener('mousedown', before);
    document.addEventListener('mousedown', onDown);
    try {
        target.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
    } finally {
        document.removeEventListener('mousedown', onDown);
        if (before !== undefined) target.removeEventListener('mousedown', before);
    }
    if (answer === undefined) throw new Error('the press never reached the document');
    return answer;
}

describe('happenedInside', () => {
    it('counts a press on the element and on anything in it', () => {
        const { root, option } = page();
        expect(askedOnTheDocument(root, root)).toBe(true);
        expect(askedOnTheDocument(option, root)).toBe(true);
    });

    it('does not count a press elsewhere on the page', () => {
        const { root, elsewhere } = page();
        expect(askedOnTheDocument(elsewhere, root)).toBe(false);
    });

    it('counts nothing inside an element that is not there', () => {
        const { option } = page();
        expect(askedOnTheDocument(option, null)).toBe(false);
    });

    it('counts a press on a control that took itself off the page before the listener heard it', () => {
        const { root, control, option } = page();
        let connectedWhenAsked: boolean | undefined;

        // A pick that commits as the mouse goes down and takes its list away,
        // as React does before the document's listeners run.
        const inside = askedOnTheDocument(option, root, () => {
            control.remove();
            connectedWhenAsked = option.isConnected;
        });

        expect(connectedWhenAsked).toBe(false);
        expect(inside).toBe(true);
    });
});

describe('happenedInside, asked with a selector', () => {
    /** popup > control > option, on the page; the popup is the one element marked. */
    function popupPage() {
        const { root, control, option, elsewhere } = page();
        root.setAttribute('data-popup', '');
        return { popup: root, control, option, elsewhere };
    }

    it('counts an event on an element that matches, and on anything in one', () => {
        const { popup, option } = popupPage();
        expect(askedOnTheDocument(popup, '[data-popup]')).toBe(true);
        expect(askedOnTheDocument(option, '[data-popup]')).toBe(true);
    });

    it('does not count an event in no element that matches', () => {
        const { option, elsewhere } = popupPage();
        expect(askedOnTheDocument(elsewhere, '[data-popup]')).toBe(false);
        expect(askedOnTheDocument(option, '[data-other]')).toBe(false);
    });

    it('counts an event on a control that took itself out of the popup before the listener heard it', () => {
        const { popup, control, option } = popupPage();
        let closestWhenAsked: Element | null | undefined;

        // The popup stays; only the control goes, so the popup is no longer
        // among the option's ancestors when the document hears the event.
        const inside = askedOnTheDocument(option, '[data-popup]', () => {
            control.remove();
            closestWhenAsked = option.closest('[data-popup]');
        });

        expect(popup.isConnected).toBe(true);
        expect(closestWhenAsked).toBeNull();
        expect(inside).toBe(true);
    });
});

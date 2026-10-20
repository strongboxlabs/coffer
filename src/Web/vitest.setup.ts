// Global Vitest setup. Extends Vitest's expect with the matchers
// from @testing-library/jest-dom (toBeInTheDocument, toHaveValue,
// toBeDisabled, etc.) so component tests read idiomatically.
import '@testing-library/jest-dom/vitest';
import { configure } from '@testing-library/react';
import { vi } from 'vitest';

// testing-library's findBy*/waitFor default to a 1000ms timeout. That is a race
// against however long the component takes to render its first data-dependent
// element, and on a loaded machine the machine wins: a CI run that took 1111s
// against a normal ~500s failed RegisterRouter's `findByRole('columnheader')`
// while the same test passed in isolation, in the full local suite, and on a
// re-run. A false red is worse than a slow test — it trains everyone to re-run
// the gate instead of reading it.
//
// Raised globally rather than per call site because there is nothing special
// about that one assertion: 67 test files use findBy* the same way, so a
// one-line fix there would just relocate the flake. The cost is only paid when a
// test genuinely fails (it then takes 5s to say so, not 1s); passing tests
// resolve as soon as the element appears and are unaffected.
configure({ asyncUtilTimeout: 5000 });
import type * as React from 'react';
import { createElement, forwardRef, useImperativeHandle } from 'react';

// jsdom doesn't ship ResizeObserver; react-virtuoso ships it as a
// hard dep on the rendering path (each item is measured via RO).
// A noop stub is enough — our tests don't depend on layout flow.
if (typeof globalThis.ResizeObserver === 'undefined') {
    globalThis.ResizeObserver = class {
        observe() {}
        unobserve() {}
        disconnect() {}
    } as unknown as typeof ResizeObserver;
}

// jsdom implements no scrolling at all, so Element.prototype.scrollIntoView is
// simply absent and any call to it throws. Every browser has had it for a
// decade, so the gap is the environment's, not the code's — stubbed here for
// the same reason as ResizeObserver above, rather than guarded at each call
// site, which would mean writing `el.scrollIntoView?.()` forever to describe a
// method that is never actually missing in production.
if (typeof Element.prototype.scrollIntoView !== 'function') {
    Element.prototype.scrollIntoView = function scrollIntoView() {};
}

// react-virtuoso uses layout APIs (scrollHeight / IntersectionObserver
// / ResizeObserver) that jsdom doesn't implement, so under the
// default behaviour the `<Virtuoso>` body renders empty — tests that
// assert on row content (status badge, payee text, etc.) can't find
// anything. Mock it to a plain list that renders every data item via
// `itemContent`. Pagination + viewport callbacks are no-ops in tests;
// the `ref.scrollIntoView` is also stubbed so keyboard-nav code paths
// don't throw. This matches the BankRegisterPage / InvestmentRegisterPage
// rendering closely enough that DOM-level assertions reflect reality.
vi.mock('react-virtuoso', () => {
    type ItemContent<T> = (index: number, item: T) => React.ReactNode;
    type VirtuosoProps<T> = {
        data?: readonly T[];
        itemContent?: ItemContent<T>;
        computeItemKey?: (index: number, item: T) => string | number;
    };
    const Virtuoso = forwardRef<unknown, VirtuosoProps<unknown>>((props, ref) => {
        useImperativeHandle(ref, () => ({
            scrollIntoView: () => {},
            scrollToIndex: () => {},
            getState: () => null,
        }));
        const { data = [], itemContent, computeItemKey } = props;
        return createElement(
            'div',
            { 'data-testid': 'virtuoso-mock' },
            data.map((item, index) =>
                createElement(
                    'div',
                    {
                        key: computeItemKey ? computeItemKey(index, item) : index,
                        'data-virtuoso-index': index,
                    },
                    itemContent ? itemContent(index, item) : null,
                ),
            ),
        );
    });
    return { Virtuoso };
});

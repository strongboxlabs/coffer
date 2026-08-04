// Global Vitest setup. Extends Vitest's expect with the matchers
// from @testing-library/jest-dom (toBeInTheDocument, toHaveValue,
// toBeDisabled, etc.) so component tests read idiomatically.
import '@testing-library/jest-dom/vitest';
import { vi } from 'vitest';
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

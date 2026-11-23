// Global Vitest setup. Extends Vitest's expect with the matchers
// from @testing-library/jest-dom (toBeInTheDocument, toHaveValue,
// toBeDisabled, etc.) so component tests read idiomatically.
import '@testing-library/jest-dom/vitest';
import { cleanup, configure } from '@testing-library/react';
import { afterAll, afterEach, vi } from 'vitest';

import {
    refuseEmptyPathSegment,
    requestLine,
    settledEmptySegmentRequests,
} from './src/lib/annotations/fetchStub';

// A request whose path has an empty segment was built from an empty id
// (`/api/ledgers//reports/pins`). It is refused in three places: by the shared
// fetch stub; by the environment's own fetch, which is what a request no test
// routed reaches; and by the API client's request functions, whatever fetch a
// test installed (below). Refusing is not enough on its own: a query keeps a
// failed fetch as its error state and the test goes on, so the test that sent
// it is failed here, after it, from what was refused, naming the request.
const environmentFetch = globalThis.fetch;
if (typeof environmentFetch === 'function') {
    globalThis.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
        const { method, raw } = requestLine(input, init);
        const refused = refuseEmptyPathSegment(method, raw);
        return refused === null ? environmentFetch(input, init) : Promise.reject(refused);
    };
}

// A test that installs a fetch of its own (vi.stubGlobal, vi.spyOn) replaces
// the one above, and its check with it. So the API client's request functions,
// which every API module sends through, check the path here themselves, before
// the request reaches whatever fetch is installed. A test that mocks an API
// module builds no URL, so there is nothing to check. The two requests auth.ts
// sends with fetch directly (a setup restore's upload and its outcome poll) are
// checked only by the environment's fetch and the shared stub.
vi.mock('./src/lib/api/_request', async (importOriginal) => {
    const client = await importOriginal<typeof import('./src/lib/api/_request')>();
    const refusal = (method: string, path: string) => refuseEmptyPathSegment(method.toUpperCase(), path);
    const request = <T>(path: string, options?: Parameters<typeof client.request>[1]): Promise<T> => {
        const refused = refusal(options?.method ?? 'GET', path);
        return refused === null ? client.request<T>(path, options) : Promise.reject(refused);
    };
    const requestBlob: typeof client.requestBlob = (path, signal) => {
        const refused = refusal('GET', path);
        return refused === null ? client.requestBlob(path, signal) : Promise.reject(refused);
    };
    const requestMultipart = <T>(
        path: string,
        ...rest: Parameters<typeof client.requestMultipart> extends [string, ...infer R] ? R : never
    ): Promise<T> => {
        const refused = refusal('POST', path);
        return refused === null ? client.requestMultipart<T>(path, ...rest) : Promise.reject(refused);
    };
    return { ...client, request, requestBlob, requestMultipart };
});

function failOnRefused(refused: readonly string[], what: string, note = ''): void {
    if (refused.length === 0) return;
    throw new Error(`${what}:\n${refused.map((line) => `  ${line}`).join('\n')}${note}`);
}

// What a test sent is taken once its page is gone and its work has had a turn
// of the event loop, so a request sent as the page unmounts, or just after the
// test ends, is this test's. The page is unmounted here, first: testing-library
// unmounts it in a hook of its own, but that one runs after this (the hooks
// after a test run in the reverse of the order they were added, and it was
// added first), and a second cleanup does nothing.
afterEach(async () => {
    cleanup();
    failOnRefused(
        await settledEmptySegmentRequests(),
        'A request was sent with an empty path segment, so an id in its URL was empty',
        '\nOne sent more than a turn of the event loop after its own test ended is reported here, by the test after it.',
    );
});
// After the file's last test nothing else would report one, so it fails the file.
afterAll(async () => {
    failOnRefused(
        await settledEmptySegmentRequests(),
        'After the last test in this file, a request was sent with an empty path segment, so an id in its URL was empty',
    );
});

// testing-library's findBy*/waitFor default to a 1000ms timeout. That is a race
// against however long the component takes to render its first data-dependent
// element, and on a loaded machine the machine wins: a findBy* that passes in
// isolation and on a re-run fails when the runner is slow. A false red is worse
// than a slow test — it trains everyone to re-run the gate instead of reading it.
//
// Raised globally rather than per call site because no one assertion is
// special: 67 test files use findBy* the same way, so a fix at one call site
// would just relocate the flake. The cost is only paid when a
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

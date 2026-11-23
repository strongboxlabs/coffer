import { vi } from 'vitest';

/**
 * Test support: a `fetch` stand-in that answers by method and path, so a page
 * test exercises the real API client (URL building, JSON parsing, error
 * mapping) against the wire shapes the server will send, instead of mocking
 * the client functions and assuming their contract.
 *
 * Used where the server half is not built yet: the stubbed responses ARE the
 * frozen contract, and a client that read a field the contract does not carry
 * fails here rather than in production.
 *
 * An unrouted request answers 404 and is recorded, so a test that forgot a
 * route sees it in `unrouted` instead of rendering against nothing.
 *
 * A request whose path has an empty segment is refused, and the test that sent
 * it fails (see {@link refuseEmptyPathSegment}).
 */
export interface StubbedCall {
    method: string;
    path: string;
    search: URLSearchParams;
    body: unknown;
}

type Handler = (call: StubbedCall) => unknown;

/** A refusal: answered as ProblemDetails with this status, detail and code, and any
 *  extension members the refusal carries (`currencyCode`, `count`, ...). */
export class StubProblem {
    constructor(
        readonly status: number,
        readonly detail: string,
        readonly code?: string,
        readonly extensions: Readonly<Record<string, unknown>> = {},
    ) {}
}

/** Route answer for a refusal, e.g. `problem(422, 'Not here.', 'read-only-row')`. */
export function problem(
    status: number,
    detail: string,
    code?: string,
    extensions?: Readonly<Record<string, unknown>>,
): StubProblem {
    return new StubProblem(status, detail, code, extensions);
}

interface Route {
    method: string;
    pattern: RegExp;
    handler: Handler;
}

export interface FetchStub {
    on(method: string, pattern: RegExp, handler: Handler | unknown): FetchStub;
    calls: StubbedCall[];
    unrouted: StubbedCall[];
    callsTo(method: string, pattern: RegExp): StubbedCall[];
}

/**
 * Requests refused for an empty path segment and not yet reported. The suite's
 * setup fails the test that sent one (vitest.setup.ts).
 */
const emptySegmentRequests: string[] = [];

/** What a `fetch` call asks for: its method and the URL as the caller wrote it. */
export function requestLine(input: RequestInfo | URL, init?: RequestInit): { method: string; raw: string } {
    const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    return { method: (init?.method ?? 'GET').toUpperCase(), raw };
}

/**
 * Whether a request URL's path has an empty segment: `//` after the origin, or
 * a `/` that ends it. Every path the API serves is made of named segments, so
 * an empty one means an id the URL was built from was empty, as in
 * `/api/ledgers//reports/pins`. The server answers it 404, and the SPA has
 * asked for something it never meant to.
 */
export function hasEmptyPathSegment(raw: string): boolean {
    const path = raw.replace(/^[a-z][a-z\d+.-]*:\/\/[^/?#]*/i, '').split(/[?#]/, 1)[0];
    return path.includes('//') || (path.length > 1 && path.endsWith('/'));
}

/**
 * Refuses a request whose path has an empty segment: records it for the suite
 * to fail the test with, and returns the error to reject the request with.
 * Null when the path is whole. Rejecting alone would not do: a query swallows a
 * failed fetch into its error state, and the test would pass.
 */
export function refuseEmptyPathSegment(method: string, raw: string): TypeError | null {
    if (!hasEmptyPathSegment(raw)) return null;
    const line = `${method} ${raw}`;
    emptySegmentRequests.push(line);
    return new TypeError(`Refused ${line}: its path has an empty segment, so an id in it was empty.`);
}

/** The refused requests not yet reported, emptying the list. */
export function takeEmptySegmentRequests(): string[] {
    return emptySegmentRequests.splice(0);
}

/** The environment's own timer, taken before any test can install fake ones. */
const realSetTimeout = globalThis.setTimeout;

/**
 * The refused requests not yet reported, taken after one turn of the event
 * loop. Work a test started can send a request just after the test ends (a
 * promise continuation, a query's batched notification, a timer of 0), and
 * the turn lets it be reported with that test rather than the next. The turn
 * runs on the environment's timer, so a test that left fake timers on does not
 * hold the report up.
 */
export async function settledEmptySegmentRequests(): Promise<string[]> {
    await new Promise<void>((resolve) => {
        realSetTimeout(resolve, 0);
    });
    return takeEmptySegmentRequests();
}

export function stubFetch(): FetchStub {
    const routes: Route[] = [];
    const calls: StubbedCall[] = [];
    const unrouted: StubbedCall[] = [];

    const fetchImpl = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const { method, raw } = requestLine(input, init);
        const url = new URL(raw, 'http://localhost');
        const body = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
        const call: StubbedCall = { method, path: url.pathname, search: url.searchParams, body };
        calls.push(call);

        const refused = refuseEmptyPathSegment(method, raw);
        if (refused !== null) throw refused;

        // Last registered wins, so a test can override a default route.
        const route = [...routes].reverse().find(
            (r) => r.method === method && r.pattern.test(url.pathname),
        );
        if (route === undefined) {
            unrouted.push(call);
            return new Response(JSON.stringify({ title: 'Not routed', detail: `${method} ${url.pathname}` }), {
                status: 404,
                headers: { 'Content-Type': 'application/problem+json' },
            });
        }
        const result = route.handler(call);
        if (result === undefined) return new Response(null, { status: 204 });
        if (result instanceof StubProblem) {
            return new Response(JSON.stringify({
                title: 'Refused',
                status: result.status,
                detail: result.detail,
                ...(result.code === undefined ? {} : { code: result.code }),
                ...result.extensions,
            }), {
                status: result.status,
                headers: { 'Content-Type': 'application/problem+json' },
            });
        }
        return new Response(JSON.stringify(result), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
        });
    });
    vi.stubGlobal('fetch', fetchImpl);

    const stub: FetchStub = {
        on(method, pattern, handler) {
            routes.push({
                method,
                pattern,
                handler: typeof handler === 'function' ? (handler as Handler) : () => handler,
            });
            return stub;
        },
        calls,
        unrouted,
        callsTo(method, pattern) {
            return calls.filter((c) => c.method === method && pattern.test(c.path));
        },
    };
    return stub;
}

// Thin fetch wrapper around the Coffer API.
//
// Conventions enforced here:
//   - `credentials: 'include'`: the auth cookie is HttpOnly + SameSite=Strict;
//     setting `include` is the explicit signal that we want the cookie sent
//     with every request. In dev, the Vite proxy serves the API at /api
//     so requests are same-origin and this is technically the default —
//     but setting it explicitly makes the production path (where the
//     SPA might be served from a different origin) safe too.
//
//   - Every response is parsed once: 2xx → body deserialized as T; 4xx/5xx
//     → ApiError thrown with the ProblemDetails envelope decoded (status,
//     code, detail). Endpoint code never sees a raw `Response` object;
//     it gets either the typed payload or a typed error.
//
//   - JSON-only. Multipart / streaming endpoints get their own helpers
//     when we need them; not in the PR 4.1 scope.
//
// CSRF posture: the session cookie is SameSite=Strict, so another SITE cannot
// send it — but a sibling subdomain is the same site. The server therefore also
// refuses any cookie-carrying unsafe request the browser does not mark as
// same-origin (Sec-Fetch-Site / Origin; SameOriginRequestMiddleware). The browser
// sets those headers itself, so the SPA needs no CSRF token of its own.

import type { RelabelMergedInto } from '../types/account';
import type { RateUnavailable } from '../types/conversion';

/** Error thrown when the API returns 4xx/5xx. */
export class ApiError extends Error {
    /** HTTP status code from the response. */
    readonly status: number;

    /**
     * Stable business-rule code from the ProblemDetails extension
     * (see API engineering-standards §5.3). Undefined for non-422
     * errors and for responses that didn't carry a `code` field.
     */
    readonly code: string | undefined;

    /** Human-readable detail from ProblemDetails. */
    readonly detail: string;

    /**
     * The missing rate a `rate-unavailable` refusal names, from the ProblemDetails
     * extension member of the same name (ADR-0114 D9, D12). Undefined on every other
     * error. The detail already names the pair and day in words.
     */
    readonly rateUnavailable: RateUnavailable | undefined;

    /**
     * The missing rates a `rate-unavailable` refusal names as a list: a share move between
     * brokerages in different currencies names each missing stored leg (account currency
     * into the reporting currency) and its day (ADR-0114 D13). Empty on every other error,
     * and on one whose `rateUnavailable` is a single rate.
     */
    readonly rateUnavailableLegs: readonly RateUnavailable[];

    /**
     * The transactions a `relabel-pair-not-representable` refusal names, from its
     * `mergedInto` member: each one that a refused pair's transaction, which no register
     * shows, was merged into (ADR-0114 D13, decided by the owner, 2026-11-18), followed
     * to the end of a chain of merges (settled by Claude, 2026-11-18). Empty on
     * every other error, and on that refusal when every pair is on a row a register shows.
     */
    readonly mergedInto: readonly RelabelMergedInto[];

    /**
     * The Demo a refused or failed `POST /api/ledgers/demo` had already named, from the
     * ProblemDetails extension member of the same name (ADR-0115 D9): the hub shows that
     * build as failed under its name. Undefined on every other error, and on a Demo
     * refusal that came before a name was chosen (another import running).
     */
    readonly ledgerName: string | undefined;

    constructor(
        status: number,
        detail: string,
        code?: string,
        rateUnavailable?: RateUnavailable,
        rateUnavailableLegs: readonly RateUnavailable[] = [],
        mergedInto: readonly RelabelMergedInto[] = [],
        ledgerName?: string,
    ) {
        super(detail);
        this.name = 'ApiError';
        this.status = status;
        this.code = code;
        this.detail = detail;
        this.rateUnavailable = rateUnavailable;
        this.rateUnavailableLegs = rateUnavailableLegs;
        this.mergedInto = mergedInto;
        this.ledgerName = ledgerName;
    }
}

interface RequestOptions {
    method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
    body?: unknown;
    signal?: AbortSignal;
    /** Extra request headers, e.g. a step-up proof (see `stepUp.ts`). */
    headers?: Record<string, string>;
}

/**
 * Generic typed request. T is the response shape. Callers should
 * supply T explicitly to keep the boundary tight.
 */
export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
    // `request` stringifies the body itself. A caller that already did leaves the server a
    // JSON *string* where it expects an object, and it answers 400 — this shipped twice
    // (notifications, then MCP rename) because `body` is `unknown` and the compiler can't
    // tell. No endpoint takes a bare JSON string, so refuse it where it is cheapest to see.
    if (typeof options.body === 'string') {
        throw new TypeError(
            `request(${path}): body is already a string; pass the object — request() serializes it.`,
        );
    }

    const init: RequestInit = {
        method: options.method ?? 'GET',
        credentials: 'include',
        headers: {
            Accept: 'application/json',
            ...(options.body !== undefined ? { 'Content-Type': 'application/json' } : {}),
            ...options.headers,
        },
        ...(options.body !== undefined ? { body: JSON.stringify(options.body) } : {}),
        ...(options.signal ? { signal: options.signal } : {}),
    };

    const response = await fetch(path, init);

    if (!response.ok) {
        throw await buildApiError(response);
    }

    // 204 No Content has no body to parse.
    if (response.status === 204) {
        return undefined as T;
    }

    return (await response.json()) as T;
}

/**
 * Binary GET — returns the response body as a Blob (e.g. a backup artifact
 * download). Same auth/error contract as {@link request}: cookie included,
 * 4xx/5xx → ApiError. Kept separate because {@link request} is JSON-only.
 */
export async function requestBlob(path: string, signal?: AbortSignal): Promise<Blob> {
    const response = await fetch(path, {
        method: 'GET',
        credentials: 'include',
        ...(signal ? { signal } : {}),
    });
    if (!response.ok) {
        throw await buildApiError(response);
    }
    return response.blob();
}

/**
 * Multipart POST — uploads a {@link FormData} body (file + fields) and
 * deserializes the JSON response as T. Same auth/error contract as
 * {@link request}: cookie included, 4xx/5xx → ApiError. Content-Type is
 * intentionally left unset so the browser adds the multipart boundary;
 * setting it manually strips the boundary and the server rejects the upload.
 */
export async function requestMultipart<T>(
    path: string,
    body: FormData,
    signal?: AbortSignal,
    headers?: Record<string, string>,
): Promise<T> {
    const response = await fetch(path, {
        method: 'POST',
        credentials: 'include',
        headers: { Accept: 'application/json', ...headers },
        body,
        ...(signal ? { signal } : {}),
    });
    if (!response.ok) {
        throw await buildApiError(response);
    }
    if (response.status === 204) {
        return undefined as T;
    }
    return (await response.json()) as T;
}

async function buildApiError(response: Response): Promise<ApiError> {
    // Try to parse a ProblemDetails body. If the response isn't JSON
    // (network proxy returned text/html etc.), fall back to a
    // status-only error.
    let code: string | undefined;
    let rateUnavailable: RateUnavailable | undefined;
    let rateUnavailableLegs: RateUnavailable[] = [];
    let mergedInto: RelabelMergedInto[] = [];
    let ledgerName: string | undefined;
    let detail = response.statusText || `HTTP ${response.status}`;
    try {
        const body = (await response.json()) as {
            detail?: string;
            title?: string;
            code?: string;
            rateUnavailable?: unknown;
            mergedInto?: unknown;
            ledgerName?: unknown;
        };
        if (typeof body.detail === 'string' && body.detail.length > 0) {
            detail = body.detail;
        } else if (typeof body.title === 'string' && body.title.length > 0) {
            detail = body.title;
        }
        if (typeof body.code === 'string') {
            code = body.code;
        }
        if (isRateUnavailable(body.rateUnavailable)) {
            rateUnavailable = body.rateUnavailable;
        } else if (Array.isArray(body.rateUnavailable)) {
            rateUnavailableLegs = body.rateUnavailable.filter(isRateUnavailable);
        }
        if (Array.isArray(body.mergedInto)) {
            mergedInto = body.mergedInto.filter(isRelabelMergedInto);
        }
        if (typeof body.ledgerName === 'string' && body.ledgerName.length > 0) {
            ledgerName = body.ledgerName;
        }
    } catch {
        // Body wasn't JSON; keep the status-text fallback.
    }
    return new ApiError(response.status, detail, code, rateUnavailable, rateUnavailableLegs, mergedInto, ledgerName);
}

function isRelabelMergedInto(value: unknown): value is RelabelMergedInto {
    if (typeof value !== 'object' || value === null) return false;
    const v = value as Record<string, unknown>;
    return typeof v.headerId === 'string'
        && typeof v.accountId === 'string'
        && typeof v.account === 'string'
        && typeof v.date === 'string'
        && (v.payee === null || typeof v.payee === 'string')
        && typeof v.hidden === 'boolean';
}

function isRateUnavailable(value: unknown): value is RateUnavailable {
    if (typeof value !== 'object' || value === null) return false;
    const v = value as Record<string, unknown>;
    return typeof v.fromCurrency === 'string'
        && typeof v.toCurrency === 'string'
        && typeof v.date === 'string'
        && v.reason === 'rate_unavailable';
}

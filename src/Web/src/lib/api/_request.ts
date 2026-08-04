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
// CSRF posture: every state-mutating endpoint authenticates via the
// SameSite=Strict cookie, which a cross-site form post cannot send.
// Combined with the WebAuthn ceremony's per-flow challenge state
// (challenges are server-minted, single-use, time-bounded), the SPA
// doesn't need additional CSRF tokens.

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

    constructor(status: number, detail: string, code?: string) {
        super(detail);
        this.name = 'ApiError';
        this.status = status;
        this.code = code;
        this.detail = detail;
    }
}

interface RequestOptions {
    method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
    body?: unknown;
    signal?: AbortSignal;
}

/**
 * Generic typed request. T is the response shape. Callers should
 * supply T explicitly to keep the boundary tight.
 */
export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
    const init: RequestInit = {
        method: options.method ?? 'GET',
        credentials: 'include',
        headers: {
            Accept: 'application/json',
            ...(options.body !== undefined ? { 'Content-Type': 'application/json' } : {}),
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
): Promise<T> {
    const response = await fetch(path, {
        method: 'POST',
        credentials: 'include',
        headers: { Accept: 'application/json' },
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
    let detail = response.statusText || `HTTP ${response.status}`;
    try {
        const body = (await response.json()) as {
            detail?: string;
            title?: string;
            code?: string;
        };
        if (typeof body.detail === 'string' && body.detail.length > 0) {
            detail = body.detail;
        } else if (typeof body.title === 'string' && body.title.length > 0) {
            detail = body.title;
        }
        if (typeof body.code === 'string') {
            code = body.code;
        }
    } catch {
        // Body wasn't JSON; keep the status-text fallback.
    }
    return new ApiError(response.status, detail, code);
}

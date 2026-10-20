import { ApiError } from './api';

// Single source for turning an unknown thrown value into a user-facing string —
// was copy-pasted as `errorMessageFor` across 6+ files with divergent fallback
// wording. Prefer the ApiError.detail; otherwise any message; else the fallback.

export function errorMessage(error: unknown, fallback = 'Something went wrong.'): string {
    if (error instanceof ApiError) return error.detail;
    if (
        typeof error === 'object' &&
        error !== null &&
        'message' in error &&
        typeof (error as { message: unknown }).message === 'string' &&
        (error as { message: string }).message.length > 0
    ) {
        return (error as { message: string }).message;
    }
    return fallback;
}

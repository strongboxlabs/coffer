import type { RegisterEntry, SavedHeaderEntries } from '../types/register';
import { request } from './_request';

/**
 * A save scoped to an account answers 200 with every entry the saved header
 * has there, or 204 when it has none. Both come back as one list, so a caller
 * handles "the header left this register" as the empty case of the same swap.
 */
export async function requestSavedEntries(
    path: string,
    options: Parameters<typeof request>[1],
): Promise<RegisterEntry[]> {
    const saved = await request<SavedHeaderEntries | undefined>(path, options);
    return saved?.entries ?? [];
}

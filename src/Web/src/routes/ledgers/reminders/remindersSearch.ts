/** The Reminders page's search: `reminder` opens that series' editor. */
export interface RemindersSearch {
    reminder?: string;
}

/** The Reminders route's search, as the router keeps it: a non-empty id, or nothing. */
export function validateRemindersSearch(search: Record<string, unknown>): RemindersSearch {
    return typeof search.reminder === 'string' && search.reminder !== '' ? { reminder: search.reminder } : {};
}

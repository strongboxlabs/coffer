// The sidebar's width and folder open state (FU-051, FU-006). The collapsed
// flag is a plain persisted boolean, so it is usePersistedDisclosure's, in
// AuthedSidebar, like the pinned-reports disclosure beside it.
//
// PER BROWSER, in localStorage (ADR-0057 records the exception): a width depends on the screen it was chosen on, and a server
// value would arrive after the first paint, so a collapsed rail would draw
// expanded for a moment and then jump. The same reasoning as theme and density
// (lib/theme.ts), and the same try/catch as usePersistedDisclosure: localStorage
// throws rather than returning null in a private window and in some embedded
// webviews, and a rail preference must never take the page down with it.
//
// The width is applied before the first paint by public/theme-init.js, which
// sets WIDTH_VAR on the document element. That file is a hand-written copy of
// readStoredWidth + clampWidth, because it has to run before any module does;
// sidebarRail.test.ts holds the copy to this one. With nothing stored the
// variable is never set and the rail keeps its 224px token (ADR-0021 Rule 1),
// so a browser that never resized sees exactly the rail it always had.

export const WIDTH_STORAGE_KEY = 'coffer.sidebar.width';
const CLOSED_FOLDERS_KEY_PREFIX = 'coffer.sidebar.closedFolders.';

/** The CSS custom property the rail's width reads. */
export const WIDTH_VAR = '--coffer-sidebar-width';

/** `w-fixed-224px`, the rail's width before anyone drags it, and what a
 *  double-click on the handle goes back to. */
export const DEFAULT_WIDTH = 224;
export const MIN_WIDTH = 200;
export const MAX_WIDTH = 480;
/** The rail never takes more than this share of the window. */
export const MAX_VIEWPORT_SHARE = 0.4;
/** One arrow-key press on the resize handle. */
export const KEY_STEP = 16;

/** The widest the rail may be in a window this wide. Never below MIN_WIDTH. */
export function maxWidthFor(viewportWidth: number): number {
    return Math.max(MIN_WIDTH, Math.min(MAX_WIDTH, Math.floor(viewportWidth * MAX_VIEWPORT_SHARE)));
}

/** Clamp to [MIN_WIDTH, min(MAX_WIDTH, 40% of the window)], in whole pixels. */
export function clampWidth(px: number, viewportWidth: number): number {
    if (!Number.isFinite(px)) return DEFAULT_WIDTH;
    return Math.min(maxWidthFor(viewportWidth), Math.max(MIN_WIDTH, Math.round(px)));
}

export function viewportWidth(): number {
    return typeof window === 'undefined' ? 1920 : window.innerWidth;
}

/** The stored width, clamped to this window; null when none is stored. */
export function readStoredWidth(): number | null {
    try {
        const raw = window.localStorage.getItem(WIDTH_STORAGE_KEY);
        if (raw === null) return null;
        const px = Number.parseInt(raw, 10);
        return Number.isFinite(px) ? clampWidth(px, viewportWidth()) : null;
    } catch {
        return null;
    }
}

/**
 * Point the rail at a width, live; saving is separate. Null takes the
 * variable away, so the rail falls back to its token.
 */
export function applyWidth(px: number | null): void {
    const style = document.documentElement.style;
    if (px === null) style.removeProperty(WIDTH_VAR);
    else style.setProperty(WIDTH_VAR, `${px}px`);
}

/** Remember a width. The default is stored as nothing, so it follows the token. */
export function saveWidth(px: number): void {
    try {
        if (px === DEFAULT_WIDTH) window.localStorage.removeItem(WIDTH_STORAGE_KEY);
        else window.localStorage.setItem(WIDTH_STORAGE_KEY, String(px));
    } catch {
        // The width just will not survive a reload.
    }
}

/**
 * The folders closed in one ledger. Folders start expanded, so only the
 * closed ones are stored: a folder created elsewhere arrives open, and a
 * deleted one costs a stale id until the next toggle rewrites the list.
 */
export function readClosedFolders(ledgerId: string): ReadonlySet<string> {
    try {
        const raw = window.localStorage.getItem(CLOSED_FOLDERS_KEY_PREFIX + ledgerId);
        if (raw === null) return new Set();
        const parsed: unknown = JSON.parse(raw);
        return Array.isArray(parsed)
            ? new Set(parsed.filter((v): v is string => typeof v === 'string'))
            : new Set();
    } catch {
        return new Set();
    }
}

export function saveClosedFolders(ledgerId: string, closed: ReadonlySet<string>): void {
    try {
        const key = CLOSED_FOLDERS_KEY_PREFIX + ledgerId;
        if (closed.size === 0) window.localStorage.removeItem(key);
        else window.localStorage.setItem(key, JSON.stringify([...closed]));
    } catch {
        // As above.
    }
}

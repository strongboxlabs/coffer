// Settings tab registry (ADR-0037 / ADR-0069 nav swap). Lives in its own
// module — NOT SettingsPage.tsx — so router.ts can import the id list for the
// route's `validateSearch` without pulling a component into a non-component
// import (keeps the react-refresh "components-only file" boundary clean).

export const SETTINGS_TAB_IDS = [
    'general',
    'members',
    'snapshots',
    'feeds',
    'quotes',
    'activity',
    'dashboard',
] as const;

export type SettingsTab = (typeof SETTINGS_TAB_IDS)[number];

export const SETTINGS_TABS: ReadonlyArray<{ id: SettingsTab; label: string }> = [
    { id: 'general', label: 'General' },
    { id: 'members', label: 'Members' },
    { id: 'snapshots', label: 'Snapshots' },
    { id: 'feeds', label: 'Bank feeds' },
    { id: 'quotes', label: 'Quotes' },
    { id: 'activity', label: 'Activity' },
    { id: 'dashboard', label: 'Dashboard' },
];

/** Coerce an unknown (URL search) value to a valid tab; defaults to General. */
export function coerceSettingsTab(value: unknown): SettingsTab {
    return SETTINGS_TAB_IDS.includes(value as SettingsTab)
        ? (value as SettingsTab)
        : 'general';
}

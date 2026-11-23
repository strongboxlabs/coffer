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
    'rates',
    'notifications',
    'activity',
    'layouts',
] as const;

export type SettingsTab = (typeof SETTINGS_TAB_IDS)[number];

export const SETTINGS_TABS: ReadonlyArray<{ id: SettingsTab; label: string }> = [
    { id: 'general', label: 'General' },
    { id: 'members', label: 'Members' },
    { id: 'snapshots', label: 'Snapshots' },
    { id: 'feeds', label: 'Bank feeds' },
    { id: 'quotes', label: 'Quotes' },
    // After Quotes (ADR-0114 D10): the rates the ledger's totals convert at, which the
    // conversion note under a page header links to.
    { id: 'rates', label: 'Exchange rates' },
    // Before Activity: Activity is the log of what happened, this is where you
    // say who gets told about it.
    { id: 'notifications', label: 'Notifications' },
    { id: 'activity', label: 'Activity' },
    // No Dashboard tab: the Overview is customised in place (edit mode), and an old
    // ?tab=dashboard link falls through to General like any unknown tab.
    // After Bank feeds conceptually, but last in the strip: it is the
    // rarest visit of the lot — you come here to prune, not to set up.
    { id: 'layouts', label: 'Import layouts' },
];

/** Coerce an unknown (URL search) value to a valid tab; defaults to General. */
export function coerceSettingsTab(value: unknown): SettingsTab {
    return SETTINGS_TAB_IDS.includes(value as SettingsTab)
        ? (value as SettingsTab)
        : 'general';
}

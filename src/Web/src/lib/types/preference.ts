// Per-(user, ledger) preferences (ADR-0057). v1: the quotes namespace + the
// opt-in provider catalog the settings UI renders toggles from.

/** One opt-in external provider available to a ledger: the pull quote providers by
 *  display name, then the others. Yahoo, then `frankfurter` ("Frankfurter (exchange
 *  rates)", ADR-0114 D8), whose turning on starts a backfill of the ledger's rates. */
export interface QuoteProvider {
    key: string;
    displayName: string;
    /** The host its requests go to (ADR-0114 D4, D8): Yahoo's chart host, Frankfurter's
     *  from the install's configured base URL. The host alone; null when the configured
     *  URL is not usable. */
    host: string | null;
}

/** The `quotes` namespace value — external providers this ledger opted into. */
export interface QuotesPrefs {
    enabledProviders: string[];
}

/** A setting value as the API checks it: nothing richer than this is stored. */
export type DashboardSettingValue = boolean | number | string | null | string[];

/** One widget in a version-2 layout. Order within a column is order in the list. */
export interface DashboardWidgetPrefV2 {
    /** Equals `key` for a widget that appears once. */
    id: string;
    key: string;
    visible: boolean;
    column: string;
    settings: Record<string, DashboardSettingValue>;
}

/** The `dashboard` namespace value as the home screen writes it (version 2). */
export interface DashboardPrefsV2 {
    version: 2;
    columns: string;
    widgets: DashboardWidgetPrefV2[];
}

/** The layout an earlier release wrote: read-only input to the migration. */
export interface DashboardPrefsV1 {
    widgets: { key: string; visible: boolean }[];
}

/**
 * What GET returns: the stored document as stored, whatever its version (or an empty
 * one), plus the row's `updatedAt`, null when nothing is stored. The SPA's resolver
 * turns any of these into a layout; a save echoes `updatedAt`.
 */
export type StoredDashboardPrefs = (Partial<DashboardPrefsV2> | Partial<DashboardPrefsV1>) & {
    updatedAt: string | null;
};

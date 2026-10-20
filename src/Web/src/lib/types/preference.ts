// Per-(user, ledger) preferences (ADR-0057). v1: the quotes namespace + the
// opt-in provider catalog the settings UI renders toggles from.

/** One opt-in external quote provider available to a ledger. */
export interface QuoteProvider {
    key: string;
    displayName: string;
}

/** The `quotes` namespace value — external providers this ledger opted into. */
export interface QuotesPrefs {
    enabledProviders: string[];
}

/** One widget's placement in the Overview layout (order = position in the list). */
export interface DashboardWidgetPref {
    key: string;
    visible: boolean;
}

/** The `dashboard` namespace value — the per-ledger Overview layout. */
export interface DashboardPrefs {
    widgets: DashboardWidgetPref[];
}

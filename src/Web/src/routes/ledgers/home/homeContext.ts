import { createContext, useContext } from 'react';

import type { Column } from './dashboardCatalog';
import type { LayoutWidget, WidgetSettings } from './dashboardLayout';

/** What every widget on the home screen knows about the page it is on. */
export interface HomeContextValue {
    ledgerId: string;
    /** The user's local date, `YYYY-MM-DD`, read once per page render. */
    today: string;
    /** Owner or editor: may post, review, sync. Viewers get no write affordance. */
    canEdit: boolean;
    isOwner: boolean;
    /** The layout is being edited: links and act-in-place controls are inert. */
    editing: boolean;
    /** Done's save of the edited layout is in flight: what would change the draft
     *  is pending, since the save's success clears it. */
    layoutSaving: boolean;
    /** The stored layout loaded, so changing it cannot overwrite one unseen. */
    layoutWritable: boolean;
    /** This month's window (its `windowMonths`) in the layout drawn, whether or not
     *  it is shown: the Watchlist reads its marks over the same months, from the
     *  same request. */
    budgetWindowMonths: number;
    hide: (id: string) => void;
    /** Take a widget off the layout for good: a report tile whose report is gone. */
    remove: (id: string) => void;
    /** What a widget is called on the page: its catalog title, or a report tile's
     *  report's name. */
    titleOf: (widget: LayoutWidget) => string;
    /** The ref for a widget's actions button and for its edit frame's Hide: a
     *  widget hidden takes the control with it, and focus goes to the same
     *  control on the next widget drawn, the previous one, or (for the actions
     *  button) Customise and (for Hide) the hidden tray (useRowRemovalFocus). */
    actionsControl: (id: string) => (el: HTMLElement | null) => void;
    hideControl: (id: string) => (el: HTMLElement | null) => void;
    moveToOtherColumn: (id: string) => void;
    customise: () => void;
    /** Save one widget's settings: at once in view mode, into the draft in edit mode. */
    saveSettings: (id: string, settings: WidgetSettings) => void;
    /** The draft a widget's open settings panel holds, or null when it is closed.
     *  The page holds it, not the widget: a widget moved to the other column mounts
     *  afresh in that column's stack, and its panel stays open with what was chosen. */
    panelDraft: (id: string) => WidgetSettings | null;
    /** Open a widget's settings panel on the settings it renders with. */
    openSettings: (id: string) => void;
    /** Change an open panel's draft; a closed panel stays closed. */
    setPanelDraft: (id: string, settings: WidgetSettings) => void;
    /** Close a widget's settings panel, saving its draft when `apply`. A widget just
     *  added from the tray that needs a subject first (the Watchlist) lands with its
     *  settings open, and closing them any way but applying takes the add back. */
    closeSettings: (id: string, apply: boolean) => void;
}

export const HomeContext = createContext<HomeContextValue | null>(null);

export function useHome(): HomeContextValue {
    const value = useContext(HomeContext);
    if (!value) throw new Error('useHome outside the home screen');
    return value;
}

/** One widget's frame: its saved entry, the settings it renders with (a draft while
 *  its settings panel is open), and where it sits. */
export interface WidgetFrameValue {
    widget: LayoutWidget;
    settings: WidgetSettings;
    settingsOpen: boolean;
    openSettings: () => void;
    setDraft: (settings: WidgetSettings) => void;
    closeSettings: (apply: boolean) => void;
    position: { column: Column; index: number; count: number };
}

export const WidgetFrameContext = createContext<WidgetFrameValue | null>(null);

export function useWidgetFrame(): WidgetFrameValue {
    const value = useContext(WidgetFrameContext);
    if (!value) throw new Error('useWidgetFrame outside a widget');
    return value;
}

/** The DOM id of a widget's panel, so the header sentence can move focus to it. */
export const widgetDomId = (id: string) => `home-widget-${id}`;

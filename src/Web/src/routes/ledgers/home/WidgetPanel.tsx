import { Component, useRef, useState, type ReactNode, type RefObject } from 'react';

import { Button } from '@/components/ui/Button';
import { Checkbox } from '@/components/ui/Checkbox';
import { ContextMenu, type ContextMenuAnchor } from '@/components/ui/ContextMenu';
import { Panel, PanelHead } from '@/components/ui/Panel';
import { RowActionsButton } from '@/components/ui/RowActionsButton';
import { SegmentedControl } from '@/components/ui/SegmentedControl';
import { errorMessage } from '@/lib/errorMessage';

import { AccountSetting } from './AccountSetting';
import { CategoriesSetting } from './CategoriesSetting';
import { catalogEntry, hasSettings, type ChoiceOption, type SettingSpec } from './dashboardCatalog';
import { defaultSettings } from './dashboardLayout';
import { useHome, useWidgetFrame, widgetDomId } from './homeContext';

/**
 * The shell every home widget renders in: a panel with an `<h2>` title, a
 * right-aligned link to the page behind it, and a kebab (Settings…, Hide, Move to
 * other column, Customise layout…). The panel is a size container, so a widget lays
 * itself out compact below 320px of its own width, whatever the page width.
 */
export function WidgetPanel({ title, link, choices, children }: {
    title: ReactNode;
    link?: ReactNode;
    /** The options of a setting whose options are the widget's own data (Cash ahead's
     *  accounts), by setting name, as the widget has them now. */
    choices?: Readonly<Record<string, readonly ChoiceOption[]>>;
    children: ReactNode;
}) {
    const home = useHome();
    const frame = useWidgetFrame();
    const [menu, setMenu] = useState<ContextMenuAnchor | null>(null);
    const name = home.titleOf(frame.widget);

    const items = [
        ...(hasSettings(frame.widget.key)
            ? [{ id: 'settings', label: 'Settings…', onSelect: frame.openSettings, disabled: !home.layoutWritable }]
            : []),
        { id: 'hide', label: 'Hide', onSelect: () => home.hide(frame.widget.id), disabled: !home.layoutWritable },
        {
            id: 'move',
            label: 'Move to other column',
            onSelect: () => home.moveToOtherColumn(frame.widget.id),
            disabled: !home.layoutWritable,
        },
        { id: 'customise', label: 'Customise layout…', onSelect: home.customise, disabled: !home.layoutWritable },
    ];

    return (
        <Panel id={widgetDomId(frame.widget.id)} tabIndex={-1} className="@container/panel focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent">
            <PanelHead className="gap-3" data-widget-head={frame.widget.id}>
                <h2 className="min-w-0 truncate text-sm font-semibold">{title}</h2>
                <div className="flex shrink-0 items-center gap-2">
                    {link}
                    {home.editing ? null : (
                        <RowActionsButton ref={home.actionsControl(frame.widget.id)} label={`${name} actions`} onOpen={setMenu} />
                    )}
                </div>
            </PanelHead>
            {menu ? <ContextMenu anchor={menu} items={items} onClose={() => setMenu(null)} /> : null}
            {frame.settingsOpen ? <SettingsPanel choices={choices ?? {}} /> : null}
            {children}
        </Panel>
    );
}

/**
 * The inline settings panel under the head. Every control changes the widget below
 * at once, so no setting is invisible; Save (or Apply, while the layout is being
 * edited) keeps it, Cancel puts it back. Escape is its Cancel and goes no further,
 * so it never reaches the page's own Escape, which would discard the whole edit.
 * The page moves focus in when it opens, and takes it when it closes.
 */
function SettingsPanel({ choices }: { choices: Readonly<Record<string, readonly ChoiceOption[]>> }) {
    const home = useHome();
    const frame = useWidgetFrame();
    const panelRef = useRef<HTMLDivElement | null>(null);
    const entry = catalogEntry(frame.widget.key)!;
    const set = (name: string, value: unknown) =>
        frame.setDraft({ ...frame.settings, [name]: value as never });
    // A widget whose subject is a list of categories shows nothing without one, so
    // there is nothing to keep until one is chosen.
    const incomplete = Object.entries(entry.settings).some(([name, spec]) =>
        spec.kind === 'categories' && !(Array.isArray(frame.settings[name]) && (frame.settings[name] as string[]).length > 0));

    return (
        <div
            ref={panelRef}
            tabIndex={-1}
            data-widget-settings={frame.widget.id}
            className="space-y-3 border-b border-border bg-surface-header px-4 py-3 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent"
            role="group"
            aria-label={`${home.titleOf(frame.widget)} settings`}
            onKeyDown={(e) => {
                // A control inside that used the key (a picker closing its list) keeps it.
                if (e.key !== 'Escape' || e.defaultPrevented) return;
                e.preventDefault();
                frame.closeSettings(false);
            }}
        >
            {Object.entries(entry.settings).map(([name, spec]) => (
                <SettingControl key={name} spec={spec} value={frame.settings[name]} onChange={(v) => set(name, v)} panel={panelRef}
                    choices={choices[name] ?? []} />
            ))}
            <div className="flex flex-wrap items-center justify-between gap-2 pt-1">
                <Button variant="link" size="xs" onClick={() => frame.setDraft(defaultSettings(frame.widget.key))}>
                    Reset this widget
                </Button>
                <div className="flex items-center gap-2">
                    <Button variant="ghost" size="sm" onClick={() => frame.closeSettings(false)}>Cancel</Button>
                    {/* While Done saves the layout, Apply is pending: the save's success
                        clears the draft it would write into (ADR-0023 §L). */}
                    <Button size="sm" onClick={() => frame.closeSettings(true)} disabled={incomplete}
                        pending={home.editing ? home.layoutSaving : undefined}>
                        {home.editing ? 'Apply' : 'Save'}
                    </Button>
                </div>
            </div>
        </div>
    );
}

function SettingControl({ spec, value, onChange, panel, choices }: {
    spec: SettingSpec;
    value: unknown;
    onChange: (value: unknown) => void;
    /** The settings panel, where focus goes when a control in it takes itself away. */
    panel: RefObject<HTMLElement | null>;
    /** The widget's own options, for a setting whose options are its data. */
    choices: readonly ChoiceOption[];
}) {
    if (spec.kind === 'boolean') {
        return <Checkbox label={spec.label} checked={value === true} onChange={(e) => onChange(e.target.checked)} />;
    }
    if (spec.kind === 'categories') {
        return <CategoriesSetting label={spec.label} max={spec.max} value={value} onChange={onChange} panel={panel} />;
    }
    if (spec.kind === 'account') {
        return <AccountSetting label={spec.label} noneLabel={spec.noneLabel} options={choices} value={value} onChange={onChange} />;
    }
    if (spec.kind === 'set') {
        const chosen = Array.isArray(value) ? (value as string[]) : [];
        return (
            <fieldset className="space-y-1">
                <legend className="mb-1 text-xs font-medium text-text-muted">{spec.label}</legend>
                {spec.options.map((o) => {
                    const v = String(o.value);
                    const on = chosen.includes(v);
                    return (
                        <Checkbox
                            key={v}
                            label={o.label}
                            checked={on}
                            // The last one stays: a set showing nothing has no visible effect.
                            disabled={on && chosen.length === 1}
                            onChange={(e) => onChange(e.target.checked ? [...chosen, v] : chosen.filter((c) => c !== v))}
                        />
                    );
                })}
            </fieldset>
        );
    }
    // A choice: the control works in strings; the stored value keeps its type.
    const options = spec.options.map((o) => ({ value: String(o.value), label: o.label }));
    return (
        <div className="flex flex-wrap items-center gap-2">
            <span className="text-xs font-medium text-text-muted">{spec.label}</span>
            <SegmentedControl
                label={spec.label}
                size="md"
                options={options}
                value={String(value)}
                onChange={(v) => onChange(spec.options.find((o) => String(o.value) === v)?.value)}
            />
        </div>
    );
}

/**
 * The ADR-0023 error band for a widget whose data failed: what failed, the detail,
 * and Retry. Never the widget's empty copy, which would say "nothing" where the
 * truth is "could not check".
 */
export function WidgetError({ what, error, onRetry }: {
    /** "cash flow", "bills". */
    what: string;
    error: unknown;
    onRetry: () => void;
}) {
    return (
        <div role="alert" className="m-4 flex flex-wrap items-center justify-between gap-2 border border-state-danger/40 bg-state-danger-soft px-3 py-2 text-sm text-state-danger">
            <span>
                Couldn&apos;t load {what}. <span className="text-text-muted">{errorMessage(error, '')}</span>
            </span>
            <Button variant="link" size="xs" onClick={onRetry}>Retry</Button>
        </div>
    );
}

/**
 * Catches a widget that throws while rendering, so one broken widget leaves the rest
 * of the home screen standing.
 */
export class WidgetBoundary extends Component<{ title: string; children: ReactNode }, { error: unknown }> {
    override state: { error: unknown } = { error: null };

    static getDerivedStateFromError(error: unknown) {
        return { error };
    }

    override render() {
        if (this.state.error === null) return this.props.children;
        return (
            <Panel className="@container/panel">
                <PanelHead><h2 className="text-sm font-semibold">{this.props.title}</h2></PanelHead>
                <WidgetError what={this.props.title.toLowerCase()} error={this.state.error} onRetry={() => this.setState({ error: null })} />
            </Panel>
        );
    }
}

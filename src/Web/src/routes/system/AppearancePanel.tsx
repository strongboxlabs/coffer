import { useState } from 'react';

import { Button } from '@/components/ui/Button';
import { Panel, PanelBody } from '@/components/ui/Panel';
import {
    ACCENTS, readStoredAccent, readStoredTheme, setAccent, setTheme, THEMES,
    type AccentId, type ThemeId,
} from '@/lib/theme';

// Appearance panel — ADR-0021 Rule 4 (revised). Two independent axes: the
// THEME (ground lightness and contrast level) and the COLOUR DIRECTION (one hue
// regenerating both the accent family and the neutral ramp).
//
// Not admin-gated, unlike every other tab on this page: appearance is a
// personal display preference, stored per device in localStorage rather than
// against the account (see lib/theme.ts for why).
//
// STAGED, not live. Selecting used to apply immediately, which repainted the
// whole app on every click — clicking down a list of four themes strobed it.
// Selection moves a local draft; Apply commits. The previews are what make that
// bearable: each one carries BOTH `data-theme` and `data-accent` on a single
// element, which re-scopes every `--color-*` for its subtree, so you see the
// real pairing without wearing it.
//
// Both attributes must sit on the SAME element — the stylesheet's selectors are
// compound (`[data-theme="dark"][data-accent="rust"]`). Splitting them across a
// parent and child silently matches nothing, which rendered all three colour
// previews identical.

export function AppearancePanel() {
    const [appliedTheme, setAppliedTheme] = useState<ThemeId>(() => readStoredTheme());
    const [appliedAccent, setAppliedAccent] = useState<AccentId>(() => readStoredAccent());
    const [draftTheme, setDraftTheme] = useState<ThemeId>(appliedTheme);
    const [draftAccent, setDraftAccent] = useState<AccentId>(appliedAccent);

    const dirty = draftTheme !== appliedTheme || draftAccent !== appliedAccent;

    function apply() {
        setTheme(draftTheme);
        setAccent(draftAccent);
        setAppliedTheme(draftTheme);
        setAppliedAccent(draftAccent);
    }

    function cancel() {
        setDraftTheme(appliedTheme);
        setDraftAccent(appliedAccent);
    }

    return (
        <section className="space-y-3">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">Appearance</h2>
                <p className="text-sm text-text-muted">
                    Applies to this browser only — it is not stored against your
                    account, so each device can differ.
                </p>
            </header>

            <Panel>
                <PanelBody>
                    <fieldset className="flex flex-col gap-2">
                        <legend className="mb-2 text-sm font-semibold">Theme</legend>
                        {THEMES.map((t) => (
                            <Option
                                key={t.id}
                                name="theme"
                                id={t.id}
                                label={t.label}
                                hint={t.hint}
                                checked={t.id === draftTheme}
                                onSelect={() => setDraftTheme(t.id)}
                                theme={t.id}
                                accent={draftAccent}
                            />
                        ))}
                    </fieldset>
                </PanelBody>
            </Panel>

            <Panel>
                <PanelBody>
                    <fieldset className="flex flex-col gap-2">
                        <legend className="mb-2 text-sm font-semibold">Colour</legend>
                        <p className="-mt-1 mb-1 text-sm text-text-muted">
                            Each choice regenerates the accent and the neutral ground
                            together, so it is a different palette rather than a tint
                            over the same one. Shown in the theme selected above,
                            because the same hue is a different colour on a light
                            ground than on a dark one.
                        </p>
                        {ACCENTS.map((a) => (
                            <Option
                                key={a.id}
                                name="accent"
                                id={a.id}
                                label={a.label}
                                hint={a.hint}
                                checked={a.id === draftAccent}
                                onSelect={() => setDraftAccent(a.id)}
                                theme={draftTheme}
                                accent={a.id}
                            />
                        ))}
                    </fieldset>
                </PanelBody>
            </Panel>

            {/* Matches the app's action footer everywhere else: right-aligned,
                Cancel (secondary) then Save (primary) — see LayoutNameDialog,
                AccountEditorDialog, the split-posting editor. */}
            <div className="flex items-center justify-end gap-2">
                <span className="mr-auto text-xs text-text-muted">
                    {dirty
                        ? 'Not saved yet — the previews show what you would get.'
                        : 'Up to date.'}
                </span>
                <Button type="button" variant="secondary" size="sm"
                    onClick={cancel} disabled={!dirty}>
                    Cancel
                </Button>
                <Button type="button" variant="primary" size="sm"
                    onClick={apply} disabled={!dirty}>
                    Save
                </Button>
            </div>
        </section>
    );
}

interface OptionProps {
    name: string;
    id: string;
    label: string;
    hint: string;
    checked: boolean;
    onSelect: () => void;
    theme: ThemeId;
    accent: AccentId;
}

function Option(props: OptionProps) {
    const { name, id, label, hint, checked, onSelect, theme, accent } = props;
    return (
        <label
            className={
                'flex cursor-pointer items-center gap-3 border p-2 '
                + (checked ? 'border-accent bg-accent-soft/40' : 'border-border hover:bg-surface-hover')
            }
        >
            <input
                type="radio"
                name={name}
                value={id}
                checked={checked}
                onChange={onSelect}
                className="accent-accent"
                // Without these the accessible name is the whole label —
                // "LightThe default." — which is what a screen reader reads out.
                aria-labelledby={`${name}-${id}-label`}
                aria-describedby={`${name}-${id}-hint`}
            />
            <span className="flex-1">
                <span id={`${name}-${id}-label`} className="block text-sm font-medium">
                    {label}
                </span>
                <span id={`${name}-${id}-hint`} className="block text-xs text-text-muted">
                    {hint}
                </span>
            </span>
            <Preview theme={theme} accent={accent} />
        </label>
    );
}

/** A miniature register in `theme` x `accent`. Both attributes on one element:
 *  the stylesheet's selectors are compound and match nothing if they are split
 *  across a parent and a child. */
function Preview({ theme, accent }: { theme: ThemeId; accent: AccentId }) {
    return (
        <span
            data-theme={theme}
            data-accent={accent}
            aria-hidden="true"
            className="block w-52 shrink-0 border border-border bg-surface"
        >
            <span className="flex justify-between border-b border-border bg-surface-header px-1.5 py-0.5 text-[0.5625rem] font-semibold uppercase tracking-wider text-text-muted">
                <span>Payee</span>
                <span>Amount</span>
            </span>
            <span className="flex items-center justify-between px-1.5 py-1 text-[0.625rem] text-text">
                <span className="flex items-center gap-1">
                    {/* the accent badge — the preview used to contain no accent
                        token at all, which made every colour look identical */}
                    <span className="inline-flex h-3 w-3 items-center justify-center rounded-full bg-accent-soft text-[0.5rem] font-bold text-accent-soft-text">
                        ✓
                    </span>
                    Ridgeline Market
                </span>
                <span className="font-mono tabular-nums text-state-danger">-84.21</span>
            </span>
            <span className="flex items-center gap-1 border-t border-border px-1.5 py-1">
                <span className="rounded bg-cat-groc-soft px-1 py-px text-[0.5625rem] font-medium text-cat-groc-text">
                    Groceries
                </span>
                <span className="rounded bg-chip-neutral px-1 py-px text-[0.5625rem] font-medium text-chip-neutral-text">
                    tag
                </span>
                <span className="ml-auto rounded bg-accent px-1.5 py-px text-[0.5625rem] font-medium text-text-inverse">
                    Save
                </span>
            </span>
        </span>
    );
}

import { useState } from 'react';

import { ActionFooter } from '@/components/ui/ActionFooter';
import { Panel, PanelBody } from '@/components/ui/Panel';
import {
    ACCENTS, DENSITIES, readStoredAccent, readStoredDensity, readStoredTheme,
    setAccent, setDensity, setTheme, THEMES,
    type AccentId, type DensityId, type ThemeId,
} from '@/lib/theme';

// Appearance panel — ADR-0021 Rule 4 (revised) and Rule 11. Three independent
// axes: the THEME (ground lightness and contrast level), the COLOUR DIRECTION
// (one hue regenerating both the accent family and the neutral ramp), and
// DENSITY (one `--spacing` token, which Tailwind multiplies into every padding,
// margin and gap in the app).
//
// Not admin-gated, unlike every other tab on this page: appearance is a
// personal display preference, stored per device in localStorage rather than
// against the account (see lib/theme.ts for why).
//
// STAGED, not live. Selecting used to apply immediately, which repainted the
// whole app on every click — clicking down a list of four themes strobed it.
// Selection moves a local draft; Save commits.
//
// ONE PREVIEW, ONE VARIABLE. Each card shows its own option against the
// APPLIED values of the other two axes, never the drafts. That looks like a
// small detail and is the whole reason the panel reads as staged.
//
// It did not start that way: every preview followed every draft, so one
// density click repainted 7 of the 10 cards and one theme click darkened 6 of
// them. Nothing was ever saved — the root attributes were correct throughout —
// but a panel where most of the surface changes the instant you click is
// indistinguishable from one that applied your choice, and it was reported as
// exactly that. "Staged" is a claim about what the user sees, not about where
// the state lives.
//
// The cost, stated plainly: stage a theme AND a colour together and the colour
// cards still show the old theme until you save. That is the honest reading —
// they show what you currently have — and it is worth more than the churn.
//
// All three attributes must sit on the SAME element — the stylesheet's colour
// selectors are compound (`[data-theme="dark"][data-accent="rust"]`).
// Splitting them across a parent and child silently matches nothing, which
// rendered all three colour previews identical.

export function AppearancePanel() {
    const [appliedTheme, setAppliedTheme] = useState<ThemeId>(() => readStoredTheme());
    const [appliedAccent, setAppliedAccent] = useState<AccentId>(() => readStoredAccent());
    const [appliedDensity, setAppliedDensity] = useState<DensityId>(() => readStoredDensity());
    const [draftTheme, setDraftTheme] = useState<ThemeId>(appliedTheme);
    const [draftAccent, setDraftAccent] = useState<AccentId>(appliedAccent);
    const [draftDensity, setDraftDensity] = useState<DensityId>(appliedDensity);

    const dirty =
        draftTheme !== appliedTheme
        || draftAccent !== appliedAccent
        || draftDensity !== appliedDensity;

    function apply() {
        setTheme(draftTheme);
        setAccent(draftAccent);
        setDensity(draftDensity);
        setAppliedTheme(draftTheme);
        setAppliedAccent(draftAccent);
        setAppliedDensity(draftDensity);
    }

    function cancel() {
        setDraftTheme(appliedTheme);
        setDraftAccent(appliedAccent);
        setDraftDensity(appliedDensity);
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
                                accent={appliedAccent}
                                density={appliedDensity}
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
                            over the same one. Shown in your current theme — if you
                            are also changing the theme above, save first and these
                            will redraw against it.
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
                                theme={appliedTheme}
                                accent={a.id}
                                density={appliedDensity}
                            />
                        ))}
                    </fieldset>
                </PanelBody>
            </Panel>

            <Panel>
                <PanelBody>
                    <fieldset className="flex flex-col gap-2">
                        <legend className="mb-2 text-sm font-semibold">Density</legend>
                        <p className="-mt-1 mb-1 text-sm text-text-muted">
                            Changes the space between things — padding, gaps and row
                            height. Text, icons and controls stay the same size, so
                            nothing gets harder to read or to hit.
                        </p>
                        {DENSITIES.map((d) => (
                            <Option
                                key={d.id}
                                name="density"
                                id={d.id}
                                label={d.label}
                                hint={d.hint}
                                checked={d.id === draftDensity}
                                onSelect={() => setDraftDensity(d.id)}
                                theme={appliedTheme}
                                accent={appliedAccent}
                                density={d.id}
                                previewRows={6}
                            />
                        ))}
                    </fieldset>
                </PanelBody>
            </Panel>

            <ActionFooter
                note={dirty
                    ? 'Not saved yet — the previews show what you would get.'
                    : 'Up to date.'}
                cancel={{ label: 'Cancel', onClick: cancel, disabled: !dirty }}
                confirm={{ label: 'Save', onClick: apply, disabled: !dirty }}
            />
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
    density: DensityId;
    /** Rows in this option's preview. Density needs a tall sample to show
     *  anything; colour and theme read fine from one row. */
    previewRows?: number;
}

function Option(props: OptionProps) {
    const { name, id, label, hint, checked, onSelect, theme, accent, density, previewRows } = props;
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
            <Preview theme={theme} accent={accent} density={density} rows={previewRows} />
        </label>
    );
}

/**
 * A miniature register in `theme` x `accent` x `density`.
 *
 * All three attributes on ONE element: the stylesheet's colour selectors are
 * compound (`[data-theme="dark"][data-accent="rust"]`) and match nothing if
 * they are split across a parent and a child — that is what once rendered all
 * three colour previews identical.
 *
 * `rows` exists because density is CUMULATIVE and a small sample hides it. The
 * card carries a fixed number of spacing steps of vertical padding, so at one
 * transaction row the three densities were 16 / 20 / 24px tall — a 4px spread
 * next to a two-line label, which read as three identical cards. Six rows puts
 * the spread at 48 / 60 / 72px, which is what the setting actually does to a
 * register you scroll. The frame's WIDTH is pinned either way, so what moves is
 * the row rhythm rather than the box.
 */
function Preview(
    { theme, accent, density, rows = 1 }: {
        theme: ThemeId;
        accent: AccentId;
        density: DensityId;
        rows?: number;
    },
) {
    return (
        <span
            data-theme={theme}
            data-accent={accent}
            data-density={density}
            aria-hidden="true"
            className="block w-fixed-208px shrink-0 border border-border bg-surface"
        >
            <span className="flex justify-between border-b border-border bg-surface-header px-1.5 py-0.5 text-[0.5625rem] font-semibold uppercase tracking-wider text-text-muted">
                <span>Payee</span>
                <span>Amount</span>
            </span>
            {SAMPLE_ROWS.slice(0, rows).map((row, i) => (
                <span
                    key={row.payee}
                    className={
                        'flex items-center justify-between px-1.5 py-1 text-[0.625rem] text-text'
                        + (i > 0 ? ' border-t border-border' : '')
                    }
                >
                    <span className="flex items-center gap-1">
                        {/* the accent badge — the preview used to contain no accent
                            token at all, which made every colour look identical */}
                        <span className="inline-flex size-icon-xs items-center justify-center rounded-full bg-accent-soft text-[0.5rem] font-bold text-accent-soft-text">
                            ✓
                        </span>
                        {row.payee}
                    </span>
                    <span className="font-mono tabular-nums text-state-danger">{row.amount}</span>
                </span>
            ))}
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

/** Distinct payees so a multi-row preview reads as a register rather than as
 *  one row repeated. Amounts are synthetic. */
const SAMPLE_ROWS = [
    { payee: 'Ridgeline Market', amount: '-84.21' },
    { payee: 'Corner Hardware', amount: '-37.60' },
    { payee: 'Alder & Finch', amount: '-19.08' },
    { payee: 'Transit Pass', amount: '-52.00' },
    { payee: 'Payroll', amount: '2,140.00' },
    { payee: 'Westbrook Utilities', amount: '-96.34' },
];

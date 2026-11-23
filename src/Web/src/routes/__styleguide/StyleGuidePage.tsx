import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { AttentionCount, AttentionDot } from '@/components/AttentionDot';
import { TagLine } from '@/components/tags/TagLine';
import { TagSelect } from '@/components/tags/TagSelect';
import { TagColorsValueProvider } from '@/components/tags/TagColorsContext';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Label } from '@/components/ui/Label';
import { SegmentedControl } from '@/components/ui/SegmentedControl';
import { MarkBar } from '@/components/ui/MarkBar';
import { InsertionLine } from '@/components/ui/InsertionLine';
import { GripIcon } from '@/components/ui/GripIcon';
import { Skeleton } from '@/components/ui/Skeleton';
import { StatusBadge } from '@/components/ui/StatusBadge';
import { ChartFrame, VIEWBOX } from '@/components/chart/ChartFrame';
import { ConversionNote, MissingRateNote } from '@/components/ConversionNote';
import { RATES_LINK_CLASS } from '@/lib/conversion';
import type { RateUnavailable } from '@/lib/types';
import { Money, NoRate } from '@/routes/ledgers/home/Money';
import { PointSelect } from '@/components/chart/PointSelect';
import { Bar } from '@/components/chart/Series';
import { MemoTagsSamples } from './MemoTagsSamples';
import { EditorDialogSamples } from './EditorDialogSamples';
import { CurrencySamples } from './CurrencySamples';
import {
    MainArea,
    MainPane,
    Sidebar,
    SidebarFooter,
    SidebarHeader,
    SidebarLayout,
    SidebarNav,
    SidebarNavLink,
    SidebarPicker,
    SidebarSection,
    TopBar,
} from '@/components/ui/SidebarLayout';

// Style guide — ADR-0021 D.1. Dev-only route (gated in router.ts on
// `import.meta.env.DEV` so the production bundle never ships it).
// Every token, primitive, and palette swatch declared in
// src/index.css renders here so the visual system is auditable in
// one place. When a token is added or changed, this page is the
// first place to verify.

const surfaceTokens = [
    { name: '--color-surface', cls: 'bg-surface', text: 'text-text' },
    { name: '--color-surface-muted', cls: 'bg-surface-muted', text: 'text-text' },
    { name: '--color-surface-sidebar', cls: 'bg-surface-sidebar', text: 'text-text' },
    { name: '--color-surface-hover', cls: 'bg-surface-hover', text: 'text-text' },
    { name: '--color-surface-header', cls: 'bg-surface-header', text: 'text-text-muted' },
    { name: '--color-chip-neutral', cls: 'bg-chip-neutral', text: 'text-chip-neutral-text' },
];

const borderTokens = [
    { name: '--color-border', cls: 'border-border' },
    { name: '--color-border-strong', cls: 'border-border-strong' },
];

const textTokens = [
    { name: '--color-text', cls: 'text-text' },
    { name: '--color-text-muted', cls: 'text-text-muted' },
    { name: '--color-text-subtle', cls: 'text-text-subtle' },
    { name: '--color-text-inverse', cls: 'text-text-inverse', bg: 'bg-accent' },
];

const accentTokens = [
    { name: '--color-accent', cls: 'bg-accent', text: 'text-text-inverse' },
    { name: '--color-accent-hover', cls: 'bg-accent-hover', text: 'text-text-inverse' },
    { name: '--color-accent-soft', cls: 'bg-accent-soft', text: 'text-accent-soft-text' },
];

// What each state colour is used for. Green is a status only: something that
// succeeded (a job, an import, a restore, a check that came back healthy), never an
// amount (ADR-0023 §H), and never Cleared, which StatusBadge draws in the accent.
const stateTokens = [
    { name: 'success', bg: 'bg-state-success-soft', text: 'text-state-success', label: '✓ Succeeded' },
    { name: 'warning', bg: 'bg-state-warning-soft', text: 'text-state-warning', label: 'P Pending' },
    { name: 'danger', bg: 'bg-state-danger-soft', text: 'text-state-danger', label: '! Failed' },
];

const categoryTokens: Array<{ slug: string; label: string; bg: string; text: string }> = [
    { slug: 'groc', label: 'Groceries', bg: 'bg-cat-groc-soft', text: 'text-cat-groc-text' },
    { slug: 'din', label: 'Dining', bg: 'bg-cat-din-soft', text: 'text-cat-din-text' },
    { slug: 'house', label: 'Housing', bg: 'bg-cat-house-soft', text: 'text-cat-house-text' },
    { slug: 'util', label: 'Utilities', bg: 'bg-cat-util-soft', text: 'text-cat-util-text' },
    { slug: 'sub', label: 'Subscriptions', bg: 'bg-cat-sub-soft', text: 'text-cat-sub-text' },
    { slug: 'tran', label: 'Transport', bg: 'bg-cat-tran-soft', text: 'text-cat-tran-text' },
    { slug: 'sal', label: 'Salary', bg: 'bg-cat-sal-soft', text: 'text-cat-sal-text' },
    { slug: 'xfer', label: 'Transfer', bg: 'bg-cat-xfer-soft', text: 'text-cat-xfer-text' },
    { slug: 'phone', label: 'Phone', bg: 'bg-cat-phone-soft', text: 'text-cat-phone-text' },
    { slug: 'rec', label: 'Recreation', bg: 'bg-cat-rec-soft', text: 'text-cat-rec-text' },
];

// A report chart's fill per hue family, side by side, which is how they are
// read: a stack of neighbours. Other is the slate the families leave out.
const chartShadeTokens = [
    { name: '--color-cat-groc-chart', cls: 'bg-cat-groc-chart' },
    { name: '--color-cat-din-chart', cls: 'bg-cat-din-chart' },
    { name: '--color-cat-house-chart', cls: 'bg-cat-house-chart' },
    { name: '--color-cat-util-chart', cls: 'bg-cat-util-chart' },
    { name: '--color-cat-sub-chart', cls: 'bg-cat-sub-chart' },
    { name: '--color-cat-tran-chart', cls: 'bg-cat-tran-chart' },
    { name: '--color-cat-sal-chart', cls: 'bg-cat-sal-chart' },
    { name: '--color-cat-phone-chart', cls: 'bg-cat-phone-chart' },
    { name: '--color-cat-rec-chart', cls: 'bg-cat-rec-chart' },
    { name: '--color-cat-xfer-text (Other)', cls: 'bg-cat-xfer-text' },
];

/** A sample missing rate: synthetic currencies and day. */
const SAMPLE_MISSING: RateUnavailable[] = [
    { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-11-10', reason: 'rate_unavailable' },
];

/**
 * Converted totals (ADR-0114 D7, D9): one ledger-level note under a page header, a
 * converted figure printed plainly, and a figure with a missing rate marked on itself.
 * The link is drawn outside a router here; on a screen it is ExchangeRatesLink.
 */
function ConvertedSamples() {
    const link = <a href="#converted" className={RATES_LINK_CLASS}>Exchange rates →</a>;
    return (
        <div id="converted" className="max-w-md space-y-3 text-sm">
            <ConversionNote reportingCurrency="USD" currencies={['USD', 'CAD']} ratesLink={link} />
            <dl className="space-y-0.5">
                <div className="flex justify-between gap-2">
                    <dt className="text-text-muted">Converted</dt>
                    <dd><Money amount={245_400} currency="USD" tone="fixed" /></dd>
                </div>
                <div className="flex justify-between gap-2">
                    <dt className="text-text-muted">Missing rate</dt>
                    <dd><Money amount={null} currency="USD" tone="fixed" rateUnavailable={SAMPLE_MISSING} /></dd>
                </div>
                <div className="flex justify-between gap-2">
                    <dt className="text-text-muted">Marker alone</dt>
                    <dd><NoRate missing={SAMPLE_MISSING} className="text-xl" /></dd>
                </div>
            </dl>
            <MissingRateNote missing={SAMPLE_MISSING} ratesLink={link}>Rates are fetched through Nov 6.</MissingRateNote>
        </div>
    );
}

function Section({ id, title, children }: { id?: string; title: string; children: React.ReactNode }) {
    return (
        <section id={id} className="space-y-3 border-b border-border pb-6 last:border-b-0">
            <h2 className="text-sm font-semibold uppercase tracking-wider text-text-muted">
                {title}
            </h2>
            {children}
        </section>
    );
}

/** A small palette for the tag-line demo; the provider keys on lower-cased name. */
const STYLEGUIDE_TAG_DTOS = [
    { id: 'g1', name: 'Project A', color: '#2563eb', usageCount: 12, transactionCount: 12 },
    { id: 'g2', name: 'Project B', color: '#16a34a', usageCount: 8, transactionCount: 8 },
    { id: 'g3', name: 'Project C', color: '#d97706', usageCount: 5, transactionCount: 5 },
    { id: 'g4', name: 'Project D', color: '#9333ea', usageCount: 3, transactionCount: 3 },
    { id: 'g5', name: 'Tax Deductible 2024', color: '#dc2626', usageCount: 21, transactionCount: 21 },
    { id: 'g6', name: 'Kitchen Remodel Phase 2 Contractor Payments 2024', color: '#0891b2', usageCount: 2, transactionCount: 2 },
    { id: 'g7', name: 'Phase 1 / Phase 2', color: '#64748b', usageCount: 1, transactionCount: 1 },
];

const STYLEGUIDE_TAGS = [
    { name: 'Project A', color: '#2563eb' },
    { name: 'Project B', color: '#16a34a' },
    { name: 'Project C', color: '#d97706' },
    { name: 'Project D', color: '#9333ea' },
    { name: 'Tax Deductible 2024', color: '#dc2626' },
    { name: 'Kitchen Remodel Phase 2 Contractor Payments 2024', color: '#0891b2' },
    { name: 'Phase 1 / Phase 2', color: '#64748b' },
    { name: 'uncoloured tag', color: null },
];

/**
 * A stateful wrapper for the gallery — {@link TagSelect} is controlled, so
 * something has to hold the value for it to be clickable here.
 *
 * `autoOpen` clicks its own edit button once, after the page has settled. That
 * exists so the OPEN panel is visible in a static capture of this page; it is
 * gallery scaffolding and deliberately not a prop on the component, which would
 * be production code serving a screenshot.
 *
 * "Settled" is the web fonts loaded and two frames drawn after that. The panel
 * is fixed to the viewport and placed once, against the layout at the moment it
 * opens, so a panel opened before the fonts reflow the page stays where its
 * control used to be; and a scroll still pending from the page's own layout
 * would reach the panel's close-on-scroll listener. Both depend on timing, so a
 * panel opened on mount looked different from one load to the next.
 */
/** SegmentedControl in both sizes, each holding its own choice. */
/** Bars with the last one hatched (a figure still in progress), and the focusable
 *  overlay that selects one by keyboard or touch. */
function ChartSample() {
    const values = [40, 55, 48, 62, 30];
    const [selected, setSelected] = useState<number | null>(3);
    const step = VIEWBOX.width / values.length;
    const scale = (v: number) => VIEWBOX.height - (v / 70) * VIEWBOX.height;
    return (
        <div className="max-w-md space-y-1">
            <p className="text-xs text-text-muted">Selected: {selected === null ? 'none' : `point ${selected + 1}, ${values[selected]}`}</p>
            <ChartFrame
                heightClass="h-fixed-128px"
                ariaLabel="Five sample values, the last still in progress"
                overlay={<PointSelect label="Sample points" labels={values.map((v, i) => `Point ${i + 1}: ${v}`)} selected={selected} onSelect={setSelected} />}
            >
                {values.map((v, i) => (
                    <Bar key={i} x={step * i + step * 0.2} width={step * 0.6} baselineY={VIEWBOX.height} valueY={scale(v)}
                        className="fill-chip-neutral-text/60" hatched={i === values.length - 1} />
                ))}
            </ChartFrame>
        </div>
    );
}

function SegmentedDemo() {
    const [view, setView] = useState<'table' | 'chart'>('table');
    const [columns, setColumns] = useState<'wide-narrow' | 'equal' | 'single'>('wide-narrow');
    return (
        <div className="flex flex-wrap items-center gap-6">
            <SegmentedControl
                label="View"
                options={[{ value: 'table', label: 'Table' }, { value: 'chart', label: 'Chart' }]}
                value={view}
                onChange={setView}
            />
            <SegmentedControl
                label="Columns"
                size="md"
                options={[
                    { value: 'wide-narrow', label: 'Wide + narrow' },
                    { value: 'equal', label: 'Equal' },
                    { value: 'single', label: 'Single' },
                ]}
                value={columns}
                onChange={setColumns}
            />
        </div>
    );
}

function TagSelectDemo({
    initial,
    compact,
    autoOpen,
    label,
}: {
    initial: readonly string[];
    compact?: boolean;
    autoOpen?: boolean;
    label: string;
}) {
    const [tags, setTags] = useState<readonly string[]>(initial);
    const ref = useRef<HTMLDivElement>(null);
    useEffect(() => {
        if (!autoOpen) return;
        // Cancelled on cleanup, so StrictMode's double-invoked effect schedules
        // one click, not two; and only a closed control is clicked, because two
        // clicks on a toggle is zero clicks and a capture would show it closed.
        let live = true;
        let frame = 0;
        const fontsReady = 'fonts' in document ? document.fonts.ready : Promise.resolve();
        void fontsReady.then(() => {
            if (!live) return;
            frame = requestAnimationFrame(() => {
                frame = requestAnimationFrame(() => {
                    if (!live) return;
                    ref.current?.querySelector<HTMLButtonElement>('button[aria-expanded="false"]')?.click();
                });
            });
        });
        return () => { live = false; cancelAnimationFrame(frame); };
    }, [autoOpen]);
    return (
        <div ref={ref} className="flex min-w-0 flex-1 items-center gap-2">
            {label === '' ? null : (
                <span className="w-fixed-128px shrink-0 font-mono text-[0.625rem] text-text-subtle">{label}</span>
            )}
            <TagSelect
                tags={tags}
                allTags={STYLEGUIDE_TAG_DTOS}
                onChange={setTags}
                disabled={false}
                compact={compact}
                aria-label={label}
            />
        </div>
    );
}

/**
 * Twenty leg rows in a 344px scroller, scrolled to its end, with the panel open
 * from the last row. The panel is fixed to the viewport, so it hangs below the
 * scroller rather than inside it; the padding under the scroller is its room,
 * as under the "open" sample, so it covers nothing of the next section.
 */
function TightViewportDemo() {
    const scroller = useRef<HTMLDivElement>(null);
    useLayoutEffect(() => {
        const el = scroller.current;
        if (el !== null) el.scrollTop = el.scrollHeight;
    }, []);
    return (
        <div className="pb-fixed-208px">
            <div ref={scroller} className="max-h-fixed-344px w-full overflow-y-auto rounded border border-border">
                {Array.from({ length: 20 }, (_, i) => (
                    <div key={i} className="flex h-control-28px items-center gap-1 border-b border-border px-1">
                        {/* A wide leading cell, because in the real
                            splits grid the tags control sits mid-row
                            with room to its left. A repro that puts
                            it hard against the container's edge
                            measures the container, not the grid. */}
                        <span className="w-fixed-320px shrink-0 truncate text-[0.6875rem] text-text-subtle">
                            leg {i + 1}
                        </span>
                        <TagSelectDemo
                            label=""
                            compact
                            autoOpen={i === 19}
                            initial={i === 19 ? ['Project B'] : []}
                        />
                    </div>
                ))}
            </div>
        </div>
    );
}

function Swatch({ name, cls, text }: { name: string; cls: string; text?: string }) {
    return (
        <div className="flex items-center gap-3">
            <div
                className={`size-swatch-lg rounded border border-border ${cls}`}
                aria-label={name}
            />
            <code className={`font-mono text-xs ${text ?? 'text-text-muted'}`}>{name}</code>
        </div>
    );
}

export function StyleGuidePage() {
    return (
        <SidebarLayout data-testid="styleguide-page">
            <Sidebar>
                <SidebarHeader>
                    <span className="text-sm font-bold tracking-tight">Coffer</span>
                </SidebarHeader>
                <SidebarPicker swatch={<span className="size-dot-sm rounded-sm bg-accent" />}>
                    Style guide
                </SidebarPicker>
                <SidebarNav>
                    <SidebarNavLink active href="#tokens">
                        Tokens
                    </SidebarNavLink>
                    <SidebarNavLink href="#typography">Typography</SidebarNavLink>
                    <SidebarNavLink href="#buttons">Buttons</SidebarNavLink>
                    <SidebarNavLink href="#inputs">Inputs</SidebarNavLink>
                    <SidebarNavLink href="#chips">Chips &amp; status</SidebarNavLink>
                    <SidebarNavLink href="#currencies">Currencies</SidebarNavLink>
                    <SidebarSection>Categories</SidebarSection>
                    {categoryTokens.map((c) => (
                        <SidebarNavLink key={c.slug} href={`#cat-${c.slug}`}>
                            {c.label}
                        </SidebarNavLink>
                    ))}
                </SidebarNav>
                <SidebarFooter>
                    <span className="text-xs text-text-muted">Dev-only · ADR-0021</span>
                </SidebarFooter>
            </Sidebar>

            <MainArea>
                <TopBar>
                    <span className="text-text-muted">Coffer</span>
                    <span className="mx-1 text-text-subtle">/</span>
                    <span className="font-medium text-text">Style guide</span>
                </TopBar>
                <MainPane>
                    {/* As wide as a register row's grid (BANK_COLS) and its padding, so
                        the row and editor samples show their Amount and Balance
                        columns, where the second amount and its rate line sit. */}
                    <div className="mx-auto max-w-5xl space-y-8 p-6">
                        <header className="space-y-1">
                            <h1 className="text-2xl font-bold tracking-tight">Style guide</h1>
                            <p className="text-sm text-text-muted">
                                Every token in <code className="font-mono">src/index.css</code>{' '}
                                renders below. Dev-only — gated on{' '}
                                <code className="font-mono">import.meta.env.DEV</code>.
                            </p>
                        </header>

                        <Section id="tokens" title="Surfaces">
                            <div className="grid grid-cols-2 gap-3">
                                {surfaceTokens.map((t) => (
                                    <Swatch key={t.name} {...t} />
                                ))}
                            </div>
                        </Section>

                        <Section title="Borders">
                            <div className="grid grid-cols-2 gap-3">
                                {borderTokens.map((t) => (
                                    <div key={t.name} className="flex items-center gap-3">
                                        <div
                                            className={`size-swatch-lg rounded border-2 bg-surface ${t.cls}`}
                                        />
                                        <code className="font-mono text-xs text-text-muted">
                                            {t.name}
                                        </code>
                                    </div>
                                ))}
                            </div>
                        </Section>

                        <Section title="Text">
                            <div className="grid grid-cols-2 gap-3">
                                {textTokens.map((t) => (
                                    <div
                                        key={t.name}
                                        className={`flex items-center gap-3 rounded p-2 ${t.bg ?? ''}`}
                                    >
                                        <span className={`text-sm font-medium ${t.cls}`}>
                                            Aa text
                                        </span>
                                        <code className="font-mono text-xs text-text-muted">
                                            {t.name}
                                        </code>
                                    </div>
                                ))}
                            </div>
                        </Section>

                        <Section title="Accent">
                            <div className="grid grid-cols-3 gap-3">
                                {accentTokens.map((t) => (
                                    <div
                                        key={t.name}
                                        className={`rounded p-3 ${t.cls} ${t.text}`}
                                    >
                                        <code className="font-mono text-xs">{t.name}</code>
                                    </div>
                                ))}
                            </div>
                        </Section>

                        <Section id="typography" title="Typography">
                            <div className="space-y-2">
                                <p className="font-sans text-base">
                                    <strong className="font-semibold">Inter (font-sans):</strong>{' '}
                                    The quick brown fox jumps over the lazy dog. 0123456789.
                                </p>
                                <p className="font-mono text-base">
                                    <strong className="font-semibold">
                                        JetBrains Mono (font-mono):
                                    </strong>{' '}
                                    The quick brown fox jumps over the lazy dog. 0123456789.
                                </p>
                                <p className="font-mono text-sm tabular-nums text-text-muted">
                                    Balances align: $ 12,345.67 / $ 8,910.00 / $ 1,234.56
                                </p>
                            </div>
                        </Section>

                        <Section id="buttons" title="Buttons">
                            <div className="flex flex-wrap items-center gap-2">
                                <Button variant="primary">Primary</Button>
                                <Button variant="secondary">Secondary</Button>
                                <Button variant="ghost">Ghost</Button>
                                <Button variant="primary" disabled>
                                    Disabled
                                </Button>
                                <Button variant="primary" size="sm">
                                    Small
                                </Button>
                                <Button variant="primary" size="lg">
                                    Large
                                </Button>
                                <Button variant="danger">Danger</Button>
                            </div>
                            {/* In-row actions: 28px, the register's control height. */}
                            <div className="mt-3 flex flex-wrap items-center gap-2">
                                <Button variant="secondary" size="xs">Extra small</Button>
                                <Button variant="ghost" size="xs">Hide</Button>
                                <Button variant="link" size="xs">Post or skip…</Button>
                            </div>
                        </Section>

                        <Section id="inputs" title="Inputs">
                            <div className="grid grid-cols-2 gap-3">
                                <div className="space-y-1">
                                    <Label htmlFor="sg-input">Username</Label>
                                    <Input id="sg-input" placeholder="alice" />
                                </div>
                                <div className="space-y-1">
                                    <Label htmlFor="sg-input-disabled">Disabled</Label>
                                    <Input
                                        id="sg-input-disabled"
                                        placeholder="cannot edit"
                                        disabled
                                    />
                                </div>
                            </div>
                        </Section>

                        <Section title="Segmented control">
                            <SegmentedDemo />
                        </Section>

                        <Section title="Mark bar">
                            <div className="grid max-w-md grid-cols-[6rem_1fr] items-center gap-x-3 gap-y-2 text-xs text-text-muted">
                                <span>Under the mark</span>
                                <MarkBar actual={60} mark={80} scaleMax={100} />
                                <span>Over the mark</span>
                                <MarkBar actual={95} mark={80} scaleMax={100} />
                                <span>Just over</span>
                                <MarkBar actual={80.4} mark={80} scaleMax={100} />
                                <span>No mark</span>
                                <MarkBar actual={45} mark={null} scaleMax={100} />
                            </div>
                        </Section>

                        <Section title="Converted figures">
                            <ConvertedSamples />
                        </Section>

                        <Section title="Skeleton">
                            <div aria-busy="true" aria-label="Loading sample" className="max-w-md space-y-2">
                                <Skeleton className="h-control-20px w-fixed-160px" />
                                <Skeleton className="h-control-28px" />
                                <Skeleton className="h-control-28px" />
                            </div>
                        </Section>

                        <Section title="Chart bars and point selection">
                            <ChartSample />
                        </Section>

                        <Section title="Drag handle and insertion line">
                            <div className="max-w-md space-y-1 text-xs">
                                <div className="flex items-center gap-2 rounded border border-border px-2 py-1.5">
                                    <span className="text-text-subtle"><GripIcon /></span>
                                    <span>First item</span>
                                </div>
                                <InsertionLine />
                                <div className="flex items-center gap-2 rounded border border-border px-2 py-1.5">
                                    <span className="text-text-subtle"><GripIcon /></span>
                                    <span>Second item</span>
                                </div>
                            </div>
                        </Section>

                        <Section id="chips" title="Status &amp; semantic states">
                            <div className="flex flex-wrap gap-2">
                                {stateTokens.map((t) => (
                                    <span
                                        key={t.name}
                                        className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium ${t.bg} ${t.text}`}
                                    >
                                        {t.label}
                                    </span>
                                ))}
                            </div>
                        </Section>

                        <Section title="Attention indicators">
                            {/* ADR-0102 D4. Here because this page is where the two
                                states sit side by side, which is the comparison that
                                settled the design: the first build filled both with
                                state-warning and state-danger, each cleared its
                                contrast floor against the surface, and on screen they
                                were the same dot. Outline-vs-fill is the channel that
                                survives at 10px. Hover either for the sentence a
                                screen reader gets. */}
                            {/* The raw tokens as well as the component: this page's
                                contract is that every token in index.css renders here,
                                and a token reachable only THROUGH a component is a
                                token nobody can compare against its neighbours. */}
                            <div className="mb-4 grid gap-3 sm:grid-cols-2">
                                <Swatch name="--color-mark-warning" cls="bg-mark-warning" />
                                <Swatch name="--color-mark-danger" cls="bg-mark-danger" />
                            </div>
                            <div className="flex flex-wrap items-center gap-6 text-xs">
                                <span className="inline-flex items-center gap-1.5">
                                    <AttentionDot severity="warning" context="Ledger" />
                                    warning, no count (app root)
                                </span>
                                <span className="inline-flex items-center gap-1.5">
                                    <AttentionDot severity="critical" context="Ledger" />
                                    critical, no count (app root)
                                </span>
                                <span className="inline-flex items-center gap-1.5">
                                    <AttentionCount severity="warning" count={1} context="Ledger" />
                                    warning, counted (row / tab)
                                </span>
                                <span className="inline-flex items-center gap-1.5">
                                    <AttentionCount severity="critical" count={12} context="Ledger" />
                                    critical, counted (row / tab)
                                </span>
                                <span className="inline-flex items-center gap-1.5 text-text-subtle">
                                    <AttentionDot severity={null} context="Ledger" />
                                    nothing outstanding — renders nothing at all
                                </span>
                            </div>
                        </Section>

                        <Section title="Tag line">
                            {/* The register's tag display (ADR-0103). Chips wrapped,
                                so a heavily tagged row grew to three times its
                                neighbours and the amount column stopped lining up.
                                One line now; the palette survives as a STRIPE rather
                                than as text colour, because the hexes are chip fills
                                and several of them are ~2:1 as foreground. */}
                            <TagColorsValueProvider colors={STYLEGUIDE_TAGS}>
                                <div className="max-w-fixed-344px space-y-2">
                                    <TagLine tags={['Project A']} />
                                    <TagLine tags={['Project A', 'Project B']} />
                                    <TagLine tags={[
                                        'Project A', 'Project B', 'Project C', 'Project D',
                                        'Tax Deductible 2024',
                                        'Kitchen Remodel Phase 2 Contractor Payments 2024',
                                        'Phase 1 / Phase 2',
                                    ]} />
                                    <TagLine tags={['uncoloured tag', 'Project B']} />
                                </div>
                            </TagColorsValueProvider>
                        </Section>

                        <Section title="Tag select">
                            {/* The editor control (ADR-0103), used on every tag
                                surface: header tags, posting tags and the
                                investment event. Strip and button are separate
                                targets on purpose — clicking the strip must not
                                open the panel, or a long value cannot be read. */}
                            <TagColorsValueProvider colors={STYLEGUIDE_TAGS}>
                                <div className="max-w-fixed-344px space-y-2">
                                    <TagSelectDemo label="empty" initial={[]} />
                                    <TagSelectDemo label="one tag" initial={['Project A']} />
                                    <TagSelectDemo
                                        label="many"
                                        initial={['Project A', 'Project B', 'Project C', 'Project D', 'Tax Deductible 2024']}
                                    />
                                    <TagSelectDemo label="compact" compact initial={['Project B', 'Project D']} />
                                </div>
                                <div className="mt-3 max-w-fixed-344px pb-fixed-208px">
                                    <TagSelectDemo label="open" autoOpen initial={['Project B']} />
                                </div>
                            </TagColorsValueProvider>
                        </Section>

                        <Section title="Tag select in a tight viewport">
                            {/* The case the style guide could not show before, and
                                the one that broke on dev: a panel opened from a row
                                near the BOTTOM of a 344px scroller that is already
                                scrolled to its end. revealRow cannot lift that row to
                                the top — there is no scroll range left — so whatever
                                the panel needs below it, it does not get. */}
                            <TagColorsValueProvider colors={STYLEGUIDE_TAGS}>
                                <TightViewportDemo />
                            </TagColorsValueProvider>
                        </Section>

                        <Section title="Memo and tags per side">
                            {/* Every register shape and editor state the memo-and-tags
                                design shows, through the real row strategy and the
                                real editor. Placeholder data. */}
                            <div id="memo-tags">
                                <MemoTagsSamples />
                            </div>
                        </Section>

                        <Section id="currencies" title="Currency decimals and transfers between currencies">
                            {/* Money at each currency's own places, the money input's
                                symbol on either side, and the second amount of a
                                transfer between currencies with each rate line
                                (ADR-0114 D13, D14, D15), through the real formatters,
                                rows and editor. Placeholder data. */}
                            <CurrencySamples />
                        </Section>

                        <Section title="Editor dialog">
                            <EditorDialogSamples />
                        </Section>

                        <Section title="Category chips">
                            <div className="flex flex-wrap gap-2">
                                {categoryTokens.map((c) => (
                                    <span
                                        key={c.slug}
                                        id={`cat-${c.slug}`}
                                        className={`inline-flex items-center rounded px-2 py-0.5 text-xs font-medium ${c.bg} ${c.text}`}
                                    >
                                        {c.label}
                                    </span>
                                ))}
                            </div>
                        </Section>

                        <Section title="Chart shades">
                            <div className="mb-4 flex h-control-40px max-w-md overflow-hidden rounded border border-border">
                                {chartShadeTokens.map((t) => (
                                    <div key={t.name} className={`flex-1 ${t.cls}`} aria-hidden />
                                ))}
                            </div>
                            <div className="grid gap-3 sm:grid-cols-2">
                                {chartShadeTokens.map((t) => (
                                    <Swatch key={t.name} name={t.name} cls={t.cls} />
                                ))}
                            </div>
                        </Section>

                        <Section title="Register row preview">
                            <div className="overflow-x-auto rounded border border-border bg-surface">
                                <table className="w-full text-xs">
                                    <thead className="border-b border-border bg-surface-muted text-[0.625rem] uppercase tracking-wider text-text-muted">
                                        <tr>
                                            <th className="px-2 py-1.5 text-left font-semibold">
                                                Date
                                            </th>
                                            <th className="px-2 py-1.5 text-left font-semibold">
                                                Status
                                            </th>
                                            <th className="px-2 py-1.5 text-left font-semibold">
                                                Payee
                                            </th>
                                            <th className="px-2 py-1.5 text-left font-semibold">
                                                Category
                                            </th>
                                            <th className="px-2 py-1.5 text-right font-semibold">
                                                Amount
                                            </th>
                                            <th className="px-2 py-1.5 text-right font-semibold">
                                                Balance
                                            </th>
                                        </tr>
                                    </thead>
                                    <tbody className="divide-y divide-border">
                                        <tr className="hover:bg-surface-hover">
                                            <td className="px-2 py-1 font-mono tabular-nums text-text-muted">
                                                2026-05-11
                                            </td>
                                            <td className="px-2 py-1">
                                                {/* The register's own badge, so the sample cannot
                                                    drift from it: Cleared is the accent, not green. */}
                                                <StatusBadge status="cleared" />
                                            </td>
                                            <td className="px-2 py-1">
                                                <span className="font-medium">Food Market</span>{' '}
                                                <span className="text-text-muted">
                                                    · Weekly groceries
                                                </span>
                                            </td>
                                            <td className="px-2 py-1">
                                                <span className="inline-flex items-center rounded bg-cat-groc-soft px-2 py-0.5 text-xs font-medium text-cat-groc-text">
                                                    Groceries
                                                </span>
                                            </td>
                                            <td className="px-2 py-1 text-right font-mono tabular-nums text-state-danger">
                                                -$87.12
                                            </td>
                                            <td className="px-2 py-1 text-right font-mono tabular-nums">
                                                $4,820.12
                                            </td>
                                        </tr>
                                    </tbody>
                                </table>
                            </div>
                        </Section>
                    </div>
                </MainPane>
            </MainArea>
        </SidebarLayout>
    );
}

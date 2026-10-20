import { useEffect, useRef, useState } from 'react';
import { AttentionCount, AttentionDot } from '@/components/AttentionDot';
import { TagLine } from '@/components/tags/TagLine';
import { TagSelect } from '@/components/tags/TagSelect';
import { TagColorsValueProvider } from '@/components/tags/TagColorsContext';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Label } from '@/components/ui/Label';
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

const stateTokens = [
    { name: 'success', bg: 'bg-state-success-soft', text: 'text-state-success', label: '✓ Cleared' },
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

function Section({ title, children }: { title: string; children: React.ReactNode }) {
    return (
        <section className="space-y-3 border-b border-border pb-6 last:border-b-0">
            <h2 className="text-sm font-semibold uppercase tracking-wider text-text-muted">
                {title}
            </h2>
            {children}
        </section>
    );
}

/** A small palette for the tag-line demo; the provider keys on lower-cased name. */
const STYLEGUIDE_TAG_DTOS = [
    { id: 'g1', name: 'Property A', color: '#2563eb', usageCount: 12 },
    { id: 'g2', name: 'Property B', color: '#16a34a', usageCount: 8 },
    { id: 'g3', name: 'Property C', color: '#d97706', usageCount: 5 },
    { id: 'g4', name: 'Property D', color: '#9333ea', usageCount: 3 },
    { id: 'g5', name: 'Tax Deductible 2024', color: '#dc2626', usageCount: 21 },
    { id: 'g6', name: 'Kitchen Remodel Phase 2 Contractor Payments 2024', color: '#0891b2', usageCount: 2 },
    { id: 'g7', name: 'Unit 1 / Unit 2', color: '#64748b', usageCount: 1 },
];

const STYLEGUIDE_TAGS = [
    { name: 'Property A', color: '#2563eb' },
    { name: 'Property B', color: '#16a34a' },
    { name: 'Property C', color: '#d97706' },
    { name: 'Property D', color: '#9333ea' },
    { name: 'Tax Deductible 2024', color: '#dc2626' },
    { name: 'Kitchen Remodel Phase 2 Contractor Payments 2024', color: '#0891b2' },
    { name: 'Unit 1 / Unit 2', color: '#64748b' },
    { name: 'uncoloured tag', color: null },
];

/**
 * A stateful wrapper for the gallery — {@link TagSelect} is controlled, so
 * something has to hold the value for it to be clickable here.
 *
 * `autoOpen` clicks its own edit button once on mount. That exists so the OPEN
 * panel is visible in a static capture of this page; it is gallery scaffolding
 * and deliberately not a prop on the component, which would be production code
 * serving a screenshot.
 */
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
    const clicked = useRef(false);
    useEffect(() => {
        // Guarded because StrictMode double-invokes effects in dev: two clicks
        // on a toggle is zero clicks, which is exactly what the first capture
        // of this page showed.
        if (!autoOpen || clicked.current) return;
        clicked.current = true;
        ref.current?.querySelector<HTMLButtonElement>('button[aria-expanded]')?.click();
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
                    <div className="mx-auto max-w-4xl space-y-8 p-6">
                        <header className="space-y-1">
                            <h1 className="text-2xl font-bold tracking-tight">Style guide</h1>
                            <p className="text-sm text-text-muted">
                                Every token in <code className="font-mono">src/index.css</code>{' '}
                                renders below. Dev-only — gated on{' '}
                                <code className="font-mono">import.meta.env.DEV</code>.
                            </p>
                        </header>

                        <Section title="Surfaces">
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

                        <Section title="Typography">
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

                        <Section title="Buttons">
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
                            </div>
                        </Section>

                        <Section title="Inputs">
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

                        <Section title="Status &amp; semantic states">
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
                                    <TagLine tags={['Property A']} />
                                    <TagLine tags={['Property A', 'Property B']} />
                                    <TagLine tags={[
                                        'Property A', 'Property B', 'Property C', 'Property D',
                                        'Tax Deductible 2024',
                                        'Kitchen Remodel Phase 2 Contractor Payments 2024',
                                        'Unit 1 / Unit 2',
                                    ]} />
                                    <TagLine tags={['uncoloured tag', 'Property B']} />
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
                                    <TagSelectDemo label="one tag" initial={['Property A']} />
                                    <TagSelectDemo
                                        label="many"
                                        initial={['Property A', 'Property B', 'Property C', 'Property D', 'Tax Deductible 2024']}
                                    />
                                    <TagSelectDemo label="compact" compact initial={['Property B', 'Property D']} />
                                </div>
                                <div className="mt-3 max-w-fixed-344px pb-fixed-208px">
                                    <TagSelectDemo label="open" autoOpen initial={['Property B']} />
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
                                <div className="max-h-fixed-344px w-full overflow-y-auto rounded border border-border">
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
                                                initial={i === 19 ? ['Property B'] : []}
                                            />
                                        </div>
                                    ))}
                                </div>
                            </TagColorsValueProvider>
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
                                                Outflow
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
                                                <span className="inline-flex size-icon-md items-center justify-center rounded-full bg-state-success-soft text-[0.625rem] font-bold text-state-success">
                                                    &#10003;
                                                </span>
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
                                                87.12
                                            </td>
                                            <td className="px-2 py-1 text-right font-mono tabular-nums">
                                                4,820.12
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

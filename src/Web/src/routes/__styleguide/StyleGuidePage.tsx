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

function Swatch({ name, cls, text }: { name: string; cls: string; text?: string }) {
    return (
        <div className="flex items-center gap-3">
            <div
                className={`h-10 w-10 rounded border border-border ${cls}`}
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
                <SidebarPicker swatch={<span className="h-1.5 w-1.5 rounded-sm bg-accent" />}>
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
                                            className={`h-10 w-10 rounded border-2 bg-surface ${t.cls}`}
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
                                                <span className="inline-flex h-4 w-4 items-center justify-center rounded-full bg-state-success-soft text-[0.625rem] font-bold text-state-success">
                                                    &#10003;
                                                </span>
                                            </td>
                                            <td className="px-2 py-1">
                                                <span className="font-medium">Whole Foods</span>{' '}
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

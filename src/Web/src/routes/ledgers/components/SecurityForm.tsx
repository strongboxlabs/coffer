import { useState } from 'react';

import type {
    CreateSecurityRequest,
    PatchSecurityRequest,
    SecurityDetail,
} from '@/lib/types';
import { SECURITY_ASSET_CLASSES } from '@/lib/types';

/**
 * Shared state + rendering for the security create / edit forms. Add and Edit
 * are the same form for everything they have in common — ticker, quote symbol,
 * the public/private quote-symbol flag, CUSIP, name, asset class, and
 * auto-pricing — plus the identical gating logic (auto-pricing needs a symbol; a
 * bare ticker is always public) and payload rules. This is the single source of
 * truth for all of that, so the two dialogs can't drift.
 *
 * Edit-only fields (rich classification, look-through sleeves, the Active
 * toggle) are NOT shared, so the Edit dialog passes them in via the
 * <see cref="SecurityFormFields"/> `extras` slot, which renders between the
 * asset class and the auto-price toggle.
 */
export function useSecurityForm(initial?: Partial<SecurityDetail>) {
    const [name, setName] = useState(initial?.name ?? '');
    const [ticker, setTicker] = useState(initial?.ticker ?? '');
    const [cusip, setCusip] = useState(initial?.cusip ?? '');
    const [assetClass, setAssetClass] = useState(initial?.assetClass ?? '');
    const [quoteSymbol, setQuoteSymbol] = useState(initial?.quoteSymbol ?? '');
    const [autoPrice, setAutoPrice] = useState(initial?.autoPrice ?? true);
    const [quoteSymbolPublic, setQuoteSymbolPublic] = useState(initial?.quoteSymbolPublic ?? true);

    // Auto-pricing needs a symbol to match/fetch — a ticker OR a quote symbol
    // (public or private: a private quote symbol is still priced by the feed).
    const hasSymbol = ticker.trim().length > 0 || quoteSymbol.trim().length > 0;
    const isValid = name.trim().length > 0;

    // A bare ticker is always public; only send "not public" with a symbol.
    const effectiveQuoteSymbolPublic = quoteSymbol.trim().length > 0 ? quoteSymbolPublic : true;
    // Auto-pricing needs a symbol; force it off when there's none.
    const effectiveAutoPrice = hasSymbol ? autoPrice : false;

    function buildCreatePayload(): CreateSecurityRequest {
        return {
            name: name.trim(),
            ticker: ticker.trim() === '' ? null : ticker.trim(),
            cusip: cusip.trim() === '' ? null : cusip.trim(),
            assetClass: assetClass === '' ? null : assetClass,
            quoteSymbol: quoteSymbol.trim() === '' ? null : quoteSymbol.trim(),
            autoPrice: effectiveAutoPrice,
            quoteSymbolPublic: effectiveQuoteSymbolPublic,
        };
    }

    /** The subset of the PATCH body that Add + Edit share. Edit spreads this and
     *  adds its classification / isActive fields. Empty string clears (→ null),
     *  matching the override-style PATCH semantics. */
    function buildPatchShared(): PatchSecurityRequest {
        return {
            name: name.trim(),
            ticker: ticker.trim(),
            cusip: cusip.trim(),
            assetClass: assetClass === '' ? '' : assetClass,
            quoteSymbol: quoteSymbol.trim(),
            autoPrice: effectiveAutoPrice,
            quoteSymbolPublic: effectiveQuoteSymbolPublic,
        };
    }

    return {
        name, setName,
        ticker, setTicker,
        cusip, setCusip,
        assetClass, setAssetClass,
        quoteSymbol, setQuoteSymbol,
        autoPrice, setAutoPrice,
        quoteSymbolPublic, setQuoteSymbolPublic,
        hasSymbol, isValid,
        buildCreatePayload, buildPatchShared,
    };
}

export type SecurityFormState = ReturnType<typeof useSecurityForm>;

const inputCls =
    'w-full rounded border border-border bg-surface px-3 py-1.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

/**
 * The shared fields, in the order both dialogs render them: ticker, quote
 * symbol, the public-quote-symbol flag, CUSIP, name, asset class, [extras],
 * auto-price. `extras` (edit-only classification / sleeves / Active) slots in
 * before the auto-price toggle so the auto-price control stays last in both.
 */
export function SecurityFormFields({
    form,
    errorCode,
    extras,
}: {
    form: SecurityFormState;
    errorCode?: string;
    extras?: React.ReactNode;
}) {
    return (
        <>
            <Field
                label="Ticker"
                hint="Optional. Case-insensitive uniqueness within this ledger."
                error={errorCode === 'security-duplicate-ticker'
                    ? 'A security with this ticker already exists in this ledger.'
                    : undefined}
            >
                <input
                    type="text"
                    value={form.ticker}
                    onChange={(e) => form.setTicker(e.target.value)}
                    placeholder="e.g. ABCDX"
                    className={inputCls}
                />
            </Field>
            <Field
                label="Quote symbol"
                hint="Symbol sent to the price provider when it differs from the ticker — blank uses the ticker."
            >
                <input
                    type="text"
                    value={form.quoteSymbol}
                    onChange={(e) => form.setQuoteSymbol(e.target.value)}
                    placeholder="defaults to ticker"
                    className={inputCls}
                />
            </Field>
            <label className="flex items-start gap-2 text-sm">
                <input
                    type="checkbox"
                    className="mt-0.5"
                    checked={form.quoteSymbol.trim().length > 0 ? form.quoteSymbolPublic : true}
                    disabled={form.quoteSymbol.trim().length === 0}
                    onChange={(e) => form.setQuoteSymbolPublic(e.target.checked)}
                />
                <span>
                    Public quote symbol
                    <span className="block text-xs text-text-subtle">
                        Uncheck for a private / feed-only symbol (e.g. a 529 portfolio
                        number): matched only against your bank feed, never sent to
                        external price providers.
                    </span>
                </span>
            </label>
            <Field
                label="CUSIP"
                hint="Optional."
                error={errorCode === 'security-duplicate-cusip'
                    ? 'A security with this CUSIP already exists in this ledger.'
                    : undefined}
            >
                <input
                    type="text"
                    value={form.cusip}
                    onChange={(e) => form.setCusip(e.target.value)}
                    placeholder="e.g. 037833100"
                    className={inputCls}
                />
            </Field>
            <Field
                label="Name"
                required
                error={errorCode === 'security-name-required' ? 'Name is required.' : undefined}
            >
                <input
                    type="text"
                    value={form.name}
                    onChange={(e) => form.setName(e.target.value)}
                    required
                    autoFocus
                    placeholder="e.g. Broad Market Index Fund"
                    className={inputCls}
                />
            </Field>
            <Field label="Asset class">
                <select
                    value={form.assetClass}
                    onChange={(e) => form.setAssetClass(e.target.value)}
                    className={inputCls}
                >
                    <option value="">— None —</option>
                    {SECURITY_ASSET_CLASSES.map((cls) => (
                        <option key={cls} value={cls}>{cls.replace(/_/g, ' ')}</option>
                    ))}
                </select>
            </Field>

            {extras}

            <label className="flex items-start gap-2 text-sm">
                <input
                    type="checkbox"
                    className="mt-0.5"
                    checked={form.hasSymbol ? form.autoPrice : false}
                    disabled={!form.hasSymbol}
                    onChange={(e) => form.setAutoPrice(e.target.checked)}
                />
                <span>
                    Auto-update prices
                    {!form.hasSymbol ? (
                        <span className="block text-xs text-text-subtle">
                            Add a ticker or quote symbol to enable.
                        </span>
                    ) : null}
                </span>
            </label>
        </>
    );
}

function Field({
    label,
    hint,
    error,
    required,
    children,
}: {
    label: string;
    hint?: string;
    error?: string;
    required?: boolean;
    children: React.ReactNode;
}) {
    return (
        <label className="flex flex-col gap-1">
            <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">
                {label}
                {required ? <span aria-hidden className="ml-0.5 text-state-danger">*</span> : null}
            </span>
            {children}
            {error ? (
                <span role="alert" className="text-xs text-state-danger">{error}</span>
            ) : hint ? (
                <span className="text-xs text-text-subtle">{hint}</span>
            ) : null}
        </label>
    );
}

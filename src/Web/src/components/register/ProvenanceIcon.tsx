import { Globe, FileText, Pencil, GitMerge } from 'lucide-react';

/**
 * Register provenance indicator (mig 107). Visualises the source
 * mechanism a transaction came from — online feed, file upload, or
 * manual entry — with an optional merge-winner overlay when other
 * rows were merged into this one.
 *
 * The icon is derived client-side from a register row's `origin`
 * (the icon-level mechanism) and `isMergeWinner` (the overlay) —
 * both universal `RegisterRowBase` fields, so the icon is
 * domain-agnostic. The full label (e.g. "SimpleFIN
 * sync · accepted") goes on the `title` attribute so hover + screen
 * readers carry the audit detail without crowding the visual.
 *
 * Slot semantics in the register row: leftmost narrow column,
 * uniform width across bank + investment registers so the column
 * line stays vertical. Compact (h-3 w-3 icons) to keep the column
 * sub-text-line in height.
 */
export interface ProvenanceIconProps {
    origin: string;
    providerKey: string | null;
    isMergeWinner: boolean;
}

export function ProvenanceIcon({
    origin,
    providerKey,
    isMergeWinner,
}: ProvenanceIconProps) {
    const { Icon, label, hue } = pickIcon(origin, providerKey);
    const fullLabel = isMergeWinner
        ? `${label} · merged ←`
        : label;
    return (
        <span
            className="relative inline-flex h-3 w-3 items-center justify-center"
            title={fullLabel}
            aria-label={fullLabel}
        >
            <Icon className={`h-3 w-3 ${hue}`} strokeWidth={2} aria-hidden />
            {isMergeWinner ? (
                // Merge-winner overlay: small chevron in the
                // upper-right, accent palette so it pops against
                // the muted provenance icon.
                <GitMerge
                    className="absolute -right-1 -top-1 h-2 w-2 text-accent"
                    strokeWidth={2.5}
                    aria-hidden
                />
            ) : null}
        </span>
    );
}

function pickIcon(origin: string, providerKey: string | null): {
    Icon: typeof Globe;
    label: string;
    hue: string;
} {
    switch (origin) {
        case 'online_import':
            return {
                Icon: Globe,
                // Use providerKey for the hover detail when present;
                // falls back to the generic label when null.
                label: providerKey
                    ? `Online import · ${providerLabel(providerKey)}`
                    : 'Online import',
                // Muted blue — feed/online association without
                // grabbing attention. Same palette family the SPA
                // uses for accent.
                hue: 'text-accent/70',
            };
        case 'file_import':
            return {
                Icon: FileText,
                label: providerKey
                    ? `File import · ${providerLabel(providerKey)}`
                    : 'File import',
                // Muted amber — file/document association.
                hue: 'text-state-warning/80',
            };
        case 'manual':
        default:
            return {
                Icon: Pencil,
                label: 'Manual entry',
                // Muted text — manual rows are the baseline; the
                // icon shouldn't draw eye away from the row content.
                hue: 'text-text-subtle',
            };
    }
}

function providerLabel(providerKey: string): string {
    switch (providerKey) {
        case 'simplefin': return 'SimpleFIN';
        case 'mdplus':    return 'MD+';
        case 'ofx':       return 'OFX';
        case 'qif':       return 'QIF';
        case 'csv':       return 'CSV';
        default:          return providerKey;
    }
}

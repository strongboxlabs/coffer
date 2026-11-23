import { Link } from '@tanstack/react-router';

import { SkeletonRows } from '@/components/ui/Skeleton';
import { accountTypeMeta } from '@/lib/accountTypes';
import type { OverviewAccountGroup } from '@/lib/types';

import { useHome, useWidgetFrame } from '../homeContext';
import { Money } from '../Money';
import { useOverview } from '../queries';
import { WidgetError, WidgetPanel } from '../WidgetPanel';

/**
 * Every account by type, with the server's subtotals. A subtotal is in the ledger's
 * reporting currency, converted (ADR-0114 D7), and reads "no rate" when a rate it
 * needs is missing; each account stays in its own currency. A closed account is
 * marked so and still counted, as net worth counts it.
 */
export function AccountsWidget() {
    const home = useHome();
    const frame = useWidgetFrame();
    const includeInactive = frame.settings.includeInactive === true;
    const overview = useOverview(home.ledgerId, home.today);

    const link = (
        <Link to="/ledgers/$ledgerId/accounts" params={{ ledgerId: home.ledgerId }} className="text-xs font-medium text-accent hover:underline">
            Manage accounts →
        </Link>
    );
    if (overview.isError) {
        return <WidgetPanel title="Accounts" link={link}><WidgetError what="accounts" error={overview.error} onRetry={() => void overview.refetch()} /></WidgetPanel>;
    }
    if (!overview.data) {
        return <WidgetPanel title="Accounts" link={link}><SkeletonRows rows={5} label="Loading accounts" /></WidgetPanel>;
    }
    const groups = overview.data.accountGroups
        .map((g) => ({ group: g, shown: g.accounts.filter((a) => includeInactive || a.isActive) }))
        .filter((g) => g.shown.length > 0);

    return (
        <WidgetPanel title="Accounts" link={link}>
            {groups.length === 0 ? (
                <p className="p-4 text-sm text-text-muted">No accounts yet.</p>
            ) : (
                <div className="divide-y divide-border">
                    {groups.map((g) => (
                        <Group key={g.group.accountType} group={g.group} shown={g.shown} currency={overview.data.currencyCode} />
                    ))}
                </div>
            )}
        </WidgetPanel>
    );
}

/**
 * One type's accounts under the server's subtotal, which counts every account of the
 * type, closed ones too, converted into the reporting `currency`.
 */
function Group({ group, shown, currency }: {
    group: OverviewAccountGroup;
    shown: OverviewAccountGroup['accounts'];
    currency: string;
}) {
    const home = useHome();
    const meta = accountTypeMeta(group.accountType);
    const Icon = meta.icon;
    return (
        <div>
            <div className="flex items-center gap-2 bg-surface-muted/30 px-4 py-1.5 text-[0.6875rem] font-semibold uppercase tracking-wider text-text-muted">
                <Icon className="size-icon-sm" aria-hidden />
                <span>{meta.label}</span>
                <Money
                    amount={group.subtotal}
                    currency={currency}
                    tone="fixed"
                    rateUnavailable={group.rateUnavailable}
                    className="ml-auto font-normal"
                />
            </div>
            <ul aria-label={meta.label} className="divide-y divide-border/60">
                {shown.map((a) => (
                    <li key={a.id}>
                        <Link
                            to="/ledgers/$ledgerId/accounts/$accountId"
                            params={{ ledgerId: home.ledgerId, accountId: a.id }}
                            className="flex items-center justify-between gap-3 px-4 py-1.5 hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent"
                        >
                            <span className="min-w-0 flex-1 truncate text-sm" title={a.name}>
                                {a.name}
                                {a.isActive ? null : <span className="ml-2 text-xs text-text-muted">closed</span>}
                            </span>
                            <Money amount={a.balance} currency={a.currencyCode} tone="fixed" rateUnavailable={a.rateUnavailable} className="text-xs font-normal" />
                        </Link>
                    </li>
                ))}
            </ul>
        </div>
    );
}

import type { ReactNode } from 'react';
import { Link } from '@tanstack/react-router';

import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { TopBar } from '@/components/ui/SidebarLayout';
import type { LedgerSummary } from '@/lib/types';

interface RegisterTopBarProps {
    ledgerId: string;
    ledger: LedgerSummary | null;
    /** The current account's display name, resolved by the page — including
     *  inactive accounts that aren't in the active-only list. Null → "Account". */
    accountName: string | null;
    /**
     * Optional per-page actions rendered to the right of the
     * breadcrumb (e.g. the bank-register Upload + Sync icons).
     * Multiple icons are common — pass them as a fragment; this
     * component wraps them in a flex container so TopBar's
     * justify-between keeps the whole group pinned to the right
     * instead of spacing siblings out across the bar.
     */
    actions?: ReactNode;
}

/**
 * Shared TopBar for the register surface (ADR-0030 §3). The
 * breadcrumb shape is identical across domains; per-page action
 * buttons differ and are passed through the <c>actions</c> slot.
 *
 * Lives in <c>register/shell/</c> alongside other shape-agnostic
 * register primitives; both <c>BankRegisterPage</c> (today's
 * <c>RegisterPage</c>) and <c>InvestmentRegisterPage</c> render it.
 */
export function RegisterTopBar({
    ledgerId,
    ledger,
    accountName,
    actions,
}: RegisterTopBarProps) {
    return (
        <TopBar>
            <Breadcrumb
                items={[
                    {
                        label: ledger?.name ?? 'Ledger',
                        node: ledger !== null ? (
                            <Link
                                to="/ledgers/$ledgerId"
                                params={{ ledgerId }}
                                className="hover:text-text"
                            >
                                {ledger.name}
                            </Link>
                        ) : (
                            'Ledger'
                        ),
                    },
                    { label: accountName ?? 'Account' },
                ]}
            />
            {actions !== undefined ? (
                <div className="flex items-center gap-1">{actions}</div>
            ) : null}
        </TopBar>
    );
}

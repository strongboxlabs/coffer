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
     * Ancestor categories, root-first, EXCLUDING the account itself —
     * non-null only when this register is a category's.
     *
     * A category isn't reached from the accounts list, so "Ledger /
     * Groceries" leaves out both where it lives and that it's a category
     * at all. Non-null turns the trail into
     * `Ledger / Categories / Food / Groceries`: the same parent/child
     * path notation used everywhere a category is shown outside a tree,
     * except that here the breadcrumb's own separators do the joining and
     * every segment is a link to that category's own register.
     *
     * Empty array = a root category (the Categories crumb, then the name).
     */
    categoryTrail?: ReadonlyArray<{ id: string; name: string }> | null;
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
    categoryTrail = null,
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
                    ...(categoryTrail !== null
                        ? [
                            {
                                label: 'Categories',
                                node: (
                                    <Link
                                        to="/ledgers/$ledgerId/categories"
                                        params={{ ledgerId }}
                                        className="hover:text-text"
                                    >
                                        Categories
                                    </Link>
                                ),
                            },
                            ...categoryTrail.map((ancestor) => ({
                                label: ancestor.name,
                                node: (
                                    <Link
                                        to="/ledgers/$ledgerId/accounts/$accountId"
                                        params={{
                                            ledgerId,
                                            accountId: ancestor.id,
                                        }}
                                        className="hover:text-text"
                                    >
                                        {ancestor.name}
                                    </Link>
                                ),
                            })),
                        ]
                        : []),
                    { label: accountName ?? 'Account' },
                ]}
            />
            {actions !== undefined ? (
                <div className="flex items-center gap-1">{actions}</div>
            ) : null}
        </TopBar>
    );
}

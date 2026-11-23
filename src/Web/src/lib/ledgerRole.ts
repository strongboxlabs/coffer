import type { LedgerSummary } from '@/lib/types';

/**
 * The ledger role rules, and the only place a role is compared with one of its names
 * (ADR-0111 D3; LedgerRoleRule.test.ts holds every other file to it).
 *
 * Screens do not call these with a role they found themselves: they ask
 * {@link useLedgerRole}, which reads the reader's role from the visible-ledgers read
 * (`['ledgers']`) and answers with them. The server refuses a write the role does not
 * allow regardless; these decide only whether a control is offered.
 */

/**
 * Whether a ledger role may write: owners and editors do, a viewer reads, and an
 * unknown role (the ledger list has not answered) writes nothing, so a control
 * never flashes for a viewer.
 */
export function canWriteLedger(role: LedgerSummary['role'] | undefined): boolean {
    return role === 'owner' || role === 'editor';
}

/**
 * Whether a ledger role is an owner's: the one that renames and deletes the ledger,
 * manages its members, its bank connections and where its notifications go. An
 * unknown role is not, so an owner's control appears a moment late rather than
 * flashing for someone else. It answers for any role, the reader's or a member's in
 * the Members list.
 */
export function isLedgerOwner(role: LedgerSummary['role'] | undefined): boolean {
    return role === 'owner';
}

import { describe, it, expect, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';

import { LedgerHubSection } from './LedgerHubSection';

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

describe('LedgerHubSection', () => {
    beforeEach(() => {
        try {
            window.localStorage.clear();
        } catch {
            /* env without localStorage */
        }
    });

    it('renders title, count badge, and body when expanded', () => {
        render(
            <LedgerHubSection
                sectionKey="t1"
                ledgerId={LEDGER_ID}
                title="Securities"
                count={42}
            >
                <div data-testid="body">body content</div>
            </LedgerHubSection>,
        );

        expect(screen.getByText('Securities')).toBeInTheDocument();
        expect(screen.getByText('42')).toBeInTheDocument();
        expect(screen.getByTestId('body')).toBeInTheDocument();
    });

    it('hides body when collapsed and shows again on click', () => {
        render(
            <LedgerHubSection
                sectionKey="t2"
                ledgerId={LEDGER_ID}
                title="Categories"
                defaultExpanded={false}
            >
                <div data-testid="body">body content</div>
            </LedgerHubSection>,
        );

        // Default-collapsed — body should not render.
        expect(screen.queryByTestId('body')).not.toBeInTheDocument();

        const toggle = screen.getByRole('button', {
            name: /categories/i,
            expanded: false,
        });
        fireEvent.click(toggle);

        expect(screen.getByTestId('body')).toBeInTheDocument();
    });

    it('persists collapsed state to localStorage keyed by ledger + section', () => {
        const { unmount } = render(
            <LedgerHubSection
                sectionKey="t3"
                ledgerId={LEDGER_ID}
                title="Accounts"
            >
                <div data-testid="body">x</div>
            </LedgerHubSection>,
        );

        // Collapse via click.
        fireEvent.click(screen.getByRole('button', { name: /accounts/i }));
        expect(screen.queryByTestId('body')).not.toBeInTheDocument();

        // Persisted key reflects the change.
        const stored = window.localStorage.getItem(
            `coffer.hub.${LEDGER_ID}.t3.expanded`,
        );
        expect(stored).toBe('false');

        unmount();

        // Remount — initial state hydrates from localStorage.
        render(
            <LedgerHubSection
                sectionKey="t3"
                ledgerId={LEDGER_ID}
                title="Accounts"
            >
                <div data-testid="body">x</div>
            </LedgerHubSection>,
        );

        // Still collapsed across the remount.
        expect(screen.queryByTestId('body')).not.toBeInTheDocument();
        expect(
            screen.getByRole('button', { name: /accounts/i, expanded: false }),
        ).toBeInTheDocument();
    });

    it('keys persistence per (ledger, section) — different ledgers have independent state', () => {
        // Collapse for ledger A.
        const { unmount } = render(
            <LedgerHubSection
                sectionKey="t4"
                ledgerId="ledger-A"
                title="Accounts"
            >
                <div data-testid="body">x</div>
            </LedgerHubSection>,
        );
        fireEvent.click(screen.getByRole('button', { name: /accounts/i }));
        unmount();

        // Ledger B mounts fresh — should be expanded (default), not
        // pick up ledger A's collapsed state.
        render(
            <LedgerHubSection
                sectionKey="t4"
                ledgerId="ledger-B"
                title="Accounts"
            >
                <div data-testid="body">x</div>
            </LedgerHubSection>,
        );
        expect(screen.getByTestId('body')).toBeInTheDocument();
    });

    it('renders the header action when supplied', () => {
        render(
            <LedgerHubSection
                sectionKey="t5"
                ledgerId={LEDGER_ID}
                title="Securities"
                headerAction={<a href="/x">Manage securities →</a>}
            >
                <div>body</div>
            </LedgerHubSection>,
        );

        expect(
            screen.getByRole('link', { name: /manage securities/i }),
        ).toBeInTheDocument();
    });

    it('shows the header action even when the section is collapsed', () => {
        render(
            <LedgerHubSection
                sectionKey="t6"
                ledgerId={LEDGER_ID}
                title="Securities"
                headerAction={<a href="/x">Manage securities →</a>}
                defaultExpanded={false}
            >
                <div data-testid="body">body</div>
            </LedgerHubSection>,
        );

        // Body hidden when collapsed.
        expect(screen.queryByTestId('body')).not.toBeInTheDocument();
        // But the manage link still reachable without expanding.
        expect(
            screen.getByRole('link', { name: /manage securities/i }),
        ).toBeInTheDocument();
    });
});

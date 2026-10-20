import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { AttentionDot, AttentionCount } from './AttentionDot';
import { useLedgerAttention } from '@/lib/useLedgerAttention';

// The navigation problem indicator (FU-049).
//
// Two things are worth holding down here, and they fail in opposite directions:
//   * the DOT must not appear when nothing is wrong — an indicator that is always on
//     is an indicator nobody reads;
//   * and it must carry its meaning without the colour, because red-vs-amber at 6px is
//     exactly the channel that fails for a chunk of readers.

const LEDGER_A = '00000000-0000-0000-0000-0000000000aa';
const LEDGER_B = '00000000-0000-0000-0000-0000000000bb';

describe('AttentionDot', () => {
    it('renders nothing when there is nothing wrong', () => {
        const { container } = render(
            <AttentionDot severity={null} context="Coffer" />,
        );
        // Not queryByRole → toBeNull, which an empty render satisfies whatever the
        // component does. The container being empty is the actual claim.
        expect(container).toBeEmptyDOMElement();
    });

    it('names the severity and the count in text, not only in colour', () => {
        render(<AttentionDot severity="critical" count={2} context="Household" />);

        // The sentence a screen reader gets. It has to say WHICH ledger, HOW bad and
        // HOW MANY, because the visual carries none of that on its own.
        expect(
            screen.getByText('Household needs attention: 2 critical problems outstanding'),
        ).toBeInTheDocument();
    });

    it('offers the same sentence on hover, for readers who cannot separate the hue', () => {
        const { container } = render(
            <AttentionDot severity="warning" count={1} context="Household" />,
        );

        const swatch = container.querySelector('[title]');
        expect(swatch).not.toBeNull();
        expect(swatch!.getAttribute('title')).toBe(
            'Household needs attention: 1 problem outstanding',
        );
    });

    it('distinguishes warning from critical by weight, not only by hue', () => {
        // Outline vs fill. Asserted because the hue alone does not carry it: the
        // first build used two solid state-* dots, both cleared their contrast
        // floor, and on screen they were the same dot (ADR-0102 D4). A future
        // "simplification" back to two fills should fail here.
        const warning = render(<AttentionDot severity="warning" context="A" />);
        expect(warning.container.querySelector('.border-mark-warning')).not.toBeNull();
        expect(warning.container.querySelector('.bg-mark-danger')).toBeNull();

        const critical = render(<AttentionDot severity="critical" context="B" />);
        expect(critical.container.querySelector('.bg-mark-danger')).not.toBeNull();
        expect(critical.container.querySelector('.border-mark-warning')).toBeNull();
    });

    it('drops the count from the label when there is none to give', () => {
        // The aggregate case. A number there would be a sum across ledgers, so the
        // sentence has to still read as a sentence without one.
        render(<AttentionDot severity="warning" context="Coffer" />);
        expect(
            screen.getByText('Coffer needs attention: problems outstanding'),
        ).toBeInTheDocument();
    });
});

describe('AttentionCount', () => {
    it('shows the number beside the dot where it is actionable', () => {
        render(<AttentionCount severity="warning" count={3} context="Household" />);
        expect(screen.getByText('3')).toBeInTheDocument();
    });

    it('renders nothing at all when there is nothing wrong', () => {
        const { container } = render(
            <AttentionCount severity={null} count={undefined} context="Household" />,
        );
        expect(container).toBeEmptyDOMElement();
    });
});

/**
 * The hook, exercised through the REAL api client over a stubbed `fetch`.
 *
 * Mocking `fetchLedgerAttention` itself would leave the URL, the credentials mode and
 * the JSON decode untested — every part of the path that can be wrong while each piece
 * looks right. The route this asks for is asserted for the same reason.
 */
describe('useLedgerAttention', () => {
    afterEach(() => {
        vi.unstubAllGlobals();
    });

    function stubAttention(rows: unknown) {
        const fetchMock = vi.fn().mockResolvedValue(
            new Response(JSON.stringify(rows), {
                status: 200,
                headers: { 'Content-Type': 'application/json' },
            }),
        );
        vi.stubGlobal('fetch', fetchMock);
        return fetchMock;
    }

    function Probe() {
        const attention = useLedgerAttention();
        return (
            <div>
                <span data-testid="worst">{attention.worst ?? 'none'}</span>
                <span data-testid="a">{attention.severityFor(LEDGER_A) ?? 'none'}</span>
                <span data-testid="a-count">{attention.countFor(LEDGER_A) ?? '-'}</span>
                <span data-testid="b">{attention.severityFor(LEDGER_B) ?? 'none'}</span>
            </div>
        );
    }

    function renderProbe() {
        const queryClient = new QueryClient({
            defaultOptions: { queries: { retry: false } },
        });
        return render(
            <QueryClientProvider client={queryClient}>
                <Probe />
            </QueryClientProvider>,
        );
    }

    it('asks the cross-ledger route once, whatever is on screen', async () => {
        const fetchMock = stubAttention([]);
        renderProbe();

        await waitFor(() => expect(fetchMock).toHaveBeenCalled());
        expect(String(fetchMock.mock.calls[0][0])).toContain('/api/ledgers/attention');
    });

    it('reports critical anywhere as the aggregate, not the first row', async () => {
        // Order is the trap: a naive implementation takes rows[0] and reports warning
        // for an install where something irreversible has gone wrong elsewhere.
        stubAttention([
            { ledgerId: LEDGER_A, severity: 'warning', count: 1 },
            { ledgerId: LEDGER_B, severity: 'critical', count: 2 },
        ]);
        renderProbe();

        await waitFor(() =>
            expect(screen.getByTestId('worst')).toHaveTextContent('critical'),
        );
        // Per ledger it stays per ledger — the aggregate must not bleed downward.
        expect(screen.getByTestId('a')).toHaveTextContent('warning');
        expect(screen.getByTestId('b')).toHaveTextContent('critical');
    });

    it('reports nothing for a ledger the server did not mention', async () => {
        stubAttention([{ ledgerId: LEDGER_B, severity: 'warning', count: 1 }]);
        renderProbe();

        // Anchored on B, so "A is none" is a statement about A rather than about a
        // response that never arrived.
        await waitFor(() => expect(screen.getByTestId('b')).toHaveTextContent('warning'));
        expect(screen.getByTestId('a')).toHaveTextContent('none');
        expect(screen.getByTestId('a-count')).toHaveTextContent('-');
    });

    it('draws nothing for a severity it does not recognise', async () => {
        // The wire type is an open string because the column is TEXT with a CHECK. A
        // severity added server-side and not taught to the UI must render as nothing,
        // not as whichever colour the fallback happens to be.
        stubAttention([{ ledgerId: LEDGER_A, severity: 'catastrophic', count: 9 }]);
        renderProbe();

        await waitFor(() => expect(screen.getByTestId('a')).toHaveTextContent('none'));
        expect(screen.getByTestId('worst')).toHaveTextContent('none');
        // And no orphan number: a count is only ever drawn beside a dot.
        expect(screen.getByTestId('a-count')).toHaveTextContent('-');
    });
});

import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';

import { buildTimelineSentinels } from './registerSentinels';

// The timeline sentinels are shared by both the bank and investment
// registers (ADR-0030 reuse). Virtuoso doesn't lay out its Header /
// Footer under jsdom (no measurable scroll dimensions), so we test the
// builder directly — it owns the head/tail gating + the oldest-label
// suffix, which is the load-bearing behavior.

describe('buildTimelineSentinels', () => {
    it('renders the Newest sentinel only at the timeline head', () => {
        const { Header, Footer } = buildTimelineSentinels({
            atTimelineHead: true,
            atTimelineTail: false,
            oldestLabel: null,
        });
        expect(Header).toBeDefined();
        expect(Footer).toBeUndefined();

        const HeaderComp = Header!;
        // Virtuoso's Header type carries a `context` prop it injects at
        // render time; the sentinel ignores it. Pass `undefined`
        // explicitly to satisfy the type without a cast.
        render(<HeaderComp context={undefined} />);
        expect(screen.getByText(/Newest transaction/i)).toBeInTheDocument();
    });

    it('renders the Oldest sentinel with the date label at the timeline tail', () => {
        const { Header, Footer } = buildTimelineSentinels({
            atTimelineHead: false,
            atTimelineTail: true,
            oldestLabel: 'Jan 1, 2020',
        });
        expect(Header).toBeUndefined();
        expect(Footer).toBeDefined();

        const FooterComp = Footer!;
        render(<FooterComp context={undefined} />);
        const node = screen.getByText(/Oldest transaction/i);
        expect(node).toHaveTextContent(/Oldest transaction · Jan 1, 2020/);
    });

    it('omits the date suffix when oldestLabel is null', () => {
        const { Footer } = buildTimelineSentinels({
            atTimelineHead: false,
            atTimelineTail: true,
            oldestLabel: null,
        });
        const FooterComp = Footer!;
        render(<FooterComp context={undefined} />);
        const node = screen.getByText(/Oldest transaction/i);
        expect(node.textContent).not.toMatch(/·/);
    });

    it('renders neither sentinel when not at an edge', () => {
        const { Header, Footer } = buildTimelineSentinels({
            atTimelineHead: false,
            atTimelineTail: false,
            oldestLabel: 'Jan 1, 2020',
        });
        expect(Header).toBeUndefined();
        expect(Footer).toBeUndefined();
    });
});

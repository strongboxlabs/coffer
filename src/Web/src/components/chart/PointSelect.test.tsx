import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { PointSelect } from './PointSelect';

function Harness({ initial = null }: { initial?: number | null }) {
    const [selected, setSelected] = useState<number | null>(initial);
    return (
        <div>
            <PointSelect
                label="Month-ends"
                labels={['Jan 31: $1', 'Feb 28: $2', 'Mar 31: $3']}
                selected={selected}
                onSelect={setSelected}
            />
            <p data-testid="readout">{selected === null ? 'none' : String(selected)}</p>
        </div>
    );
}

describe('PointSelect', () => {
    it('puts one point in the tab order, the selected one or else the last', () => {
        render(<Harness />);
        const buttons = screen.getAllByRole('button');
        expect(buttons.map((b) => b.tabIndex)).toEqual([-1, -1, 0]);
    });

    it('moves focus with the arrow keys and selects with Enter', async () => {
        const user = userEvent.setup();
        render(<Harness />);

        await user.tab();
        expect(screen.getByRole('button', { name: 'Mar 31: $3' })).toHaveFocus();
        await user.keyboard('{ArrowLeft}');
        expect(screen.getByRole('button', { name: 'Feb 28: $2' })).toHaveFocus();
        // Moving focus selects nothing.
        expect(screen.getByTestId('readout')).toHaveTextContent('none');

        await user.keyboard('{Enter}');
        expect(screen.getByTestId('readout')).toHaveTextContent('1');
        expect(screen.getByRole('button', { name: 'Feb 28: $2' })).toHaveAttribute('aria-pressed', 'true');

        // The ends hold rather than wrap.
        await user.keyboard('{ArrowLeft}{ArrowLeft}{ArrowLeft}');
        expect(screen.getByRole('button', { name: 'Jan 31: $1' })).toHaveFocus();
    });

    it('selects on a click, as a touch does', async () => {
        const user = userEvent.setup();
        render(<Harness />);
        await user.click(screen.getByRole('button', { name: 'Jan 31: $1' }));
        expect(screen.getByTestId('readout')).toHaveTextContent('0');
    });
});

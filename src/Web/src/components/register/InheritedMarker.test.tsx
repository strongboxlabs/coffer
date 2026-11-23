import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';

import { InheritedMarker, InheritedText } from './InheritedMarker';

describe('InheritedMarker', () => {
    it('names the account the value comes from', () => {
        render(<InheritedMarker from="Checking" />);
        expect(screen.getByRole('img', { name: 'From Checking' })).toBeInTheDocument();
    });

    it('falls back to "the other side" when the account is not known', () => {
        render(<InheritedMarker from={null} />);
        expect(screen.getByRole('img', { name: 'From the other side' })).toBeInTheDocument();
    });
});

describe('InheritedText', () => {
    it('draws the marker before an inherited value, in the subtle colour', () => {
        render(<InheritedText inherited from="Checking">Team coffee</InheritedText>);
        const text = screen.getByText('Team coffee');
        expect(screen.getByRole('img', { name: 'From Checking' })).toBeInTheDocument();
        expect(text.closest('[data-inherited="true"]')).toHaveClass('text-text-subtle');
    });

    it('renders an own value as plain text, with no marker', () => {
        render(
            <div data-testid="cell">
                <InheritedText inherited={false} from="Checking">Client lunch</InheritedText>
            </div>,
        );
        expect(screen.getByTestId('cell')).toHaveTextContent('Client lunch');
        expect(screen.queryByRole('img', { name: /^From / })).not.toBeInTheDocument();
    });
});

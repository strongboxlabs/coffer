import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';

import { ProvenanceIcon } from './ProvenanceIcon';

/**
 * The provenance icon's hue: a file import is drawn neutral, like a manual
 * row, because amber only ever means a warning. An online import keeps its
 * muted accent.
 */

const glyphOf = (label: string) => screen.getByLabelText(label).querySelector('svg')!;

describe('ProvenanceIcon', () => {
    it('draws a file import neutral, not amber', () => {
        render(<ProvenanceIcon origin="file_import" providerKey="ofx" isMergeWinner={false} />);

        const glyph = glyphOf('File import · OFX');
        expect(glyph).toHaveClass('text-text-subtle');
        expect(glyph.getAttribute('class')).not.toMatch(/state-warning/);
    });

    it('names a CSV file import by its reader, not by its raw key', () => {
        render(
            <>
                <ProvenanceIcon origin="file_import" providerKey="csv-generic" isMergeWinner={false} />
                <ProvenanceIcon origin="file_import" providerKey="csv-fidelity" isMergeWinner={false} />
            </>,
        );

        expect(screen.getByLabelText('File import · CSV')).toBeInTheDocument();
        expect(screen.getByLabelText('File import · Fidelity CSV')).toBeInTheDocument();
    });

    it('keeps an online import\'s accent and a manual row\'s neutral', () => {
        render(
            <>
                <ProvenanceIcon origin="online_import" providerKey="simplefin" isMergeWinner={false} />
                <ProvenanceIcon origin="manual" providerKey={null} isMergeWinner={false} />
            </>,
        );

        expect(glyphOf('Online import · SimpleFIN')).toHaveClass('text-accent/70');
        expect(glyphOf('Manual entry')).toHaveClass('text-text-subtle');
    });
});

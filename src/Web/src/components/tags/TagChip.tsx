import { Chip } from '@/components/ui/Chip';
import { tagChipStyle } from '@/lib/tagPalette';

import { useTagColor } from './TagColorsContext';

/**
 * A register tag chip, coloured from the ledger's tag palette via
 * {@link useTagColor}. A tag with a colour renders as a translucent tint
 * of its hex (inline style wins over the Chip's default classes); an
 * uncoloured tag (or one rendered outside a {@link TagColorsProvider})
 * falls back to the theme's default gray Chip.
 */
export function TagChip({ name }: { name: string }) {
    const color = useTagColor(name);
    return (
        <Chip variant="default" style={tagChipStyle(color)}>
            {name}
        </Chip>
    );
}

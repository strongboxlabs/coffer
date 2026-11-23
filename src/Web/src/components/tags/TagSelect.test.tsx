import { describe, expect, it, vi } from 'vitest';
import { createEvent, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { Modal } from '@/components/ui/Modal';
import type { TagDto } from '@/lib/types';
import { TagSelect } from './TagSelect';

function dict(names: readonly string[]): TagDto[] {
    return names.map((name, i) => ({ id: `t${i}`, name, color: null, usageCount: 1, transactionCount: 1 }));
}

function open() {
    fireEvent.click(screen.getByRole('button', { name: 'Edit Tags' }));
}

describe('TagSelect while this side inherits', () => {
    it('shows the inherited tags with the marker, not the empty placeholder', () => {
        render(
            <TagSelect
                tags={[]}
                placeholderTags={['Work']}
                placeholderFrom="Checking"
                allTags={dict(['Work', 'Travel'])}
                onChange={() => {}}
                disabled={false}
                aria-label="Tags"
            />,
        );
        expect(screen.getByRole('img', { name: 'From Checking' })).toBeInTheDocument();
        expect(screen.getByTitle('Work')).toHaveAttribute('data-inherited', 'true');
        expect(screen.queryByText('Tags (optional)')).not.toBeInTheDocument();
    });

    it('a pick starts the own set from the inherited set', () => {
        const onChange = vi.fn();
        render(
            <TagSelect
                tags={[]}
                placeholderTags={['Work']}
                allTags={dict(['Work', 'Travel'])}
                onChange={onChange}
                disabled={false}
                aria-label="Tags"
            />,
        );
        open();
        // The inherited tag reads as applied in the list.
        expect(screen.getByRole('checkbox', { name: 'Work' })).toBeChecked();
        fireEvent.click(screen.getByRole('checkbox', { name: 'Travel' }));
        expect(onChange).toHaveBeenCalledWith(['Work', 'Travel']);
    });

    it('unpicking an inherited tag keeps the rest of the inherited set', () => {
        const onChange = vi.fn();
        render(
            <TagSelect
                tags={[]}
                placeholderTags={['Work', 'Travel']}
                allTags={dict(['Work', 'Travel'])}
                onChange={onChange}
                disabled={false}
                aria-label="Tags"
            />,
        );
        open();
        fireEvent.click(screen.getByRole('checkbox', { name: 'Work' }));
        expect(onChange).toHaveBeenCalledWith(['Travel']);
    });

    it('an own set replaces the inherited one in the strip', () => {
        render(
            <TagSelect
                tags={['Reimbursable']}
                placeholderTags={['Work']}
                allTags={dict(['Work', 'Reimbursable'])}
                onChange={() => {}}
                disabled={false}
                aria-label="Tags"
            />,
        );
        expect(screen.getByTitle('Reimbursable')).not.toHaveAttribute('data-inherited');
        expect(screen.queryByTitle('Work')).not.toBeInTheDocument();
    });
});

describe('TagSelect strip', () => {
    it('reads "Tags (optional)" while the set is empty and nothing is inherited', () => {
        render(
            <TagSelect tags={[]} allTags={dict(['Work'])} onChange={() => {}} disabled={false} aria-label="Tags" />,
        );
        expect(screen.getByLabelText('Tags', { selector: 'span' })).toHaveTextContent(/^Tags \(optional\)$/);
    });

    // The memo input beside this control carries no colour of its own, so it
    // renders in the theme's text colour; own tag names match it.
    it('shows own names in the full text colour, as the memo shows its text', () => {
        render(
            <TagSelect tags={['Work']} allTags={dict(['Work'])} onChange={() => {}} disabled={false} aria-label="Tags" />,
        );
        const names = screen.getByTitle('Work').querySelector('.truncate')!;
        expect(names).toHaveClass('text-text');
        expect(names).not.toHaveClass('text-text-muted');
    });

    it('keeps inherited names in the subtle colour, behind the marker', () => {
        render(
            <TagSelect
                tags={[]}
                placeholderTags={['Work']}
                placeholderFrom="Checking"
                allTags={dict(['Work'])}
                onChange={() => {}}
                disabled={false}
                aria-label="Tags"
            />,
        );
        const names = screen.getByTitle('Work').querySelector('.truncate')!;
        expect(names).toHaveClass('text-text-subtle');
        expect(names).not.toHaveClass('text-text');
        expect(screen.getByRole('img', { name: 'From Checking' })).toBeInTheDocument();
    });
});

describe('TagSelect tag limit', () => {
    const many = Array.from({ length: 25 }, (_, i) => `tag-${String(i).padStart(2, '0')}`);

    it('refuses a 21st tag on a set at the limit', () => {
        const twenty = many.slice(0, 20);
        render(
            <TagSelect
                tags={twenty}
                allTags={dict(many)}
                onChange={() => {}}
                disabled={false}
                aria-label="Tags"
            />,
        );
        open();
        expect(screen.getByRole('checkbox', { name: 'tag-20' })).toBeDisabled();
        expect(screen.getByRole('checkbox', { name: 'tag-00' })).toBeEnabled();
    });

    it('lets an over-limit set swap one tag for another but not grow', () => {
        const onChange = vi.fn();
        const stored = many.slice(0, 22);
        const { rerender } = render(
            <TagSelect
                tags={stored}
                allTags={dict([...many, 'extra'])}
                onChange={onChange}
                disabled={false}
                maxCount={22}
                aria-label="Tags"
            />,
        );
        open();
        // At its stored size: adding is refused.
        expect(screen.getByRole('checkbox', { name: 'extra' })).toBeDisabled();
        // Removing one is allowed...
        fireEvent.click(screen.getByRole('checkbox', { name: 'tag-00' }));
        expect(onChange).toHaveBeenLastCalledWith(stored.slice(1));
        // ...and with one removed, one may be added back: a same-size swap.
        rerender(
            <TagSelect
                tags={stored.slice(1)}
                allTags={dict([...many, 'extra'])}
                onChange={onChange}
                disabled={false}
                maxCount={22}
                aria-label="Tags"
            />,
        );
        expect(screen.getByRole('checkbox', { name: 'extra' })).toBeEnabled();
    });
});

describe('TagSelect opening', () => {
    it('puts focus in the filter, so typing filters the list', () => {
        render(
            <TagSelect
                tags={[]}
                allTags={dict(['Work', 'Travel'])}
                onChange={() => {}}
                disabled={false}
                aria-label="Tags"
            />,
        );
        open();
        const filter = screen.getByLabelText('Filter tags');
        expect(screen.getByRole('checkbox', { name: 'Travel' })).toBeInTheDocument();
        expect(filter).toHaveFocus();
    });
});

describe('TagSelect Escape', () => {
    // The editors' one rule for a list: with it open, Escape closes the list
    // and goes no further, marked handled; with it closed, Escape is the
    // editor's.
    function renderInParent() {
        const parentKeys: { key: string; defaultPrevented: boolean }[] = [];
        render(
            <div onKeyDown={(e) => parentKeys.push({ key: e.key, defaultPrevented: e.defaultPrevented })}>
                <TagSelect
                    tags={[]}
                    allTags={dict(['Work', 'Travel'])}
                    onChange={() => {}}
                    disabled={false}
                    aria-label="Tags"
                />
            </div>,
        );
        return { parentKeys };
    }

    it('closes the open list, returns focus to its button, and stops there', () => {
        const { parentKeys } = renderInParent();
        open();
        const filter = screen.getByLabelText('Filter tags');
        fireEvent.keyDown(filter, { key: 'a' });

        const escape = createEvent.keyDown(filter, { key: 'Escape' });
        fireEvent(filter, escape);

        expect(screen.queryByLabelText('Filter tags')).toBeNull();
        expect(screen.getByRole('button', { name: 'Edit Tags' })).toHaveFocus();
        expect(escape.defaultPrevented).toBe(true);
        // The parent saw the other key, not this Escape.
        expect(parentKeys.map((k) => k.key)).toEqual(['a']);
    });

    it('closes the open list on an Escape from its button too', () => {
        const { parentKeys } = renderInParent();
        open();
        expect(screen.getByLabelText('Filter tags')).toBeInTheDocument();
        const button = screen.getByRole('button', { name: 'Edit Tags' });

        const escape = createEvent.keyDown(button, { key: 'Escape' });
        fireEvent(button, escape);

        expect(screen.queryByLabelText('Filter tags')).toBeNull();
        expect(escape.defaultPrevented).toBe(true);
        expect(parentKeys).toEqual([]);
    });

    it('leaves an Escape with the list closed to the editor', () => {
        const { parentKeys } = renderInParent();
        const button = screen.getByRole('button', { name: 'Edit Tags' });
        expect(button).toHaveAttribute('aria-expanded', 'false');

        fireEvent.keyDown(button, { key: 'Escape' });

        expect(parentKeys).toEqual([{ key: 'Escape', defaultPrevented: false }]);
    });
});

describe('TagSelect when focus leaves it', () => {
    // A list left open after focus moves on is still on screen, but the
    // Escape that should close it lands on the other field and reaches the
    // editor, which cancels the edit. Focus leaving the control closes it.
    function renderBetweenFields(onChange: (next: readonly string[]) => void = () => {}) {
        const parentEscapes: { defaultPrevented: boolean }[] = [];
        render(
            <div
                onKeyDown={(e) => {
                    if (e.key === 'Escape') parentEscapes.push({ defaultPrevented: e.defaultPrevented });
                }}
            >
                <input aria-label="Before" />
                <TagSelect
                    tags={[]}
                    allTags={dict(['Work', 'Travel'])}
                    onChange={onChange}
                    disabled={false}
                    aria-label="Tags"
                />
                <input aria-label="After" />
            </div>,
        );
        return { parentEscapes };
    }

    it('closes the list when Shift+Tab leaves it, so the next Escape is the editor\'s', async () => {
        const user = userEvent.setup();
        const { parentEscapes } = renderBetweenFields();
        const button = screen.getByRole('button', { name: 'Edit Tags' });
        await user.click(button);
        expect(screen.getByLabelText('Filter tags')).toHaveFocus();

        // Back to the button: still inside the control, so the list stays.
        await user.tab({ shift: true });
        expect(button).toHaveFocus();
        expect(screen.getByLabelText('Filter tags')).toBeInTheDocument();

        await user.tab({ shift: true });
        expect(screen.getByLabelText('Before')).toHaveFocus();
        expect(screen.queryByLabelText('Filter tags')).toBeNull();
        expect(button).toHaveAttribute('aria-expanded', 'false');

        await user.keyboard('{Escape}');
        expect(parentEscapes).toEqual([{ defaultPrevented: false }]);
    });

    it('closes the list when Tab moves past its last tag', async () => {
        const user = userEvent.setup();
        renderBetweenFields();
        await user.click(screen.getByRole('button', { name: 'Edit Tags' }));

        await user.tab();
        await user.tab();
        expect(screen.getByRole('checkbox', { name: 'Work' })).toHaveFocus();

        await user.tab();
        expect(screen.getByLabelText('After')).toHaveFocus();
        expect(screen.queryByLabelText('Filter tags')).toBeNull();
    });

    it('stays open when a click on a tag\'s name toggles it', async () => {
        // The name is not focusable. The press keeps focus in the control,
        // and the click reaches the tag's label and toggles it.
        const user = userEvent.setup();
        const onChange = vi.fn();
        renderBetweenFields(onChange);
        await user.click(screen.getByRole('button', { name: 'Edit Tags' }));
        expect(screen.getByLabelText('Filter tags')).toHaveFocus();

        await user.click(screen.getByTitle('Travel'));

        expect(onChange).toHaveBeenCalledWith(['Travel']);
        expect(screen.getByLabelText('Filter tags')).toBeInTheDocument();
    });
});

describe('TagSelect inside a dialog', () => {
    // The dialog's panel can take focus, so a press on a tag's name moves focus
    // to the panel rather than to nothing. That is still a press inside the
    // control: closing there unmounted the list before the click toggled the
    // tag, and left focus on the panel for the next Escape to close the dialog.
    function renderInDialog(onChange: (next: readonly string[]) => void = () => {}) {
        const onClose = vi.fn();
        render(
            <Modal open onClose={onClose} ariaLabel="Dialog">
                <TagSelect
                    tags={[]}
                    allTags={dict(['Work', 'Travel'])}
                    onChange={onChange}
                    disabled={false}
                    aria-label="Tags"
                />
            </Modal>,
        );
        return { onClose };
    }

    it('toggles a tag on a click on its name, and its Escape stays the list\'s', async () => {
        const user = userEvent.setup();
        const onChange = vi.fn();
        const { onClose } = renderInDialog(onChange);
        await user.click(screen.getByRole('button', { name: 'Edit Tags' }));
        expect(screen.getByLabelText('Filter tags')).toHaveFocus();

        await user.click(screen.getByTitle('Travel'));

        expect(onChange).toHaveBeenCalledWith(['Travel']);
        expect(screen.getByLabelText('Filter tags')).toBeInTheDocument();

        await user.keyboard('{Escape}');
        expect(screen.queryByLabelText('Filter tags')).toBeNull();
        expect(onClose).not.toHaveBeenCalled();

        await user.keyboard('{Escape}');
        expect(onClose).toHaveBeenCalledTimes(1);
    });

    it('keeps focus in the filter on a press on the list\'s padding, so its Escape stays the list\'s', async () => {
        // No label here to hand focus back on the click, so a press that moved
        // focus to the dialog's panel would leave it there, list open, for the
        // next Escape to close the dialog.
        const user = userEvent.setup();
        const { onClose } = renderInDialog();
        await user.click(screen.getByRole('button', { name: 'Edit Tags' }));
        const filter = screen.getByLabelText('Filter tags');
        expect(filter).toHaveFocus();

        await user.click(screen.getByRole('group', { name: 'Tags' }));

        expect(filter).toHaveFocus();

        await user.keyboard('{Escape}');
        expect(screen.queryByLabelText('Filter tags')).toBeNull();
        expect(onClose).not.toHaveBeenCalled();

        await user.keyboard('{Escape}');
        expect(onClose).toHaveBeenCalledTimes(1);
    });
});

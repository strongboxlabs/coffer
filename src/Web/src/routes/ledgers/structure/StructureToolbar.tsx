import type { ReactNode, Ref } from 'react';
import { Plus, Search } from 'lucide-react';

import { Button } from '@/components/ui/Button';
import { Checkbox } from '@/components/ui/Checkbox';
import { Input } from '@/components/ui/Input';

// The strip above every list of Structure (ADR-0111): search, "Show inactive" where the
// list has an inactive state, anything particular to the list, and the one New button.
// It is the same on every tab, so a reader who has used one has used all three.

export function StructureToolbar({
    noun,
    query,
    onQuery,
    showInactive,
    onShowInactive,
    createLabel,
    onCreate,
    createRef,
    disabled,
    pending,
    children,
}: {
    /** What the list holds, plural, as the search box names it: "accounts". */
    noun: string;
    query: string;
    onQuery: (next: string) => void;
    /** Both, or neither: a list with nothing inactive (tags) leaves the checkbox out. */
    showInactive?: boolean;
    onShowInactive?: (next: boolean) => void;
    /** Both, or neither: a reader who cannot edit is not offered New. */
    createLabel?: string;
    onCreate?: () => void;
    /** New's ref: where focus goes when the list's last row is taken away (useRowRemovalFocus). */
    createRef?: Ref<HTMLButtonElement>;
    /** Greys the controls: the list has failed to load. */
    disabled?: boolean;
    /** The list is still loading: the fields and New are pending, so each
     *  keeps focus and refuses input (pending.ts). */
    pending?: boolean;
    /** Actions particular to one list, ahead of New: tags' "Remove unused". */
    children?: ReactNode;
}) {
    return (
        <div className="flex flex-wrap items-center gap-3">
            <div className="relative w-fixed-288px max-w-full">
                <Search
                    aria-hidden
                    className="pointer-events-none absolute left-2.5 top-1/2 size-icon-sm -translate-y-1/2 text-text-subtle"
                />
                <Input
                    type="search"
                    aria-label={`Search ${noun}`}
                    placeholder={`Search ${noun}`}
                    value={query}
                    disabled={disabled}
                    pending={pending}
                    onChange={(e) => onQuery(e.target.value)}
                    className="h-control-36px pl-8"
                />
            </div>
            {onShowInactive !== undefined ? (
                <Checkbox
                    label="Show inactive"
                    checked={showInactive ?? false}
                    disabled={disabled}
                    pending={pending}
                    onChange={(e) => onShowInactive(e.target.checked)}
                    wrapperClassName="text-xs text-text-muted"
                />
            ) : null}
            <div className="ml-auto flex items-center gap-3">
                {children}
                {onCreate !== undefined && createLabel !== undefined ? (
                    <Button ref={createRef} type="button" variant="primary" size="sm" onClick={onCreate} disabled={disabled}
                        pending={pending}>
                        <Plus aria-hidden className="size-icon-sm" />
                        {createLabel}
                    </Button>
                ) : null}
            </div>
        </div>
    );
}

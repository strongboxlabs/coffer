import { useEffect, useRef } from 'react';

import { ActionFooter } from '@/components/ui/ActionFooter';

// What a dialog shows in place of its form when a save it made left mirrored transfers
// across currencies, so each now keeps two amounts (ADR-0114 D13; the count is the
// response's `becameTwoAmount`). The dialog stays open on this line and a single Done,
// which closes it, as Escape and the dialog's close do (decided by the owner, 2026-11-18).
// The save's own work (refresh, selection) is done by then; Done only closes. Shared by
// the account editor's currency change and the register's Move dialog.

export interface TwoAmountDoneProps {
    /** How many transfers now keep two amounts; the host shows this only when above 0. */
    count: number;
    onDone: () => void;
}

export function TwoAmountDone({ count, onDone }: TwoAmountDoneProps) {
    const doneRef = useRef<HTMLButtonElement | null>(null);
    // The control that saved (Save, Move) is gone with the form, so focus goes to Done.
    useEffect(() => {
        doneRef.current?.focus();
    }, []);
    const text = count === 1
        ? "1 transfer now keeps two amounts, one in each account's currency."
        : `${count} transfers now keep two amounts, one in each account's currency.`;
    return (
        <div className="flex flex-col gap-4">
            <p role="status" className="text-sm text-text">{text}</p>
            <ActionFooter confirm={{ label: 'Done', onClick: onDone }} confirmRef={doneRef} />
        </div>
    );
}

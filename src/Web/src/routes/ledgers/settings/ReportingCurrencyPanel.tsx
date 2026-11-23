import { useId, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';

import { updateLedger } from '@/lib/api';
import { currencyChoices } from '@/lib/currencies';
import { errorMessage } from '@/lib/errorMessage';
import { useMutation } from '@/lib/mutation';
import { invalidateConvertedReads } from '@/lib/registerInvalidation';
import { useLedgerRole } from '@/lib/useLedgerRole';
import { Button } from '@/components/ui/Button';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel';
import { pendingChoiceProps } from '@/components/ui/pending';

/** What the reporting currency is and what changing it does (ADR-0114 D7, D9, D12). */
function Help({ id }: { id?: string }) {
    return (
        <div id={id} className="max-w-prose space-y-2 text-sm text-text-muted">
            <p>
                Totals that combine accounts are shown in this currency: net worth, the
                Overview, category and payee totals, budget totals, report totals, and the
                ledger totals AI connectors read. Each account&apos;s own figures stay in that
                account&apos;s currency: its register, its balance, and its own line in
                Returns. A holding priced in another currency is valued in its account&apos;s
                currency.
            </p>
            <p>
                A combined total is converted at the rate for the day it describes: a
                balance at that day&apos;s rate, a transaction at its date, today&apos;s value at
                the latest rate. Where a rate for that day is missing, the total is left
                blank and says which pair and day. Changing the currency rewrites nothing:
                every figure is read again in the new one.
            </p>
        </div>
    );
}

/**
 * Settings → General: the ledger's reporting currency (ADR-0114 D7). Only an owner
 * changes it, as with the name: a field, then Change currency, disabled until the
 * value differs from the stored one, and no success notice. Anyone else reads the
 * currency as text, with the same help.
 */
export function ReportingCurrencyPanel({ ledgerId }: { ledgerId: string }) {
    const queryClient = useQueryClient();
    const { ledger, isOwner } = useLedgerRole(ledgerId);
    const fieldId = useId();
    const helpId = useId();
    const stored = ledger?.currencyCode ?? '';
    const [picked, setPicked] = useState<string | null>(null);
    const value = picked ?? stored;
    // A change that lands leaves the value equal to the stored one, which disables
    // Change currency with focus on it; focus goes to the field (ADR-0023 §L).
    const fieldRef = useRef<HTMLSelectElement>(null);

    const mutation = useMutation({
        mutationFn: (currencyCode: string) => updateLedger(ledgerId, { currencyCode }),
        onSuccess: async () => {
            // Every converted figure reads again in the new currency, and the server
            // starts fetching the rates it needs (ADR-0114 D11).
            invalidateConvertedReads(queryClient, ledgerId);
            await queryClient.invalidateQueries({ queryKey: ['ledgers'] });
            setPicked(null);
        },
    });

    return (
        <Panel>
            <PanelHead>
                <span className="font-medium">Reporting currency</span>
            </PanelHead>
            {isOwner ? (
                <PanelBody className="space-y-3">
                    <div>
                        <FieldLabel htmlFor={fieldId}>Currency</FieldLabel>
                        <select
                            ref={fieldRef}
                            id={fieldId}
                            aria-describedby={helpId}
                            value={value}
                            // A pick made while the change saves would be thrown away when it
                            // lands, so the field refuses input until then (ADR-0023 §L).
                            {...pendingChoiceProps<HTMLSelectElement>(mutation.isPending, {
                                onChange: (e) => setPicked(e.target.value),
                            })}
                            className="mt-1 flex h-control-40px w-fixed-288px rounded-md border border-border bg-surface px-3 py-2 text-sm text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:ring-accent aria-disabled:cursor-not-allowed aria-disabled:opacity-50"
                        >
                            {currencyChoices(value).map((c) => <option key={c} value={c}>{c}</option>)}
                        </select>
                    </div>
                    <Help id={helpId} />
                    {mutation.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(mutation.error, 'Could not change the reporting currency.')}
                        </p>
                    ) : null}
                    <div className="flex items-center justify-between gap-3">
                        <p className="text-xs text-text-subtle">Only an owner can change the reporting currency.</p>
                        <Button
                            type="button"
                            variant="secondary"
                            disabled={ledger === undefined || value === stored}
                            pending={mutation.isPending}
                            fallbackFocusRef={fieldRef}
                            onClick={() => mutation.mutate(value)}
                            pendingLabel="Saving…"
                        >
                            Change currency
                        </Button>
                    </div>
                </PanelBody>
            ) : (
                // The currency, read: nothing to change it with.
                <PanelBody className="space-y-3">
                    <p className="text-sm" data-reporting-currency>{stored}</p>
                    <Help />
                    <p className="text-xs text-text-subtle">Only an owner can change the reporting currency.</p>
                </PanelBody>
            )}
        </Panel>
    );
}

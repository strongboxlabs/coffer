import { ApiError } from '@/lib/api';
import { missingLegsSentences } from '@/lib/conversion';
import { secondAmountRefusal, type SecondAmountSide } from '@/lib/secondAmount';

// The words an editor shows a refused save in (ADR-0114 D13, D15). A host hands the
// editor the server's refusal whole, so the editor, which knows the second amount it
// shows, can say some refusals better than the server's detail does.

/** A save's failure as a host hands it to the bank editor: a server refusal kept
 *  whole, anything else as words. */
export function saveRefusal(error: unknown): string | ApiError {
    if (error instanceof ApiError) return error;
    if (error instanceof Error && error.message.length > 0) return error.message;
    return 'Save failed.';
}

/**
 * The bank editor's words for a refused save. A missing or zero second amount on the
 * one second amount the editor shows reads as the field's own sentence (board ED-4),
 * naming the currency and the account (ADR-0114 D13); every other refusal shows the
 * server's detail, which names what it refused (a figure finer than its currency names
 * the currency and its places, ADR-0114 D15).
 */
export function saveErrorWords(
    error: string | ApiError,
    sides: readonly (SecondAmountSide | null)[],
): string {
    if (typeof error === 'string') return error;
    const asked = secondAmountRefusal(error.code, sides);
    return asked === null ? error.detail : `${asked}.`;
}

/**
 * The investment editor's words for a refused save. A missing or zero second amount
 * reads as the field's own sentence (board OT-1), naming the currency and the account;
 * a share move between brokerages in different currencies with a lot that has no rate
 * names each missing stored leg and its day, as the rate line does (ADR-0114 D13); every
 * other refusal shows the server's detail, as the bank editor's does.
 */
export function investmentSaveErrorWords(err: unknown, transferSide: SecondAmountSide | null): string {
    if (!(err instanceof ApiError)) return 'Save failed.';
    const asked = secondAmountRefusal(err.code, [transferSide]);
    if (asked !== null) return asked;
    if (err.code === 'rate-unavailable' && err.rateUnavailableLegs.length > 0) {
        return missingLegsSentences(err.rateUnavailableLegs);
    }
    return err.detail;
}

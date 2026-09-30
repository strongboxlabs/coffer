import { describe, it, expect } from 'vitest';

import {
    backupPartCount,
    orderBackupParts,
    parseBackupPartName,
    BACKUP_PART_SIZE_FALLBACK,
} from './backup';

// Multi-part restore (ADR-0101). A backup over the configured part size is
// several files, and the restore form takes them back exactly as they were
// downloaded. Everything here runs BEFORE a byte is uploaded, which is the
// point: a set assembled wrong produces an archive that fails to decrypt, and
// that failure is indistinguishable from a wrong passphrase — on the one day an
// operator has no patience for being sent to the wrong place.

/** A File whose reported size we control — Blob size is otherwise its content. */
function sized(name: string, size: number): File {
    const f = new File([''], name);
    Object.defineProperty(f, 'size', { value: size });
    return f;
}

describe('backupPartCount', () => {
    const P = BACKUP_PART_SIZE_FALLBACK;

    it('is 1 up to and including the part size, then grows', () => {
        expect(backupPartCount(0, P)).toBe(1);
        expect(backupPartCount(P, P)).toBe(1);
        expect(backupPartCount(P + 1, P)).toBe(2);
        expect(backupPartCount(P * 2, P)).toBe(2);
        // 141 MB — the artifact that could not be restored through a CDN at all.
        expect(backupPartCount(141_000_000, P)).toBe(3);
    });

    it('tracks the part size it is given, not a built-in one', () => {
        // Api:Backup:PartSizeMb is configurable (ADR-0101), and the whole point
        // of lowering it is that the browser then cuts smaller pieces. A count
        // computed from a hardcoded size would ignore the setting silently.
        expect(backupPartCount(10_000_000, 1024 * 1024)).toBe(10);
        expect(backupPartCount(10_000_000, 100 * 1024 * 1024)).toBe(1);
    });
});

describe('parseBackupPartName', () => {
    it('reads the part and count out of a part filename', () => {
        expect(parseBackupPartName('coffer-20260929T031500000Z-0a1b2c3d.cofferbak.002-of-003'))
            .toEqual({ part: 2, partCount: 3 });
    });

    it('returns null for a whole backup, so one file is never mistaken for a set', () => {
        expect(parseBackupPartName('coffer-20260929T031500000Z-0a1b2c3d.cofferbak')).toBeNull();
        expect(parseBackupPartName('db.cofferbak')).toBeNull();
        // A partial suffix, a wrong width, or a trailing copy marker are all NOT
        // parts — matching them loosely would let a renamed file into the set.
        expect(parseBackupPartName('db.cofferbak.2-of-3')).toBeNull();
        expect(parseBackupPartName('db.cofferbak.002-of-003 (1)')).toBeNull();
    });
});

describe('orderBackupParts', () => {
    const set = (indices: number[], count = 3) =>
        indices.map((i) =>
            sized(
                `coffer-20260929T031500000Z-0a1b2c3d.cofferbak.${String(i).padStart(3, '0')}-of-${String(count).padStart(3, '0')}`,
                BACKUP_PART_SIZE_FALLBACK,
            ),
        );

    it('orders a complete set regardless of how the picker handed them over', () => {
        const result = orderBackupParts(set([3, 1, 2]));
        expect('parts' in result).toBe(true);
        // Order is the whole claim — a file picker sorts however it likes, and
        // concatenating in that order silently corrupts the archive.
        expect((result as { parts: File[] }).parts.map((f) => f.name)).toEqual([
            'coffer-20260929T031500000Z-0a1b2c3d.cofferbak.001-of-003',
            'coffer-20260929T031500000Z-0a1b2c3d.cofferbak.002-of-003',
            'coffer-20260929T031500000Z-0a1b2c3d.cofferbak.003-of-003',
        ]);
    });

    it('refuses an incomplete set and says how many are missing', () => {
        const result = orderBackupParts(set([1, 2]));
        expect(result).toEqual({
            error: 'This backup has 3 parts, but 2 were selected. Restoring needs all of them.',
        });
    });

    it('refuses parts from different backups', () => {
        const mixed = [...set([1], 3), ...set([1], 2)];
        expect(orderBackupParts(mixed)).toEqual({
            error: 'These parts are from different backups — pick one backup’s parts.',
        });
    });

    it('refuses a file that is not a part at all', () => {
        const result = orderBackupParts([...set([1, 2], 3), sized('notes.txt', 10)]);
        expect(result).toEqual({ error: 'notes.txt is not a backup part.' });
    });
});

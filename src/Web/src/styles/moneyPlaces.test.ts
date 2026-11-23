import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

import { describe, expect, it } from 'vitest';

import { WHOLE_TREE_SCAN_TIMEOUT_MS } from '@/lib/wholeTreeScan';

/**
 * Money is at its currency's places, through lib/money, never at a fixed two.
 *
 * ADR-0114 D15: every money figure is shown, typed and stored at its currency's own
 * ISO 4217 minor units, and the SPA has one roundToMinorUnits helper, half away from
 * zero, that replaces every fixed two-place rounding and every ×100 money site and is
 * applied to the save body too; while a currency is null nothing is rounded. ADR-0114
 * D14: every money input's step and placeholder follow its currency's places, and the
 * remaining ad-hoc currency formatters move into lib/money.
 *
 * Nothing in the markup or in a role-based test shows that a whole-unit or a
 * three-place currency was rounded at two places, so this scans every non-test source
 * file for the shapes that assume two places, and for a currency formatter built
 * outside lib/money. Most rules read one line at a time; a rule marked wholeFile reads
 * the file's whole text, so a call wrapped over several lines is seen. A match fails
 * unless the file and a fragment of the matched text are listed below with a reason. An entry whose fragment no longer matches fails too,
 * so the list cannot go stale: the editor that moves a site onto lib/money removes its
 * entry in the same change.
 */

const SRC = join(import.meta.dirname, '..');

/** The one file that may build a currency formatter. */
const MONEY_MODULE = 'lib/money.ts';

interface Rule {
    readonly name: string;
    readonly pattern: RegExp;
    /** Files the rule does not apply to at all. */
    readonly exempt?: ReadonlyArray<string>;
    /** Tested against the file's whole text, so a call wrapped over several lines is seen. */
    readonly wholeFile?: boolean;
}

const RULES: ReadonlyArray<Rule> = [
    // A rounding helper called at two places, its arguments wrapped or not; [^;] keeps a
    // match inside one statement.
    { name: 'a two-place roundTo', pattern: /\broundTo\([^;]*?,\s*2\s*\)/s, wholeFile: true },
    { name: 'a fixed two-place rounding or padding', pattern: /\.toFixed\(\s*2\s*\)/ },
    { name: 'a ×100 rounding', pattern: /\*\s*100\s*\)\s*\/\s*100\b/ },
    { name: 'a ×100 rounding', pattern: /Math\.round\([^\n]*\*\s*100\s*\)/ },
    { name: 'a one-cent step', pattern: /\bstep=\{?\s*["'`]?0\.01\b/ },
    { name: 'a two-place placeholder', pattern: /\bplaceholder=\{?\s*["'`]0\.00["'`]/ },
    { name: 'a currency formatter outside lib/money', pattern: /style:\s*["'`]currency["'`]/, exempt: [MONEY_MODULE] },
];

interface Allowed {
    readonly file: string;
    /** Text the matched line contains, so an unrelated edit to the file does not break the entry. */
    readonly fragment: string;
    readonly reason: string;
}

const NOT_MONEY = 'not money';

const ALLOWED: ReadonlyArray<Allowed> = [
    // Not money: these are percentages, byte sizes and progress, which keep their own places.
    { file: 'lib/format.ts', fragment: '(bytes / (1024 * 1024 * 1024)).toFixed(2)', reason: `${NOT_MONEY}: a size in gigabytes` },
    { file: 'routes/ledgers/HoldingsPanel.tsx', fragment: 'const shown = value.toFixed(2)', reason: `${NOT_MONEY}: a percentage` },
    { file: 'routes/ledgers/HoldingsPanel.tsx', fragment: 'return Number(value.toFixed(2))', reason: `${NOT_MONEY}: a percentage, at the places fmtPercent prints it` },
    { file: 'routes/ledgers/home/widgets/InvestmentsWidget.tsx', fragment: 'percentAsPrinted(p.percentChange).toFixed(2)', reason: `${NOT_MONEY}: a percentage` },
    { file: 'routes/ledgers/home/widgets/InvestmentsWidget.tsx', fragment: '(shown === 0 ? 0 : shown).toFixed(2)', reason: `${NOT_MONEY}: a percentage` },
    { file: 'routes/ledgers/home/widgets/InvestmentsWidget.tsx', fragment: 'Math.round(Math.abs(percent) * 100)) / 100', reason: `${NOT_MONEY}: a percentage` },
    { file: 'routes/imports/ImportLedgerPage.tsx', fragment: 'Math.round((job.completed / job.total) * 100)', reason: `${NOT_MONEY}: an import's progress` },
    { file: 'routes/landing/demoBuild.ts', fragment: 'Math.round((stepNumber / total) * 100)', reason: `${NOT_MONEY}: a Demo build's progress` },
];

function sourceFiles(dir: string): string[] {
    const out: string[] = [];
    for (const entry of readdirSync(dir)) {
        if (entry === 'node_modules' || entry === 'dist') continue;
        const full = join(dir, entry);
        if (statSync(full).isDirectory()) {
            out.push(...sourceFiles(full));
        } else if (/\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry)) {
            out.push(full);
        }
    }
    return out;
}

interface Hit {
    readonly file: string;
    readonly line: number;
    readonly rule: string;
    readonly text: string;
}

function scan(): { files: number; hits: Hit[] } {
    const files = sourceFiles(SRC);
    const hits: Hit[] = [];
    for (const full of files) {
        const file = relative(SRC, full).split(sep).join('/');
        const content = readFileSync(full, 'utf8');
        const lines = content.split(/\r?\n/);
        const applies = RULES.filter((rule) => !rule.exempt?.includes(file));
        lines.forEach((text, i) => {
            for (const rule of applies) {
                if (rule.wholeFile) continue;
                if (rule.pattern.test(text)) hits.push({ file, line: i + 1, rule: rule.name, text: text.trim() });
            }
        });
        for (const rule of applies) {
            if (!rule.wholeFile) continue;
            const all = new RegExp(rule.pattern.source, `${rule.pattern.flags.replace('g', '')}g`);
            for (const match of content.matchAll(all)) {
                const line = content.slice(0, match.index).split(/\r?\n/).length;
                hits.push({ file, line, rule: rule.name, text: match[0].replace(/\s+/g, ' ').trim() });
            }
        }
    }
    return { files: files.length, hits };
}

const isAllowed = (hit: Hit) => ALLOWED.some((a) => a.file === hit.file && hit.text.includes(a.fragment));

describe('money is at its currency\'s places, through lib/money', () => {
    it('finds no two-place money shape and no currency formatter outside lib/money, but the listed ones', () => {
        const { files, hits } = scan();

        // Guard the guard: a walk that found nothing would pass by matching nothing.
        expect(files).toBeGreaterThan(300);

        const unlisted = hits.filter((hit) => !isAllowed(hit)).map((h) => `${h.file}:${h.line} (${h.rule}): ${h.text}`);
        expect(unlisted).toEqual([]);

        const stale = ALLOWED
            .filter((a) => !hits.some((hit) => hit.file === a.file && hit.text.includes(a.fragment)))
            .map((a) => `${a.file}: ${a.fragment}`);
        expect(stale).toEqual([]);

        for (const a of ALLOWED) expect(a.reason.length).toBeGreaterThan(0);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    it('catches each shape it bans', () => {
        // The rules themselves, on lines of the shapes they ban and on near misses they
        // must leave alone, so a loosened pattern fails here and not only in review.
        const banned = [
            'amount: n.toFixed(2)',
            'Math.round(x * 100) / 100',
            'Math.round(Math.abs(x) * 100)',
            'step="0.01"',
            "step={'0.01'}",
            'placeholder="0.00"',
            "new Intl.NumberFormat(undefined, { style: 'currency', currency })",
            'roundTo(\n    a * b, 2));',
        ];
        const fine = [
            'roundTo(x, 6)',
            'value.toFixed(1)',
            'Math.round(x * 1000) / 1000',
            'step="any"',
            'placeholder="0"',
            "{ style: 'percent' }",
        ];
        const matches = (text: string) => RULES.some((rule) => rule.pattern.test(text));
        for (const text of banned) expect([text, matches(text)]).toEqual([text, true]);
        for (const text of fine) expect([text, matches(text)]).toEqual([text, false]);
    });
});

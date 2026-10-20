#!/usr/bin/env node
// =============================================================================
// moneydance-export-demo.gen.mjs — build the demo export from its curated core
// plus a synthetic household history.
//
//     node data/samples/moneydance-export-demo.gen.mjs
//
//   in:  moneydance-export-demo.base.json   (hand-curated, never written here)
//   out: moneydance-export-demo.json        (embedded in the API, FU-009)
// =============================================================================
//
// WHY A BASE FILE RATHER THAN ONE SELF-CONTAINED GENERATOR. The base carries
// three things that are expensive to re-emit and cheap to break: the curated
// 103-category tree that ADR-0091 made the single source of truth for every NEW
// ledger's starter categories (`starter-categories.gen.mjs` projects it, and
// `StarterCategoryParityTests` pins the two together); the MD-native investment
// transactions whose action mix `DemoSampleImportTests` asserts is closed over
// ADR-0027's nine actions; and the `csnap` price rows that ADR-0084's rank gate
// needs in order to prove a trade overwrites an import seed. Regenerating those
// from JavaScript would risk silent drift in three places to gain nothing — the
// new material is the HISTORY, so that is all this file writes.
//
// WHAT WAS MISSING. The base is 23 transactions across ten days, every one of
// them `stat: " "`, and only one of them touches a bank account. So it exercised
// neither reconciliation nor anything a spending or cash-flow report reads: a
// reports slice tested against it would have passed while being wrong for every
// real ledger (FU-009). It also carried no `ol.orig-payee` / `ol.orig-memo` at
// all, which left FU-024 — the entry about those fields being dropped on import
// — with no data to work against.
//
// NOTHING REAL IS READ. Every payee, memo and amount below is invented. The
// format is known from the importer's own types (`MdTxn.From`, `MdAcct.From`)
// and from the two samples already here, so no real export is needed to get the
// shapes right, and no file carrying anyone's finances enters the process.
//
// THE ENCODING, verified against `TransactionMapper`:
//   * every value is a STRING, including dates and amounts;
//   * `{N}.pamt` is the amount on the transaction's OWN account (the origin
//     leg), `{N}.samt` the amount on the split's account (the counterpart leg) —
//     opposite signs, in minor units;
//   * `dt` / `td` are yyyymmdd; `ts` / `dtentered` are epoch milliseconds;
//   * a bank row sets `xfer_type: "xfrtp_bank"` and NO `invest.txntype`:
//     `MdTxn.IsInvestmentShape` routes on those two, and setting the txntype
//     would send a grocery shop into the investment mapper;
//   * the parent's `stat` seeds the origin leg's reconciliation and each split's
//     own `stat` seeds its counterpart's, so a transfer cleared on one side only
//     is expressed by setting one of them (ADR-0082);
//   * `{N}.desc` becomes a per-leg memo ONLY on multi-split rows — the mapper
//     drops it on single-split ones, where MD echoes the payee into it.
// =============================================================================

import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const HERE = dirname(fileURLToPath(import.meta.url));
const BASE_PATH = join(HERE, 'moneydance-export-demo.base.json');
const OUT_PATH = join(HERE, 'moneydance-export-demo.json');

// --- determinism -------------------------------------------------------------
// A fixed seed, so re-running produces a byte-identical file and a diff on this
// output means someone changed the generator or the base — not that the dice
// rolled differently.
let _s = 0x9e3779b9;
function rnd() {
    _s |= 0; _s = (_s + 0x6d2b79f5) | 0;
    let t = Math.imul(_s ^ (_s >>> 15), 1 | _s);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
}
const pick = (xs) => xs[Math.floor(rnd() * xs.length)];
/** Integer minor units, inclusive range. */
const cents = (lo, hi) => Math.round(lo + rnd() * (hi - lo));
const HEX = '0123456789abcdef';
function uuid() {
    let out = '';
    for (let i = 0; i < 32; i++) out += HEX[Math.floor(rnd() * 16)];
    return `${out.slice(0, 8)}-${out.slice(8, 12)}-${out.slice(12, 16)}-${out.slice(16, 20)}-${out.slice(20)}`;
}

// --- base --------------------------------------------------------------------
const base = JSON.parse(readFileSync(BASE_PATH, 'utf8'));
const items = base.all_items;
const accounts = items.filter((i) => i.obj_type === 'acct');

const ROOT = accounts.find((a) => a.type === 'r');
const USD = ROOT.currid;
const byName = new Map();
for (const a of accounts) {
    if (a.type !== 'i' && a.type !== 'e') { byName.set(a.name, a); continue; }
    // Categories are addressed by their full path, because leaf names repeat
    // across parents on purpose — "Misc" exists under four of them.
    const parent = accounts.find((p) => p.id === a.parentid);
    const path = parent && (parent.type === 'i' || parent.type === 'e')
        ? `${parent.name}/${a.name}` : a.name;
    byName.set(path, a);
}
const cat = (path) => {
    const hit = byName.get(path);
    if (!hit) throw new Error(`category not in base export: ${path}`);
    return hit.id;
};

const CHECKING = byName.get('Checking').id;
const SAVINGS = byName.get('Savings').id;

// --- accounts the household needs and the base lacks -------------------------
// The base has Checking, Savings and an investment account. A ledger with no
// credit card cannot show a statement cycle, a payment transfer, or the one
// account type whose sign convention people most often get wrong.
const EVERYDAY_CARD = uuid();
const TRAVEL_CARD = uuid();
// A house and the mortgage against it. Net worth is the one report that needs
// an account whose balance does not come from a bank feed, and the mortgage
// payment is the everyday transaction people most often model wrongly: it is
// not an expense, it is interest (expense) plus principal (a liability moving
// toward zero). `sbal` carries the opening position so neither needs an
// invented origin transaction — BalanceValidator reconstructs from sbal plus
// flows, so the file stays self-consistent.
const HOUSE = uuid();
const MORTGAGE = uuid();

// --- the car loan ------------------------------------------------------------
// Dimensions 1 and 40 of the fixture audit, which are one gap: no fixture had a
// Moneydance LOAN account (`type='o'`), so `loan_terms` — the whole of ADR-0050
// on the import side — had never been written by any test at any point.
//
// A loan rather than a second liability, and kept ALONGSIDE the mortgage rather
// than replacing it, because MD models the two differently and a real ledger
// usually carries both shapes: the mortgage here is a liability whose split the
// user enters by hand (which is why its interest is a rough declining curve),
// and this is an amortizing loan whose terms MD stores so it can compute the
// split. Converting the mortgage instead would have cost the only `liability`
// account, which net worth needs and `DemoSampleBreadthTests` already pins.
//
// `Loan/Interest` and `Loan/Payment` are in the starter taxonomy and were used
// by nothing — the taxonomy was already expecting this account.
const AUTO_LOAN = uuid();
const AUTO_LOAN_PRINCIPAL_MINOR = 2_850_000;   // in MD's minor units (cents)
const AUTO_LOAN_RATE_PCT = 5.9;                // percent per year, as MD stores it
const AUTO_LOAN_PAYMENTS = 60;
const AUTO_LOAN_PER_YEAR = 12;
// First payment predates the window, so the loan is mid-term for all 36 months
// and `sbal` has to carry the payments already made. A loan that starts inside
// the window would leave most of it with no loan activity at all.
const AUTO_LOAN_FIRST = { y: 2022, m: 8 };     // September 2022
const AUTO_RATE_M = AUTO_LOAN_RATE_PCT / 100 / AUTO_LOAN_PER_YEAR;
const AUTO_LOAN_PAYMENT = Math.round(
    AUTO_LOAN_PRINCIPAL_MINOR * AUTO_RATE_M /
    (1 - Math.pow(1 + AUTO_RATE_M, -AUTO_LOAN_PAYMENTS)));

/** 1-based index of the payment due in (y, m); <1 or >N means none is. */
const autoLoanPaymentNo = (y, m) =>
    (y - AUTO_LOAN_FIRST.y) * 12 + (m - AUTO_LOAN_FIRST.m) + 1;

/**
 * Principal still owed after `k` payments, in minor units.
 *
 * Iterated rather than closed-form ON PURPOSE. Each payment's interest is
 * rounded to the cent before it is subtracted, exactly as the emitted rows do
 * it, so `sbal` plus the generated flows lands on the same figure the
 * amortization implies. A closed-form balance would be a few cents off and
 * BalanceValidator would be reconciling against a number no transaction
 * produced.
 */
function autoLoanBalanceAfter(k) {
    let owed = AUTO_LOAN_PRINCIPAL_MINOR;
    for (let i = 0; i < k; i++) {
        owed -= AUTO_LOAN_PAYMENT - Math.round(owed * AUTO_RATE_M);
    }
    return Math.max(owed, 0);
}

// Payments already made when the window opens. A LITERAL, checked against
// `START` further down rather than derived from it, because `START` is not
// defined yet here and `newAccounts` is an array literal evaluated on the spot.
// The check matters more than the derivation would: if the window moves and
// this does not, `sbal` sits on a balance no payment in the file produces and
// BalanceValidator reconciles against a number out of thin air.
const AUTO_LOAN_PRIOR_PAYMENTS = 9;            // September 2022 .. May 2023
const AUTO_LOAN_SBAL = -autoLoanBalanceAfter(AUTO_LOAN_PRIOR_PAYMENTS);

const newAccounts = [
    { obj_type: 'acct', id: EVERYDAY_CARD, type: 'c', name: 'Everyday Card',
      parentid: ROOT.id, currid: USD, sbal: '0', ts: '1686000000000' },
    { obj_type: 'acct', id: TRAVEL_CARD, type: 'c', name: 'Travel Card',
      parentid: ROOT.id, currid: USD, sbal: '0', ts: '1686000000000' },
    { obj_type: 'acct', id: HOUSE, type: 'a', name: 'Primary Residence',
      parentid: ROOT.id, currid: USD, sbal: '41500000', ts: '1686000000000' },
    { obj_type: 'acct', id: MORTGAGE, type: 'l', name: 'Primary Residence Mortgage',
      parentid: ROOT.id, currid: USD, sbal: '-28900000', ts: '1686000000000' },
    // type 'o' — the loan. Every `MdLoanFields` key MD writes is here, because
    // the point is to exercise the mapper, and `LoanMapper` returns null (and
    // seeds nothing) if principal, rate, payment count or payments-per-year is
    // missing. `interest_account_id` is an MD account uuid like any other, so a
    // CATEGORY id is what belongs there.
    { obj_type: 'acct', id: AUTO_LOAN, type: 'o', name: 'Torvanne Auto Loan',
      parentid: ROOT.id, currid: USD, sbal: String(AUTO_LOAN_SBAL), ts: '1686000000000',
      int_rate: String(AUTO_LOAN_RATE_PCT),
      num_payments: String(AUTO_LOAN_PAYMENTS),
      pmts_per_year: String(AUTO_LOAN_PER_YEAR),
      init_principal: String(AUTO_LOAN_PRINCIPAL_MINOR),
      points: '0',
      escrow_payment: '0',
      // MD's flag: 1 = amortize, 0 = use the fixed `monthly_pmt`. Computed here,
      // so `LoanMapper` must store NULL in fixed_payment despite monthly_pmt
      // being present — which is the branch worth having a fixture for.
      calc_pmt: '1',
      monthly_pmt: String(AUTO_LOAN_PAYMENT),
      interest_account_id: cat('Loan/Interest'),
      date_created: '20220915' },
];

// --- the brokerage, carried over from the curated core -----------------------
// Looked up by name rather than hard-coded: the base owns these ids, and a
// literal here would rot silently the first time it is regenerated.
const BROKERAGE = byName.get('Demo Investment Account').id;
const ETF = byName.get('Index ETF A');
const ETF_CURR = ETF.currid;
/** Index ETF A is dec=5, so a share count is shares x 10^5. */
const SHARE_UNITS = 1e5;

/**
 * Price per share on a given month, drifting upward from the `drift` base
 * below across the window so that it ARRIVES at the price the curated core's
 * own trades are made at. A flat price would make every performance report a
 * straight line and hide any cost-basis or gain defect behind equal numbers.
 */
function priceOn(y, m) {
    const months = (y - 2023) * 12 + (m - 5);
    const drift = 520 + months * 3.4;
    const wobble = Math.sin(months * 1.3) * 18 + (rnd() - 0.5) * 9;
    return Math.round((drift + wobble) * 100) / 100;
}

// --- the cast ----------------------------------------------------------------
// Invented names. A payee carries both the cleaned form MD would show and the
// raw string a bank feed delivers, so `ol.orig-payee` has something to be
// different from — which is the whole of what FU-024 is about.
const GROCERS = [
    ['Quellmont Market', 'QUELLMONT MARKET #0412 POS PURCHASE'],
    ['Pinewood Grocery', 'PINEWOOD GROCERY 88 DEBIT CARD'],
    ['Valebrook Foods', 'VALEBROOK FOODS 1120 PURCHASE'],
];
const DINERS = [
    ['Brasswick Kitchen', 'BRASSWICK KITCHEN 7781'],
    ['Nine Lanterns', 'NINE LANTERNS LLC DEBIT'],
    ['The Wenlow Tap', 'WENLOW TAP HOUSE 0099'],
    ['Marrow & Vine', 'MARROW AND VINE 4410'],
];
const FUEL = [
    ['Westgate Fuel', 'WESTGATE FUEL 221 PUMP 4'],
    ['Oakline Petrol', 'OAKLINE PETROL STN 7'],
];
const EMPLOYER = 'Thornbury Logistics';

// The one heavily-tagged row's tags. Dimension 25 of the fixture audit, and the
// case ADR-0103 built `TagLine` for in so many words: "chips are fine at two
// tags and fall apart at eight ... Moneydance users tag heavily and names run
// to 50 characters, so that is the normal case here, not an outlier."
//
// MEASURED BEFORE THIS EXISTED: the sample had 95 tagged fields and every single
// one carried exactly ONE tag, across 5 distinct names, the longest 16
// characters. So the control's entire reason for existing — the width it
// truncates, the count badge, the title fallback — had no fixture behind it, and
// the register row whose height used to triple could not be reproduced.
//
// Names stop at 50 because `TagsEndpoints.MaxTagNameLength` is 64 and the point
// is a realistic worst case, not an illegal one. The importer does NOT enforce
// that cap (`AddCommaSeparated` only trims), so a fixture could carry a name the
// API would refuse to create; staying under it keeps this file from depending on
// that asymmetry.
const HEAVY_HEADER_TAGS = [
    'Reimbursable - submit with quarterly claim form',
    'Tax deductible - professional development category',
    'Receipt scanned and filed in the document archive',
    'Awaiting employer reimbursement since trip date',
    'Shared cost - settle with household at year end',
    'Needs review before the next reconciliation pass',
    'Conference 2024',
    'Travel',
    'Business',
];
// A DIFFERENT set on the leg, not a slice of the header's. ADR-0103 gives the
// two levels different meanings, and overlapping names would let a reader —
// or a grouping — mistake one level for the other.
const HEAVY_LEG_TAGS = [
    'Rail fare booked through the corporate travel desk',
    'Advance purchase - non refundable after departure',
    'Carbon offset purchased with this ticket at source',
    'Split across two cost centres by prior arrangement',
    'Claimed at the standard mileage equivalent instead',
    'Paid on the travel card rather than the joint card',
    'Receipt emailed',
    'Rail',
];

// --- calendar ----------------------------------------------------------------
// Ends two days before the base export's own `export_date` (20260520) so the
// file reads as one ledger exported on one day, and begins far enough back that
// a 36-month cash-flow report has 36 months to draw.
const START = new Date(Date.UTC(2023, 5, 1));
const END = new Date(Date.UTC(2026, 4, 18));
const EXPORT_DAY = Date.UTC(2026, 4, 20);

// The literal above, checked. Moving the window without moving it is the one
// way this file can go quietly inconsistent.
const impliedPriorPayments =
    autoLoanPaymentNo(START.getUTCFullYear(), START.getUTCMonth()) - 1;
if (impliedPriorPayments !== AUTO_LOAN_PRIOR_PAYMENTS) {
    throw new Error(
        `AUTO_LOAN_PRIOR_PAYMENTS is ${AUTO_LOAN_PRIOR_PAYMENTS} but the window ` +
        `implies ${impliedPriorPayments} — sbal would not match the emitted rows`);
}

const ymd = (d) => `${d.getUTCFullYear()}${String(d.getUTCMonth() + 1).padStart(2, '0')}${String(d.getUTCDate()).padStart(2, '0')}`;
const day = (y, m, dd) => new Date(Date.UTC(y, m, dd));

/**
 * Reconciliation state by age, which is the shape a real ledger has: the back
 * catalogue is reconciled against statements, the last couple of months are
 * cleared but not yet reconciled, and the last few weeks have not been touched.
 * MD's own codes — `NormalizeMdStatus` folds R and c alike to Coffer's
 * "cleared", but keeping both is what the file would really contain.
 */
function statusFor(d) {
    const ageDays = (EXPORT_DAY - d.getTime()) / 86400000;
    if (ageDays > 365) return 'R';
    if (ageDays > 45) return 'c';
    return ' ';
}

const out = [];
let clock = Date.UTC(2023, 5, 1, 9, 0, 0);
const stamp = () => String((clock += 37_000));

/**
 * One transaction. `legs` are the other sides: `{ acct, amount, memo?, stat? }`,
 * where `amount` is the minor-unit effect ON THAT ACCOUNT, so the origin leg
 * takes its negation.
 */
function txn({ acct, date, desc, memo, legs, chk, origPayee, origMemo, stat, tags, taxDate }) {
    const row = {
        obj_type: 'txn',
        id: uuid(),
        acctid: acct,
        desc,
        dt: ymd(date),
        // MD's `td`. Equal to `dt` on almost every row, which is exactly why
        // the few where it differs are worth carrying: dimension 17, and the
        // data FU-018's tax-year grouping will have to be built against.
        td: ymd(taxDate ?? date),
        dtentered: stamp(),
        ts: stamp(),
        stat: stat ?? statusFor(date),
        xfer_type: 'xfrtp_bank',
    };
    if (memo) row.memo = memo;
    if (chk) row.chk = chk;
    // Only rows that would have arrived from a feed carry the bank's own
    // strings; a hand-entered cheque has no `ol.*` at all.
    if (origPayee) row['ol.orig-payee'] = origPayee;
    if (origMemo) row['ol.orig-memo'] = origMemo;
    // Comma-separated, which is what AddCommaSeparated parses. Header tags are
    // a claim about the whole transaction; a split's own `{N}.tags` is a claim
    // about that posting, and ADR-0103 keeps them apart rather than folding the
    // second into the first.
    if (tags) row.tags = tags.join(',');

    const multi = legs.length > 1;
    legs.forEach((leg, i) => {
        row[`${i}.id`] = uuid();
        row[`${i}.obj_type`] = '';
        row[`${i}.acctid`] = leg.acct;
        row[`${i}.samt`] = String(leg.amount);
        row[`${i}.pamt`] = String(-leg.amount);
        row[`${i}.desc`] = multi ? leg.memo ?? desc : desc;
        if (leg.stat) row[`${i}.stat`] = leg.stat;
        if (leg.tags) row[`${i}.tags`] = leg.tags.join(',');
    });
    out.push(row);
}

/** A spend from a card or bank account into one expense category. */
function spend(acct, date, payee, raw, category, amount, memo, tags, taxDate) {
    txn({
        acct, date, desc: payee, memo, tags, taxDate,
        origPayee: raw,
        origMemo: memo ? undefined : `${raw} ${ymd(date)}`,
        legs: [{ acct: cat(category), amount }],
    });
}

/**
 * An MD-native investment transaction: `invest.txntype` set, splits carrying
 * `invest.splittype` of sec / inc / fee. Kept native deliberately — the
 * QIF-origin path (blank txntype, classified via xfer_type + qif_invst_action)
 * is reference-ledger.json's job, and duplicating it here would blur which
 * fixture covers which classifier.
 *
 * `legs` are raw: { acct, splittype, pamt, samt, tags? }. No sign is inferred,
 * because on a `sec` split `samt` is a SHARE COUNT and on the others it is
 * cash — one rule cannot cover both.
 */
function invTxn({ date, txntype, xferType, desc, memo, legs, tags }) {
    const row = {
        obj_type: 'txn',
        id: uuid(),
        acctid: BROKERAGE,
        desc,
        dt: ymd(date),
        td: ymd(date),
        dtentered: stamp(),
        ts: stamp(),
        stat: statusFor(date),
        'invest.txntype': txntype,
        xfer_type: xferType,
    };
    if (memo) row.memo = memo;
    if (tags) row.tags = tags.join(',');
    legs.forEach((leg, i) => {
        row[`${i}.id`] = uuid();
        row[`${i}.obj_type`] = '';
        row[`${i}.acctid`] = leg.acct;
        row[`${i}.invest.splittype`] = leg.splittype;
        row[`${i}.pamt`] = String(leg.pamt);
        row[`${i}.samt`] = String(leg.samt);
        row[`${i}.desc`] = desc;
        row[`${i}.stat`] = row.stat;
        if (leg.tags) row[`${i}.tags`] = leg.tags.join(',');
    });
    out.push(row);
}

/** Monthly price points, so a holding has a series and not a single dot. */
const snaps = [];
function snapshot(date, price) {
    snaps.push({
        obj_type: 'csnap', id: uuid(), curr: ETF_CURR,
        dt: ymd(date),
        // MD stores the INVERSE rate, which is why the curated core's own
        // snapshots read as a small fraction, the reciprocal of the share
        // price, rather than as the price.
        relrt: String(1 / price), urt: String(1 / price),
        hi: '0.0', lo: '0.0', rhi: '0.0', rlo: '0.0', vol: '0', ts: stamp(),
    });
}

// --- the history -------------------------------------------------------------
for (let d = new Date(START); d <= END; d.setUTCMonth(d.getUTCMonth() + 1)) {
    const y = d.getUTCFullYear();
    const m = d.getUTCMonth();
    const inMonth = (dd) => day(y, m, dd) <= END;

    // Two paychecks. The gross/withholding split is the reason this exists at
    // all: it is the one everyday transaction with six legs, and until now the
    // sample had no multi-split bank row for a register or a report to meet.
    for (const payDay of [15, lastDay(y, m)]) {
        if (!inMonth(payDay)) continue;
        const date = day(y, m, payDay);
        const gross = cents(312_000, 318_000);
        const fed = Math.round(gross * 0.18);
        const state = Math.round(gross * 0.052);
        const ss = Math.round(gross * 0.062);
        const med = Math.round(gross * 0.0145);
        const health = 21_400;
        txn({
            acct: CHECKING, date, desc: EMPLOYER, memo: 'Direct deposit',
            origPayee: 'THORNBURY LOGISTICS PAYROLL',
            origMemo: 'DIRECT DEP PPD ID 9920114',
            legs: [
                { acct: cat('Salary/Paycheck'), amount: -gross, memo: 'Gross pay' },
                { acct: cat('Tax/Federal Income Tax'), amount: fed, memo: 'Federal withholding' },
                { acct: cat('Tax/State Income Tax'), amount: state, memo: 'State withholding' },
                { acct: cat('Tax/Social Security Tax'), amount: ss, memo: 'Social Security' },
                { acct: cat('Tax/Medicare Tax'), amount: med, memo: 'Medicare' },
                { acct: cat('Insurance/Health'), amount: health, memo: 'Health premium', tags: ['Benefits'] },
            ],
        });
    }

    // Rent, by cheque — the only thing in the ledger with a check number, and
    // therefore the only thing that proves the column is wired.
    if (inMonth(1)) {
        txn({
            acct: CHECKING, date: day(y, m, 1), desc: 'Corvane Property Group',
            memo: 'Monthly rent', chk: String(1200 + (y - 2023) * 12 + m),
            legs: [{ acct: cat('Bills/Rent'), amount: 182_500 }],
        });
    }

    // Utilities and subscriptions, each on its own day so a daily series has
    // something other than one spike per month.
    const bills = [
        [4, 'Ridgeline Power', 'RIDGELINE POWER AUTOPAY', 'Bills/Electric', cents(7_800, 17_500)],
        [6, 'Cedar Gas Co', 'CEDAR GAS CO ACH DEBIT', 'Bills/Natural Gas', cents(2_400, 11_900)],
        [9, 'Harbour Water District', 'HARBOUR WATER DIST', 'Bills/Water/Sewer', cents(4_100, 6_800)],
        [11, 'Lattice Broadband', 'LATTICE BROADBAND MONTHLY', 'Bills/Cable', 8_999],
        [12, 'Quillfen Mobile', 'QUILLFEN MOBILE WIRELESS', 'Bills/Phone', 7_450],
    ];
    for (const [dd, payee, raw, category, amount] of bills) {
        if (inMonth(dd)) spend(CHECKING, day(y, m, dd), payee, raw, category, amount);
    }

    // Groceries and dining, on the card.
    for (const dd of [3, 8, 13, 19, 25]) {
        if (!inMonth(dd)) continue;
        const [payee, raw] = pick(GROCERS);
        spend(EVERYDAY_CARD, day(y, m, dd), payee, raw, 'Personal/Groceries', cents(4_200, 14_800));
    }
    for (const dd of [7, 16, 23]) {
        if (!inMonth(dd)) continue;
        const [payee, raw] = pick(DINERS);
        spend(EVERYDAY_CARD, day(y, m, dd), payee, raw, 'Personal/Dining', cents(2_600, 9_400));
    }
    for (const dd of [5, 20]) {
        if (!inMonth(dd)) continue;
        const [payee, raw] = pick(FUEL);
        spend(EVERYDAY_CARD, day(y, m, dd), payee, raw, 'Automotive/Fuel', cents(3_100, 7_600));
    }

    // Card payment. A transfer between two real accounts, so BOTH sides carry a
    // status — this is the row that proves per-leg reconciliation is per-leg.
    if (inMonth(18)) {
        const date = day(y, m, 18);
        const amount = cents(68_000, 112_000);
        txn({
            acct: CHECKING, date, desc: 'Everyday Card payment',
            origPayee: 'EVERYDAY CARD ONLINE PMT',
            legs: [{ acct: EVERYDAY_CARD, amount, stat: statusFor(date) }],
        });
    }

    // Savings sweep.
    if (inMonth(16)) {
        const date = day(y, m, 16);
        txn({
            acct: CHECKING, date, desc: 'Transfer to Savings', memo: 'Monthly sweep',
            legs: [{ acct: SAVINGS, amount: 40_000, stat: statusFor(date) }],
        });
    }

    // Savings interest — small, monthly, and the only recurring income that is
    // not payroll, so an income report has more than one bar.
    if (inMonth(lastDay(y, m))) {
        const date = day(y, m, lastDay(y, m));
        txn({
            acct: SAVINGS, date, desc: 'Interest paid',
            legs: [{ acct: cat('Investment/Interest Received'), amount: -cents(180, 1_450) }],
        });
    }

    // The long tail: occasional, seasonal and annual spending, which is what
    // separates a report with a shape from a report with four flat lines.
    const occasional = [
        [10, 'Fennick Veterinary', 'FENNICK VET CLINIC', 'Pet Care/Veterinarian', cents(6_500, 31_000), 0.35],
        [14, 'Quarry Lane Pharmacy', 'QUARRY LANE PHARMACY 22', 'Healthcare/Prescriptions', cents(1_200, 8_400), 0.5],
        [21, 'Tamsett & Co', 'TAMSETT AND CO RETAIL', 'Personal/Clothing', cents(4_000, 22_000), 0.4],
        [22, 'Thistlewood Hardware', 'THISTLEWOOD HARDWARE', 'Home/Maintenance', cents(2_500, 26_000), 0.4],
        [24, 'Orrindale Cinema', 'ORRINDALE CINEMA 14', 'Personal/Entertainment', cents(2_400, 6_200), 0.45],
        [26, 'Ashford Pet Supply', 'ASHFORD PET SUPPLY', 'Pet Care/Food', cents(3_800, 9_900), 0.5],
        [17, 'Mirrowfield Dental', 'MIRROWFIELD DENTAL GROUP', 'Healthcare/Dental', cents(9_000, 38_000), 0.18],
    ];
    for (const [dd, payee, raw, category, amount, chance] of occasional) {
        if (inMonth(dd) && rnd() < chance) {
            spend(EVERYDAY_CARD, day(y, m, dd), payee, raw, category, amount);
        }
    }

    // Tax date ≠ posted date — dimension 17, and the shape that makes it matter
    // rather than a curiosity: bought on the last day of December, posted by
    // the card in January. Under posted-date grouping it lands in the new tax
    // year; under transacted-date grouping it does not, and which of those a
    // report should offer is the whole of FU-018's open half.
    //
    // At a YEAR boundary deliberately. A differing tax date inside one month
    // proves only that the column round-trips; one that crosses a period
    // boundary is what can make two reports disagree, which is the behaviour
    // worth having a fixture for.
    if (m === 0 && inMonth(2)) {
        spend(EVERYDAY_CARD, day(y, m, 2), 'Pellowick Books', 'PELLOWICK BOOKS 2210',
            'Education/Books', cents(4_200, 9_800), 'Ordered 31 Dec, posted 2 Jan',
            undefined, day(y - 1, 11, 31));
    }
    // And one away from any boundary, so a test can tell "the column is
    // populated at all" apart from "the year boundary is handled".
    if (m === 4 && inMonth(6)) {
        spend(CHECKING, day(y, m, 6), 'Thistlewood Hardware', 'THISTLEWOOD HARDWARE',
            'Home/Maintenance', cents(3_100, 12_400), 'Ordered 28 Apr, charged 6 May',
            undefined, day(y, m - 1, 28));
    }

    // Car insurance, half-yearly.
    if ((m === 2 || m === 8) && inMonth(2)) {
        spend(CHECKING, day(y, m, 2), 'Brackwell Mutual', 'BRACKWELL MUTUAL INS PREM',
            'Insurance/Automobile', cents(54_000, 61_000));
    }
    // Property tax, annually.
    if (m === 10 && inMonth(15)) {
        spend(CHECKING, day(y, m, 15), 'County Treasurer', 'COUNTY TREASURER RE TAX',
            'Tax/Property Tax', cents(238_000, 262_000));
    }
    // A summer trip, on the travel card, so one account has a seasonal profile
    // instead of a flat one.
    if ((m === 6 || m === 7) && inMonth(12)) {
        const trip = [`Trip ${y}`];
        spend(TRAVEL_CARD, day(y, m, 11), 'Skerrowby Inn', 'SKERROWBY INN LODGING',
            'Vacation/Lodging', cents(62_000, 141_000), undefined, trip);
        spend(TRAVEL_CARD, day(y, m, 12), 'Northbound Rail', 'NORTHBOUND RAIL TICKETS',
            'Vacation/Travel', cents(18_000, 44_000), undefined, trip);
    }
    // ONCE in the whole history, not annually: a row tagged nine ways is a shape
    // the register and the tag control have to survive, not a habit this
    // household has. Repeating it would also bend the tag frequencies a Tag
    // Summary report reads, and the fixture's job here is to carry the case, not
    // to make it common.
    //
    // It is a SPLIT so both ADR-0103 levels get the case at once: nine tags on
    // the header, eight more on one posting. A single-leg row could only ever
    // exercise one of them.
    //
    // Business/* is otherwise unused by the generated history, so this also
    // gives that branch of the taxonomy its first transactions.
    if (y === 2024 && m === 7 && inMonth(18)) {
        txn({
            acct: TRAVEL_CARD, date: day(y, m, 18),
            desc: 'Harbourfield Conference Center',
            memo: 'Annual conference - reimbursable',
            origPayee: 'HARBOURFIELD CONF CTR 0188',
            tags: HEAVY_HEADER_TAGS,
            legs: [
                { acct: cat('Business/Reimbursed Expense'), amount: 94_500,
                  memo: 'Two nights lodging' },
                { acct: cat('Business/Reimbursed Expense'), amount: 31_200,
                  memo: 'Rail fare', tags: HEAVY_LEG_TAGS },
                { acct: cat('Business/Non-Reimbursed Expense'), amount: 8_650,
                  memo: 'Meals beyond the per diem' },
            ],
        });
    }
    // The car loan's monthly payment — the same interest/principal shape as the
    // mortgage below, but driven by the amortization the account's own
    // `loan_terms` declare, so the ledger and the terms agree. That agreement
    // is the point: a loan account whose transactions do not follow its own
    // schedule would let an amortization bug pass against this fixture.
    //
    // Interest is rounded per payment and principal takes the remainder, so the
    // payment total is exactly AUTO_LOAN_PAYMENT every month and the final
    // balance lands on zero rather than on a drift of accumulated halves.
    {
        const k = autoLoanPaymentNo(y, m);
        if (k >= 1 && k <= AUTO_LOAN_PAYMENTS && inMonth(8)) {
            const date = day(y, m, 8);
            const owed = autoLoanBalanceAfter(k - 1);
            const interest = Math.round(owed * AUTO_RATE_M);
            const principal = AUTO_LOAN_PAYMENT - interest;
            txn({
                acct: CHECKING, date, desc: 'Torvanne Motor Finance',
                memo: `Auto loan payment ${k} of ${AUTO_LOAN_PAYMENTS}`,
                origPayee: 'TORVANNE MTR FIN ACH',
                legs: [
                    { acct: cat('Loan/Interest'), amount: interest, memo: 'Interest' },
                    { acct: AUTO_LOAN, amount: principal, memo: 'Principal',
                      stat: statusFor(date) },
                ],
            });
        }
    }

    // The mortgage. Interest is an expense; principal moves the liability
    // toward zero. Splitting it is the point — a single "mortgage" expense line
    // is the most common way a ledger's net worth goes quietly wrong.
    if (inMonth(3)) {
        const date = day(y, m, 3);
        const months = (y - 2023) * 12 + (m - 5);
        const interest = Math.max(72_000, 118_000 - months * 420);
        const principal = 168_000 - interest + 72_000;
        txn({
            acct: CHECKING, date, desc: 'Ashby Mutual Mortgage', memo: 'Monthly payment',
            origPayee: 'ASHBY MUTUAL MTG ACH',
            legs: [
                { acct: cat('Bills/Mortgage Interest'), amount: interest, memo: 'Interest' },
                { acct: MORTGAGE, amount: principal, memo: 'Principal', stat: statusFor(date) },
            ],
        });
    }

    // Cash into the brokerage, so the investment account has a balance history
    // rather than a single opening position.
    if (inMonth(19)) {
        const date = day(y, m, 19);
        txn({
            acct: CHECKING, date, desc: 'Transfer to brokerage',
            legs: [{ acct: BROKERAGE, amount: 50_000, stat: statusFor(date) }],
        });
    }

    const price = priceOn(y, m);
    if (inMonth(28)) snapshot(day(y, m, 28), price);

    // Quarterly: buy with the accumulated cash, and take the dividend. Both
    // land inside ADR-0027's nine actions (buy / dividend_cash), which
    // DemoSampleImportTests asserts is closed — breadth must never be bought by
    // widening that set.
    if (m % 3 === 2 && inMonth(15)) {
        const date = day(y, m, 15);
        const cash = cents(130_000, 160_000);
        const fee = 495;
        const shares = Math.round((cash / 100 / price) * SHARE_UNITS);
        invTxn({
            date, txntype: 'buy', xferType: 'xfrtp_buysell',
            desc: 'Index ETF A', memo: `Buy at ${price.toFixed(2)}`,
            legs: [
                { acct: ETF.id, splittype: 'sec', pamt: -cash, samt: shares },
                { acct: cat('Investment/Trading Commission'), splittype: 'fee', pamt: -fee, samt: fee },
            ],
        });
    }
    if (m % 3 === 2 && inMonth(22)) {
        const amount = cents(2_400, 6_900);
        invTxn({
            date: day(y, m, 22), txntype: 'div', xferType: 'xfrtp_dividend',
            desc: 'Index ETF A dividend', memo: 'Quarterly distribution',
            tags: ['Portfolio income'],
            legs: [
                { acct: ETF.id, splittype: 'sec', pamt: 0, samt: 0 },
                { acct: cat('Investment/Dividends'), splittype: 'inc', pamt: amount, samt: -amount },
            ],
        });
    }
    // One sale a year, so realized gains and cost basis have something to
    // compute over a rising price rather than a flat one.
    if (m === 9 && inMonth(24)) {
        const shares = Math.round(0.9 * SHARE_UNITS);
        const cash = Math.round((shares / SHARE_UNITS) * price * 100);
        const fee = 495;
        invTxn({
            date: day(y, m, 24), txntype: 'sell', xferType: 'xfrtp_buysell',
            desc: 'Index ETF A', memo: `Sell at ${price.toFixed(2)}`,
            legs: [
                { acct: ETF.id, splittype: 'sec', pamt: cash, samt: -shares },
                { acct: cat('Investment/Trading Commission'), splittype: 'fee', pamt: -fee, samt: fee },
            ],
        });
    }

    // A RETURN: a negative amount posted to an EXPENSE category.
    //
    // Dimension 7 of the fixture audit, and the one it ranks first. Every
    // other expense here is positive, which makes "expenses are positive"
    // look like a rule instead of the usual case — and it was stated as a
    // rule in review. A refund is ordinary (clothing goes back), and it is
    // what makes a report prove it nets rather than totalling magnitudes.
    // Deliberately in a month where the same category also has a purchase,
    // so the row nets rather than simply reading negative.
    if (m % 4 === 1 && inMonth(23)) {
        spend(EVERYDAY_CARD, day(y, m, 23), 'Tamsett & Co', 'TAMSETT AND CO RETURN',
            'Personal/Clothing', -cents(3_000, 9_000), 'Returned');
    }

    // A ZERO-AMOUNT posting — dimension 11. The sample already had zero-amount legs, but
    // every one of them was an investment security leg, where zero cash is
    // structural rather than a value the user entered. Nothing carried the
    // ordinary case: a row that exists, means something, and moves no money.
    //
    // A waived fee is the most common shape of it — the charge is entered so
    // the waiver is on the record, and the amount is zero. Annually, so a
    // monthly report has months with and without one.
    if (m === 9 && inMonth(27)) {
        spend(CHECKING, day(y, m, 27), 'Account service charge', 'MONTHLY SERVICE CHARGE WAIVED',
            'Bank Charge/Service Charges', 0, 'Waived — balance above minimum');
    }

    // An occasional bank fee, because "Bank Charge" is in the starter taxonomy
    // and nothing in the ledger ever landed there.
    if (rnd() < 0.2 && inMonth(27)) {
        spend(CHECKING, day(y, m, 27), 'Account service charge', 'MONTHLY SERVICE CHARGE',
            'Bank Charge/Service Charges', 1_200);
    }
}

function lastDay(y, m) { return new Date(Date.UTC(y, m + 1, 0)).getUTCDate(); }

// --- reminders ---------------------------------------------------------------
// Dimension 15. `recurring_transactions` had an import path (ADR-0047) and an
// integration test, but no FIXTURE carried a reminder — so the demo a new
// install provisions arrived with an empty Reminders screen, and the full
// pipeline's reminder step never ran over the sample at all.
//
// Two, chosen for the rrule branches the existing test leaves alone: it covers
// `weeklydays`, these cover `monthlydays` and `yearly`. MD's precedence is
// daily -> monthly -> weekly -> yearly, so each needs the earlier fields absent
// or zero or it is simply not reached.
const reminders = [
    {
        obj_type: 'reminder', id: uuid(),
        desc: 'Corvane Property Group', memo: 'Monthly rent', type: 'monthly',
        sdt: '20230601',
        // Acknowledged partway through, so next_due is a COMPUTED cursor rather
        // than the series' first occurrence. An unacknowledged reminder leaves
        // NextDueCalculator returning the start date, which is the one answer
        // it cannot get wrong.
        ackdt: '20260401',
        acdays: '3',                  // auto-commit three days ahead
        daily: '0', monthlydays: '1', monthlymod: '1', weeklydays: '', yearly: '0',
        is_loan_reminder: '0',
        'txn.acctid': CHECKING,
        'txn.desc': 'Corvane Property Group',
        'txn.memo': 'Monthly rent',
        'txn.dt': '20230601',
        'txn.0.id': uuid(),
        'txn.0.acctid': cat('Bills/Rent'),
        'txn.0.samt': '182500',
        'txn.0.pamt': '-182500',
        'txn.0.desc': 'Monthly rent',
    },
    {
        obj_type: 'reminder', id: uuid(),
        desc: 'County Treasurer', memo: 'Property tax', type: 'yearly',
        sdt: '20231115',
        // acdays -1 is MD's "manual" — the mapper must store NULL, not -1, and
        // nothing else in the suite carries the negative case.
        acdays: '-1',
        // An end date, so `end_date` is non-null on one of the two.
        ldt: '20301115',
        daily: '0', monthlydays: '', monthlymod: '0', weeklydays: '', yearly: '1',
        is_loan_reminder: '0',
        'txn.acctid': CHECKING,
        'txn.desc': 'County Treasurer',
        'txn.memo': 'Property tax',
        'txn.dt': '20231115',
        'txn.0.id': uuid(),
        'txn.0.acctid': cat('Tax/Property Tax'),
        'txn.0.samt': '250000',
        'txn.0.pamt': '-250000',
        'txn.0.desc': 'Property tax',
    },
];

// --- emit --------------------------------------------------------------------
// Accounts first, then the base's own items, then the generated history. Order
// is not load-bearing for the importer (it indexes by id), but keeping the
// curated core contiguous makes the diff of a regeneration readable.
const merged = {
    metadata: { ...base.metadata, file_name: 'Coffer Demo', file_path: 'Coffer Demo.moneydance' },
    all_items: [...items, ...newAccounts, ...snaps, ...out, ...reminders],
    local_settings: base.local_settings,
};

writeFileSync(OUT_PATH, JSON.stringify(merged, null, 1) + '\n');

const bankTxns = out.length;
const cleared = out.filter((t) => t.stat !== ' ').length;
console.log(`moneydance-export-demo.json written`);
console.log(`  base items     ${items.length}`);
console.log(`  + accounts     ${newAccounts.length}`);
console.log(`  + transactions ${bankTxns}  (${cleared} cleared/reconciled, ${bankTxns - cleared} open)`);
console.log(`  span           ${ymd(START)}..${ymd(END)}`);

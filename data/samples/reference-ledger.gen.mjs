// =============================================================================
// reference-ledger.gen.mjs — generator for data/samples/reference-ledger.json
//
// A synthetic, PII-free "truly representative" Moneydance export whose shapes
// are derived from diffing the real import against the demo (roadmap #3). Unlike
// the demo (MD-native: `invest.txntype` set), this mirrors real MD-from-QIF
// data: investment txns have a BLANK `invest.txntype` and are classified via
// `xfer_type` + `qif_invst_action` — the code path 100% of real investment
// transactions actually use. It also carries the shapes the demo lacks:
//   - $1/share placeholder transfers (orphan + paired) and a self-ref buysellxfr
//   - security diversity: mutual fund (dec 9), money-market (dec 4), bond,
//     equity, international, ETF — spanning share precisions the demo never hits
//   - an inactive/closed position, and asset + liability accounts (net-worth breadth)
//
// Encoding (verified against the importer): pamt = cash * 100 (cents);
// samt = shares * 10^dec; price is derived cash/qty (the `rate` field is
// ignored by the importer). Run: `node data/samples/reference-ledger.gen.mjs`.
// =============================================================================
import { writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const items = [];
let ts = 1779300000000;
const T = () => String(ts++);
const push = (o) => { items.push({ ...o, ts: T() }); return o.id; };
const cents = (d) => String(Math.round(d * 100));
const shares = (q, dec) => String(Math.round(q * 10 ** dec));

// ---- currencies + securities ------------------------------------------------
push({ obj_type: "curr", id: "curr-usd", currid: "USD", name: "US Dollar",
       type: undefined, isbase: "1", dec: "2", pref: "$", suff: "", rate: "1.0", rrate: "1.0" });

// [id, ticker, name, dec, sec_type, sec_subtype]
const SECS = [
  ["MFA", "Reference Mutual Fund A", 9, "2", "Balanced"],       // high-precision mutual fund
  ["MFB", "Reference Mutual Fund B", 4, "2", "Equity Index"],
  ["MMF", "Reference Money Market",  4, "5", "Money Market"],   // $1-NAV fund
  ["EQA", "Reference Equity A",      2, "2", "Equity"],
  ["INT", "Reference International", 4, "2", "International"],
  ["BND", "Reference Bond A",        3, "3", "Income"],
  ["ETF", "Reference ETF A",         5, "2", "Exchange Traded Fund"],
];
const secCurr = {}, sAcct = {}, secDec = {};
for (const [tk, name, dec] of SECS) {
  secDec[tk] = dec;
  secCurr[tk] = push({ obj_type: "curr", id: `curr-${tk}`, currid: `^${tk}`, name, type: "s",
                       ticker: tk, dec: String(dec), rate: "1.0", rrate: "1.0", hide_in_ui: "0" });
}

// ---- accounts ---------------------------------------------------------------
const root = push({ obj_type: "acct", id: "acct-root", type: "r", name: "Reference Ledger", currid: "curr-usd", sbal: "0" });
const brk  = push({ obj_type: "acct", id: "acct-brk", type: "v", parentid: root, currid: "curr-usd", name: "Reference Brokerage", sbal: "0", applies_to_net_worth: "1", is_inactive: "0" });
const ira  = push({ obj_type: "acct", id: "acct-ira", type: "v", parentid: root, currid: "curr-usd", name: "Reference Rollover IRA", sbal: "0", applies_to_net_worth: "1", is_inactive: "0" });
const bank = push({ obj_type: "acct", id: "acct-bank", type: "b", parentid: root, currid: "curr-usd", name: "Reference Checking", sbal: "0", applies_to_net_worth: "1" });
const asset= push({ obj_type: "acct", id: "acct-asset", type: "a", parentid: root, currid: "curr-usd", name: "Reference Asset", sbal: "500000", applies_to_net_worth: "1" });
const liab = push({ obj_type: "acct", id: "acct-liab", type: "l", parentid: root, currid: "curr-usd", name: "Reference Loan", sbal: "-1200000", applies_to_net_worth: "1" });
// categories
const catDiv  = push({ obj_type: "acct", id: "cat-div", type: "i", parentid: root, currid: "curr-usd", name: "Dividend Income", sbal: "0" });
const catFee  = push({ obj_type: "acct", id: "cat-fee", type: "e", parentid: root, currid: "curr-usd", name: "Investment Fees", sbal: "0" });
const catXfer = push({ obj_type: "acct", id: "cat-xfer", type: "i", parentid: root, currid: "curr-usd", name: "Investments/Transfers", sbal: "0" });
const catMisc = push({ obj_type: "acct", id: "cat-misc", type: "e", parentid: root, currid: "curr-usd", name: "Misc Investment Expense", sbal: "0" });

// one 's' security sub-account per (investment account, security) actually used.
function sacct(acctV, tk, inactive = false) {
  const key = `${acctV}:${tk}`;
  if (!sAcct[key]) {
    const [, name, , sec_type, sec_subtype] = SECS.find((s) => s[0] === tk);
    sAcct[key] = push({ obj_type: "acct", id: `s-${acctV}-${tk}`, type: "s", parentid: acctV,
      currid: secCurr[tk], name, sec_type, sec_subtype, is_inactive: inactive ? "1" : "0",
      applies_to_net_worth: "1", cost_basis: "1", strike: "0", face_value: "0",
      option: "0", option_price: "0.0", sec_dividend: "0", bond_type: "0" });
  }
  return sAcct[key];
}

// ---- transactions (QIF-origin: blank invest.txntype) ------------------------
function txn(acctV, dt, qif, xtype, splits, extra = {}) {
  const o = { obj_type: "id" in extra ? "txn" : "txn", id: `txn-${items.length}`, acctid: acctV,
    dt: String(dt), td: String(dt), desc: qif, memo: qif,
    qif_invst_action: qif, xfer_type: xtype, stat: " ", dtentered: T(), ...extra };
  splits.forEach((sp, i) => {
    o[`${i}.acctid`] = sp.acct; o[`${i}.samt`] = sp.samt; o[`${i}.pamt`] = sp.pamt;
    o[`${i}.invest.splittype`] = sp.stype; o[`${i}.id`] = `${o.id}-${i}`;
    o[`${i}.desc`] = qif; o[`${i}.obj_type`] = "";
  });
  push(o);
}
// BUY: sec samt +, pamt - (cash out); optional fee
function buy(acctV, tk, qty, price, fee = 0) {
  const sp = [{ acct: sacct(acctV, tk), stype: "sec", samt: shares(qty, secDec[tk]), pamt: cents(-qty * price) }];
  if (fee) sp.push({ acct: catFee, stype: "fee", samt: cents(fee), pamt: cents(-fee) });
  txn(acctV, 20180115, "Buy", "xfrtp_buysell", sp);
}
// SELL: sec samt -, pamt + (cash in)
function sell(acctV, tk, qty, price, dt = 20200310, fee = 0) {
  const sp = [{ acct: sacct(acctV, tk), stype: "sec", samt: shares(-qty, secDec[tk]), pamt: cents(qty * price) }];
  if (fee) sp.push({ acct: catFee, stype: "fee", samt: cents(fee), pamt: cents(-fee) });
  txn(acctV, dt, "Sell", "xfrtp_buysell", sp);
}
// DIV cash: sec 0/0, inc leg (samt -, pamt +)
function divCash(acctV, tk, amt, dt = 20190401) {
  txn(acctV, dt, "Div", "xfrtp_dividend",
    [{ acct: sacct(acctV, tk), stype: "sec", samt: "0", pamt: "0" },
     { acct: catDiv, stype: "inc", samt: cents(-amt), pamt: cents(amt) }], { reinvest: "false" });
}
// DIV reinvest: sec (+shares, -cash), inc (-cash, +cash)
function divReinvest(acctV, tk, qty, amt, dt = 20191218) {
  txn(acctV, dt, "ReinvDiv", "xfrtp_dividend",
    [{ acct: sacct(acctV, tk), stype: "sec", samt: shares(qty, secDec[tk]), pamt: cents(-amt) },
     { acct: catDiv, stype: "inc", samt: cents(-amt), pamt: cents(amt) }], { reinvest: "true" });
}
// Transfer-OUT at the $1 placeholder (ShrsOut): sec (-shares, +shares*100 == $1/share) + xfr clearing
function dollarOut(acctV, tk, qty, dt, xferAcct = catXfer) {
  const s = shares(qty, secDec[tk]);
  const proceeds = Math.round(qty * 100);   // $1/share
  txn(acctV, dt, "ShrsOut", "xfrtp_buysellxfr",
    [{ acct: sacct(acctV, tk), stype: "sec", samt: String(-Number(s)), pamt: String(proceeds) },
     { acct: xferAcct, stype: "xfr", samt: String(proceeds), pamt: String(-proceeds) }]);
}
// Transfer-IN carrying real cost (ShrsIn): sec (+shares, -realcost) + xfr clearing
function sharesIn(acctV, tk, qty, price, dt, xferAcct = catXfer) {
  txn(acctV, dt, "ShrsIn", "xfrtp_buysellxfr",
    [{ acct: sacct(acctV, tk), stype: "sec", samt: shares(qty, secDec[tk]), pamt: cents(-qty * price) },
     { acct: xferAcct, stype: "xfr", samt: cents(-qty * price), pamt: cents(qty * price) }]);
}
// bank transfer into the brokerage cash (parent brokerage gains, bank split loses)
function bankXfer(acctV, amt, dt = 20180101) {
  txn(acctV, dt, "Xin", "xfrtp_bank",
    [{ acct: bank, stype: "xfr", samt: cents(-amt), pamt: cents(amt) }]);
}
function miscExp(acctV, tk, amt, dt = 20200601) {
  txn(acctV, dt, "MiscExp", "xfrtp_miscincexp",
    [{ acct: sacct(acctV, tk), stype: "sec", samt: "0", pamt: "0" },
     { acct: catMisc, stype: "exp", samt: cents(amt), pamt: cents(-amt) }]);
}

// --- brokerage: fund the account, buy a spread, dividends, a real sale --------
bankXfer(brk, 250000);
buy(brk, "EQA", 500, 62.50, 9.95);
buy(brk, "ETF", 300, 110.00, 4.95);
buy(brk, "MFA", 1234.567891, 32.1234);      // dec-9 fractional mutual fund
buy(brk, "MMF", 100000, 1.00);              // money-market $1-NAV
buy(brk, "BND", 200, 98.75);
buy(brk, "INT", 800, 41.10, 7.50);
divCash(brk, "EQA", 214.50);
divReinvest(brk, "MFA", 12.345678, 402.11);
sell(brk, "EQA", 200, 71.25, 20190815, 6.95);  // real gain, later date
miscExp(brk, "ETF", 15.00);
buy(brk, "EQA", 3, 68.40);                  // small odd-lot follow-on buy

// --- large-magnitude position (push the money boundary) ----------------------
buy(brk, "ETF", 250000, 305.7654, 49.95);

// --- IRA: a multi-lot rollover IN, then an ORPHAN $1 transfer-OUT of it -------
sharesIn(ira, "MFB", 424.392, 30.3900, 20060213);
sharesIn(ira, "MFB", 312.492, 32.6800, 20060213);
sharesIn(ira, "MFB", 100.000, 34.0400, 20060213);   // 3-lot rollover, one security+date
divReinvest(ira, "MFB", 22.008, 896.40, 20121214);
dollarOut(ira, "MFB", 858.892, 20130118);           // orphan $1 transfer-out of all shares

// --- PAIRED $1 transfer: brokerage EQA out -> IRA in, same day ---------------
dollarOut(brk, "INT", 800.000, 20210615);
sharesIn(ira, "INT", 800.000, 44.3130, 20210615);   // dest carries real carried cost

// --- self-referential buysellxfr (ADR-0053): transfer clearing back to brk ---
// A real-priced sellx whose xfr leg targets the SAME investment account.
txn(brk, 20190701, "Sold", "xfrtp_buysellxfr",
  [{ acct: sacct(brk, "BND", true), stype: "sec", samt: shares(-50, 3), pamt: cents(50 * 99.10) },
   { acct: brk, stype: "xfr", samt: cents(-50 * 99.10), pamt: cents(-50 * 99.10) }]);

// ---- emit -------------------------------------------------------------------
const out = {
  metadata: { exporter: "Coffer synthetic reference ledger", moneydance_build: 0,
    export_date: 20260729, file_name: "reference-ledger", file_path: "reference-ledger.moneydance", extensions: [] },
  all_items: items,
};
const dir = dirname(fileURLToPath(import.meta.url));
writeFileSync(join(dir, "reference-ledger.json"), JSON.stringify(out, null, 1));
console.log(`wrote reference-ledger.json: ${items.length} items ` +
  `(${items.filter(i => i.obj_type === "txn").length} txns, ` +
  `${items.filter(i => i.obj_type === "acct" && i.type === "s").length} security sub-accounts)`);

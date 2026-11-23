# 0115 — The Demo is generated, imported and seeded from the hub

* Status: Accepted (amends [0088](0088-setup-asks-one-question.md): setup asks no ledger
  question; amends [0095](0095-master-key-moves-to-a-welcome-screen.md): the welcome's next
  step is the hub's)
* Date: 2026-11-18
* Related: [0071](0071-install-provisioning-ui-import-authed-restore.md) (install
  provisioning, D5 starter categories), [0052](0052-moneydance-reimport-idempotency.md)
  (an import always creates a new ledger), [0086](0086-mcp-write-observability.md)
  (`ledger_operations`), [0091](0091-one-category-taxonomy.md) (the one taxonomy),
  [0016](0016-moneydance-account-translation.md) (Moneydance account translation),
  [0084](0084-trade-derived-security-prices.md) (price ranks, D6),
  [0112](0112-one-write-lock-per-ledger.md) (the write lock the examples' writers take),
  [0114](0114-benchmarks-and-currency-conversion.md) (conversion, which a Demo does not
  need, D7), [0104](0104-reports-are-a-place-you-go.md) (Reports, whose Demo dependency
  this meets).

## Context

ADR-0088 left setup one question: tick a box and the first user gets a Demo ledger. The
Demo behind that box had three problems.

- **It was frozen.** It was a JSON export generated once and embedded in the API, so its
  history ended on the day it was generated. Every month after that, the newest rows grew
  older, and the Overview, budgets and reminders of a fresh Demo described a past that
  never reached today.
- **It was thin.** A Moneydance export carries accounts, transactions, prices and
  reminders, and nothing Coffer adds on top: folders, budget targets, saved reports, a
  Home layout, review rows, merges, edits beside the bank's originals. A Demo that is an
  import alone shows none of the parts a new user most needs to see.
- **It was in the wrong place.** Setup is a security ceremony, and the Demo was its one
  question about data. It ran after setup committed and best-effort, so a slow or failing
  import was logged and dropped: the user landed on an empty hub with no word of why. And
  only the first user, once, could ever have one.

## Decision

### D1 — The Demo is generated when it is added, from a fixed seed and the date

A C# generator (`src/Api/Provisioning/Demo`) writes the Demo's Moneydance export when the
Demo is added: five years of a made-up two-earner household, ending on the day it is
added. Its inputs are a fixed seed and that date (`DemoSpec`, which a test may also cut to
the window's trailing months). It reads no clock and
draws from no runtime random source, so the same seed and date always give the same bytes.

- **The curated core stays a file.** `data/samples/moneydance-export-demo.base.json`,
  embedded in the API, holds what is chosen by hand: the category tree (ADR-0091's one
  taxonomy), the currencies and the file's settings. The generator writes the accounts,
  securities, prices, transactions and reminders around it. No generated export is
  checked in or embedded, and the JavaScript generator is retired.
- **It is read as an upload is.** The generator writes bytes, and the same reader that
  parses an uploaded export reads them. The Demo goes through exactly an upload's parse
  and import, under the same memory limit, and generation runs inside the import's memory
  slot.
- **The date is the caller's today.** The hub sends the browser's local date. The server
  takes it by the register's rule for today, so the last day's rows are never Scheduled,
  and uses its own date when none is sent. A date more than one day past the server's
  date is refused (`demo-date-in-future`): no time zone is that far ahead, so it means a
  wrong clock, and it would give a Demo with Scheduled rows.
- **Its categories come with it.** The core carries the whole tree, so a Demo is not given
  the starter catalogue on top (ADR-0088's rule, unchanged).

### D2 — The hub adds a Demo on every install; setup creates no ledger

**Setup asks nothing about ledgers** (amends ADR-0088). It creates no ledger, takes no
`includeDemo`, and its completion response carries no `ledgerId` or `ledgerName`. A
client still sending `includeDemo` is not refused: the field is ignored. The welcome panel
after setup points to the hub: create an empty ledger, add a Demo, or import a Moneydance
export (amends ADR-0095). Every install therefore starts on an empty hub.

**The hub offers the Demo on every install, to any signed-in user.** Its header actions
are, in order, Import from Moneydance, Demo ledger and New ledger, shown in every state.
The empty hub also offers New ledger and Add a Demo ledger as two cards. The Demo action
calls `POST /api/ledgers/demo?today=yyyy-MM-dd`, on the `/api/ledgers` group.

**Each press adds a new ledger.** There is no rebuild and no replace. The ledger is named
"Demo", then "Demo 2", "Demo 3" and so on (`DemoLedgerName.Next`): the smallest free
number, so a deleted number is reused. Names are compared trimmed and ignoring case, over
every ledger the caller can see, a shared one included. A failed build kept nothing, so
trying again takes the same name. A ledger named by hand while a build runs may share its
name: ledger names are not unique anywhere, and nothing here makes them so.

### D3 — The Demo is a background import job, with a phase of its own after the commit

The Demo runs through `ImportJobRunner`, as an upload does, so it has the same limits and
records:

- **One at a time.** One running import per user, the Demo included, and one parsed export
  in memory install-wide. Both refusals are the upload's `import-already-running`.
- **The import is an ordinary Moneydance import.** It creates a new ledger owned by the
  caller (ADR-0052) and lists in Activity as a Moneydance import, recorded as started by
  hand (`triggered_via` `manual`) rather than as a file upload. Its checks and skips are
  carried on the job as an upload's are, and the server log names each failed check; the
  hub does not show them. A Demo that fails a check or skips a row is a test failure (the generator's
  and the whole Demo's tests), not a state that ships.
- **Found again by the hub.** `GET /api/ledgers/demo/job` returns the caller's latest Demo
  job, running or finished, or 204 when there is none. The hub reads it when it opens and
  every second while the job runs, so a build survives leaving the hub and coming back. A
  later import of any kind replaces it, as finished jobs are replaced today.
- **Two phases.** A Demo job is in its `import` phase until the import's one transaction
  commits, then in its `examples` phase, which adds what an export cannot carry. The job
  publishes the new ledger's id as soon as the import commits, so from then on, while the
  job runs, the hub shows the build on its own row and the ledger list names no example
  missing. The export and the memory slot are released before
  the examples phase, so the next import need not wait for it. A user's own Moneydance
  import has no examples phase.
- **The examples, in order.** The examples phase is a fixed list of steps, each its own
  step of the hub's progress, each committed on its own and through the writers the app's
  own endpoints use, with the checks those endpoints make first. First it finds every row
  the generator's manifest names in the new ledger, by its Moneydance id, and every later
  step needs that. Then it files the everyday bank accounts and the retirement accounts
  into a folder each; sets the brokerage's trade-commission flag, the target-date fund's
  look-through and the hand-priced fund's quarter-end prices; sets a year of budget
  targets, ending in the Demo's month; renames a few imported rows, whose first edit keeps
  the bank's values beside them as their originals, hides the copies the bank sent twice
  (an imported row is hidden, never deleted), and marks every other row of the checking
  account's last closed statement reconciling, the rest staying uncleared (the generator
  counts the half without the re-sent copies, which the statement does not list, so no
  hidden row is marked), every check made before the first of these writes; imports a
  card's latest closed statement as a person does, saving its CSV layout and reading the
  statement through that saved layout into the card's review queue, under the import's own
  Activity entry, then merging each statement row the owner had already typed in into that
  typed row, which survives, settled (the statement is read through the layout first, as
  the import dialog previews it, and each typed row is matched to exactly one statement row
  of its day and amount before anything is written; the importer has the app's file
  readers and no feed or quote source, so nothing reaches out); posts an
  estimated bill's recent occurrences from a
  new reminder and only then turns its estimate and its auto-post on, adds the mortgage as
  a managed loan reminder and skips the next occurrence of an imported reminder, its first
  after the Demo's last day (that reminder's bills post on their own day of the month,
  weekends and holidays included, so no occurrence on or before that day is still due; a
  skip also skips every earlier occurrence still due, so were one due, the reminders step
  would be refused before it writes anything); saves three private reports
  and pins them before two of the report catalog's; saves a Home layout that shows
  them, unless its owner saved one first, which is kept; and takes one manual snapshot,
  the owner's, after every other example so that a restore of it brings them all back
  (captured through the service role, as the snapshot endpoint captures, so it holds every
  member's rows; a Demo already at the snapshot limit fails the step, since a manual
  snapshot never replaces another). Last, it runs the ledger's
  consistency check, which must find every projection healthy; it never repairs one, since
  a repair would hide the defect. A step that needs one that failed is skipped, saying
  which (every step needs the first; the Home layout needs the reports), and what a step
  wrote before another failed stays.
- **A lossy import gets no examples.** When the import carried a failed check or a
  skipped row, the examples phase adds nothing and records why ("not started: the import
  reported problems"): examples over a history known to be incomplete would hide that it
  is.
- **The examples are their own Activity entry.** The phase records itself on the Demo's
  `ledger_operations` row, provider `demo`, family `ingest`, started by hand, which Activity
  lists as "Demo examples" with how many of its steps were added, and which the ledger list
  reads (D9). A row closed partial opens in Activity to its message, the steps not added
  and why, since it records no error rows for ADR-0055 D5's rule to open it on (settled by
  Claude, 2026-11-19). The import opens the row inside its own transaction, so a Demo never exists
  without it. The examples phase claims it before its first step, in one write that only
  an unclaimed row takes, so the examples run once per Demo, and stamps the row's start
  then, so Activity lists the examples after the import they follow. Its details hold one
  entry per step, keyed by the step, 1 added and 0 not, written after each step. It is not
  an import, so undo-import refuses it (`operation-not-an-import`), dry run included. The
  card statement the examples import is a file import with its own entry, provider
  `csv-generic`, listed newest first above the examples that ran it. Its `triggered_via` is
  `manual`, as the Demo's Moneydance import's is: nothing was uploaded, and the hub action
  started it. The file importer records `file-upload` unless its caller names another value,
  and only this step does. Undo-import accepts that entry: it removes the statement's rows
  still in review, and the ones merged stay folded into the rows they were merged into. The
  app offers that undo only in the import dialog, for the import the dialog has just run,
  so nothing in the app undoes a Demo's statement; only a direct call to the endpoint does.
- **Failure is before the commit, or not at all.** A Demo job fails only at or before the
  import's commit, so a failed job kept nothing, and its error says so: "Building *name*
  failed and nothing was kept. Try again, or check the server logs." Once the import has
  committed the job succeeds. An example that could not be added does not fail the job; it
  is reported on the ledger (D9).

### D4 — The examples go through the app's own writers and checks, and fail closed

An example is a state a person could have reached through the app, or it is not added.

- **The repositories the endpoints call.** Each step calls the repository its endpoint
  calls, on a service-role context of its own, naming the Demo's ledger in every call, and
  each writer takes the ledger's write lock as it always does (ADR-0112). No step writes a
  row by hand, and none replays an endpoint over HTTP. The service role bypasses row-level
  security, as the import and the feed and auto-post jobs do, so the ledger id is what
  keeps a step inside its Demo: it comes only from the job's own import, and the whole
  Demo's test asserts that every other ledger is unchanged (D8).
- **The endpoints' checks, shared.** Where an endpoint makes a check its repository does
  not, that check is one internal static method that the endpoint and the step both call,
  and the endpoint's own tests still pin it: a budget target's amount
  (`BudgetEndpoints.IsAllowedTargetAmount`); a reminder's recurrence, estimate, edit and
  auto-post (`RemindersEndpoints.ValidateRecurrence`, `RejectIneligibleEstimate`,
  `ValidateBankEdit` and `EnableAutoPostIfRequestedAsync`, with `PostingValidation` and
  `TagValidation`); a saved report, its spec read as the summary endpoint reads one
  (`SavedReportsEndpoints.Validate`, `ReportsEndpoints.ReadSummarySpec`); a Home layout
  (`PreferencesEndpoints.CheckDashboard`); a register edit and a bulk reconcile
  (`TransactionsEndpoints.ValidatePatchBody`, `ValidateBulkReconStatus`). The account
  checks an endpoint makes in line (the account is the ledger's, or is a bank-shape
  account) the step makes through the same repository calls. Where the repository checks,
  the step relies on it: a folder's name (the repository's own `CheckName`, which the step
  calls first, as the endpoint does) and members, a fund's look-through and prices,
  the CSV layout (validated again once saved, as the import endpoint validates a saved
  layout before it reads through it), the merges, the skip and the snapshot limit. A
  refusal is worded as the endpoint words it (`BusinessError.Describe`).
- **Fail closed.** Any outcome of a writer but success fails the step, with the writer's
  reason; no step works around a refusal. The first step finds every row the manifest
  names exactly once, or fails, and every later step with it. A step is recorded as added
  or not as a whole, so each step makes every check it can before its first write, and a
  step refused by a check is unwritten rather than half-written. What a step wrote before
  a writer refused it stays (D3).
- **Run once, recorded as it goes.** The Demo's `ledger_operations` row (D3) is opened in
  the import's own transaction, so there is no Demo without it. Only a run that claims it,
  in one guarded write that takes only a row still running with nothing recorded, adds
  examples; any other run is refused and logged and leaves the row as it is, so it can
  neither add the examples twice nor rewrite the record of the first. Each step's result
  is written as soon as it is known, and the row is closed once, whatever happened:
  completed, partial, or failed when the first step was not added. Both writes touch only
  a row still running, so a row the sweep has closed stays closed.
- **Its own time.** The phase never runs on a request's cancellation. Its own is tied to
  the server stopping and to a limit of ten minutes, and a cancelled run skips what is
  left ("skipped: cancelled"). Every date a step computes comes from the Demo's last day,
  never from the clock, which stamps only the times the row and the writers record; the
  examples a Demo gets depend on its date, not on when the phase runs.
- **The startup sweep.** A job lives in memory, so no job is running when the server
  starts, and a Demo row still running then is an orphan. `DemoMarkerSweep` closes each one
  at start: partial, with "Interrupted: the server restarted before these examples were
  added: …" naming the steps it does not record as added, or completed when it records
  every one (D9).
- **Checked, never repaired.** The last step runs the ledger's consistency check at the
  Demo's date, and a projection that disagrees fails the step, named. The step never runs
  the repair: a repair would hide a defect one of the writers made.

### D5 — What is seeded, and what is not

**Seeded:** the examples D3 lists, each something an export cannot carry: folders, the
investment settings and hand-entered prices, budget targets, register edits beside the
bank's originals, hidden re-sent copies, a half-reconciled statement, a card statement
imported through a saved CSV layout with its review rows and merges, reminders (an
estimated bill with auto-post on, a managed loan reminder, a skipped occurrence), saved
reports and their pins, a Home layout, and one snapshot. Each is the owner's, made as the
owner would make it: the saved reports are private, and the pins, the Home layout and the
snapshot are the owner's.

**Not seeded:** pending rows, other users and invites, connected apps, feeds, webhooks,
tokens, benchmarks, a quote-refresh schedule and exchange rates.

- Nothing seeded adds a person, a credential or an outside source to the install, and
  nothing in a Demo makes a request outside it until its owner turns quotes on (D6). The
  card statement's importer has the app's file readers and no feed or quote source (D3).
- A pending row is written only by a bank feed, and a Demo has none.
- The one schedule a Demo has is the reminder auto-post job, which its auto-commit
  reminders turn on as they do on any ledger. A Demo left in use therefore posts those
  reminders' bills as their days come, as any ledger with auto-post on does.

### D6 — Real tickers, priced automatically once quotes are on

- **Real tickers, generic names.** The Demo's securities carry real exchange tickers under
  made-up generic names: index funds (one of them sold out of along the way), a money-market
  fund, two stocks, one of which splits on its real date, and a target-date fund both
  earners' plans hold. One stable-value fund has no ticker; the examples enter its prices
  by hand at quarter-ends (D3).
- **Automatic pricing on.** Every security keeps the `auto_price` an import gives it, which
  is on. The stable-value fund has no ticker, so nothing fetches it, and its setting is not
  changed.
- **Generated prices until then.** The history's prices are the generator's: the export's
  daily closes (`import`) and the prices of its own trades (`trade`), both on synthetic
  levels. A new ledger fetches no quotes until a user turns a quote source on for it, and
  the Demo seeds no quote schedule (D5), so until then every price in it is its own.
- **The consequence (ADR-0084).** A fetched close outranks both generated sources for the
  days it covers. Once quotes are on, real prices land on synthetic holdings: from the
  first fetched day the holdings are valued at the market's level, which need not be the
  generated one, so the Demo's value can move at once by the difference. This is accepted
  with the real tickers: with them, turning quotes on in a Demo works as it does on a real
  ledger.

### D7 — US dollars only

Every account, security, price and example in a Demo is in US dollars, the curated core's
one currency, and its business days follow the US Federal Reserve's holidays. No figure in
a Demo needs an exchange rate, which it does not seed (D5), and none of its totals depends
on one. A Demo therefore does not show the conversion of ADR-0114; more than one currency
stays covered by test-built ledgers.

### D8 — One test class adds the whole Demo; every other test uses a short span

- **`FullDemoTests` adds the whole Demo once.** It adds the five-year Demo through the
  hub's endpoint and the job, at the tests' fixed date, in a database of its own, and every
  one of its facts reads that one Demo: a lossless import, every example added and none
  named missing, each example's state, the consistency check healthy, every other ledger
  unchanged, no outside source, and the shapes the history must keep. The build has a
  ceiling of three minutes, and reaching it is a failure.
- **Short spans elsewhere.** Every other test that imports a Demo imports a short span of
  the same generator at the same date (`DemoSpan.LastMonths`): the trailing twelve or three
  months of bank-route rows, with the whole investment route and every account, security,
  price and reminder, each real account ending on the whole Demo's balance. Every
  export row the manifest names lies in the shortest span, so the examples run on it too.
- **The generator's own tests do not import.** They generate the whole Demo in memory, and
  golden hashes pin its bytes, so a change in what it writes is a deliberate one.

The suite pays for one whole Demo per run, and a test that needs a Demo of its own gets a
short one.

### D9 — The hub shows the build as a row, and says what became of it

A Demo being built is a row in the hub's list, after the ledgers.

- **Progress.** One scale numbers the import's steps and then the examples', and the row
  says "Step *n* of *m* — *label*", where *n* is the step in progress and the label is a
  fixed readable phrase for it (`DemoProgressLabels`), from the job's `stepNumber` and
  `stepLabel`. The import reports a step once it is done, so the step in progress is the
  next one in the import's ordered step list. A bar shows the same fraction. The Moneydance
  wizard keeps its own wording for a user's import.
- **Preparing.** While the job runs, in either phase, the row is not a link and says
  Preparing. Once the ledger exists (from the import's commit) its own list row is hidden,
  so the Demo is listed once.
- **Failed.** When the job failed, or the request itself failed after the name was chosen
  (its ProblemDetails carries a `ledgerName` extension), the row says "Building *name*
  failed and nothing was kept. Try again, or check the server logs." and offers Try again,
  which adds the Demo again. The sentence is the same whatever the reason; the reason is
  in the server log, the refusal for lack of memory included. There is no dismiss: the
  next press replaces it.
- **Interrupted.** When the hub last saw the job running, the server now has no such job,
  and no ledger by that name has appeared that was not there when the build started, the
  row says "Building *name* stopped because the server restarted. Nothing was kept.", with
  Try again. The hub keeps what it last saw in `sessionStorage` (`coffer.demoJob`); without
  storage it still sees this while it stays open.
- **Examples incomplete.** A Demo whose import committed but whose examples did not all
  finish is an ordinary, openable ledger, counted with the rest, with a warning line under
  it: "The history is complete, but some examples could not be added: *names*." The names
  come from `GET /api/ledgers`, whose items carry `demoExamplesMissing` (an array of names,
  or null), read from the Demo's own `ledger_operations` row, provider `demo`. It is null
  for a ledger with no such row, for one whose examples all finished, and for one whose job
  is still running, which shows as Preparing instead. A restart during the examples phase
  keeps the Demo: at the next start, its row still running is closed as partial, naming
  the steps it does not record as added (as completed when it records every one), so the
  Demo lists as examples incomplete. Nothing runs the examples again.
- **Refused before a name.** A refusal that names no Demo (another import is running, the
  date is refused, or the request failed before a name was chosen) is an alert line under
  the header with the server's message. It is not a row, because no Demo was reserved.
- **Succeeded.** The row becomes the ledger's ordinary row. The row itself opens the
  ledger; there is no Open button.

The panel's count is of ledgers that can be opened: a preparing, failed or interrupted
row is not counted, and an examples-incomplete ledger is.

## Consequences

- An install that is already set up never sees setup again, so it changes only in the
  hub: the Demo ledger action appears. A Demo from the old setup box is an ordinary ledger
  and is not changed. It counts for numbering, so the next Demo is "Demo 2".
- More than one person on an install can have a Demo, and one person can have several.
- A Demo added on different days differs: its history ends on the day it was added.
- The API has no consumer but the SPA (the setup routes included), so removing
  `includeDemo`, `ledgerId` and `ledgerName` from setup breaks no contract.
- The server keeps no record of a failed build once a later import replaces the job, so a
  build that failed while the hub was closed reads as interrupted. Both kept nothing, and
  Try again is the remedy for either.
- Audit retention prunes old `ledger_operations` rows, so an examples-incomplete warning
  can eventually go with them. A snapshot restore keeps them, since the run log survives a
  restore. A ledger with no Demo row reads as a plain ledger.
- A Demo starts with one of its ledger's snapshots taken: a manual one, which counts
  toward the ledger's limit and which no scheduled snapshot evicts, so it stays until
  someone deletes it (ADR-0037).
- Once a user turns quotes on for a Demo, its holdings are valued at real prices against
  its synthetic history (D6).

## Fixes in the import the Demo runs through

A Demo that is an ordinary import has to import losslessly, so the import it runs through
was held to that. Each fix is a defect, not an option, and the last four change every
Moneydance import:

- **The Demo's checks are kept.** The setup Demo returned only its ledger, and a failed
  check or a skip was logged and dropped. The Demo now carries them on its job, as an
  upload does, though no screen shows a Demo job's checks (D3). What keeps a Demo
  lossless is the generator: it writes only the investment actions ADR-0027 maps, so the
  importer declines none of its rows, and tests fail if any check fails or any row is
  skipped.
- **A bank-route skip is listed.** A transaction the bank route declined, or dropped as a
  second copy of an OFX transaction id, was only counted, so the no-dropped-transactions
  check passed. Each is now listed with its reason, and a count without its list fails the
  check ([moneydance-import-fidelity.md](../moneydance-import-fidelity.md), Dropped
  transactions).
- **An auto-commit reminder turns auto-post on.** An import that brings a reminder set to
  auto-commit now turns the ledger's auto-post job on, in the import's transaction, as
  saving such a reminder does.
- **A security's type is read from the security account**, where Moneydance writes it
  (ADR-0016, amendment of 2026-11-18).
- **An account keeps its tax-status guess.** The importer guesses each account's tax
  status from its name and type ([ADR-0066](0066-account-tax-status.md)), but the account
  insert left the column out, so every imported account had none. The insert now writes
  it. Accounts imported earlier keep the tax status they have.

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

import ts from 'typescript';
import { describe, expect, it } from 'vitest';

import { WHOLE_TREE_SCAN_TIMEOUT_MS } from '@/lib/wholeTreeScan';

/**
 * One rule for a button that is busy (ADR-0023 §L).
 *
 * THE RULE. A button whose request is pending keeps focus and refuses to act.
 * It says so through the shared mechanism in pending.ts: `pending` on Button,
 * IconButton, RowActionsButton and an ActionFooter slot, `isConfirming` on
 * ConfirmDialog, and usePendingButtonProps on a raw button. It never says so with
 * the native disabled attribute: a focused button that becomes disabled drops
 * focus to the page, and after a refusal from the server nothing is focused.
 * Native disabled is for a button that is unavailable for another reason: a
 * required field still empty, nothing changed yet, no permission.
 *
 * WHAT IT CHECKS. Every disabled in the SPA's source, found by parsing it:
 *  - a JSX attribute named `disabled`, or ending in `Disabled` (a component's
 *    `confirmDisabled`, `newDisabled`), on any element;
 *  - a `disabled` property written in an object literal anywhere (an
 *    ActionFooter slot, a menu's item, an object spread into an element).
 * One on a field is not a button's and is the field rule's (below): an input
 * other than a button-typed one (submit, button, reset, image), a select,
 * textarea, option or optgroup, a fieldset that holds no button, or a
 * component in FIELDS. A
 * disabled fieldset disables every button inside it, so one that holds a
 * button, a button-typed input or a component not in FIELDS is a button's.
 * Every other disabled is a button's, and
 *  - fails outright, by file and line, when its expression reads a request's
 *    pending state: a TanStack flag (FLAGS), a `status` compared with
 *    'pending', one of TanStack's hooks that count requests in flight, a name
 *    that says a request is in flight (BUSY_WORDS), or a name or field the
 *    file binds to any of these. A name is bound by declaration, assignment,
 *    parameter default or destructuring, where a destructured flag is bound by
 *    the property it is read from (`{ isPending: locked }`), and a `status`
 *    read into a name (`const st = m.status`, `{ status: st }`) counts when
 *    that name is compared with 'pending'. A function of the same file whose
 *    body reads a pending state binds its name, so calling it reads one, and
 *    so does a value or helper made by useMemo or useCallback from such a
 *    function. A disabled in an object that is a branch of a condition that
 *    reads a pending state (`{...(save.isPending ? { disabled: true } : {})}`)
 *    reads it too;
 *  - otherwise is counted per file in REVIEWED, with why the button is
 *    unavailable rather than busy. A count that differs fails, and so does a
 *    file that is not listed, so a new one fails until it is reviewed.
 * A hand-written `aria-disabled` or `aria-busy` that reads a pending state
 * fails too: it would mark the button without refusing the click, so a second
 * press would send a second request. Every other hand-written aria-disabled on
 * a non-field, and aria-busy on a button (a button tag, a button-typed input,
 * role="button", or a component not in FIELDS), is counted per file in
 * REVIEWED's `marks`, so one that reads a pending state the detector cannot
 * see still needs a reason. An aria-busy on a region (a list that is loading)
 * is not a button's and is not counted. Every file is parsed, with no text
 * filter in front.
 *
 * Not caught: one disabled removed and another added in the same file, which
 * leaves its count as it was; a pending state handed in from another file
 * under a name that does not say so (a prop named `disabled`), which is what
 * the review of each counted use is for; a useState flag set around a request
 * under a name not in BUSY_WORDS; a renamed useTransition flag; a disabled
 * set on an object by assignment (`props.disabled = true`) rather than written
 * in its literal; an input whose type is not written as a literal; a helper
 * wrapped in a call other than useMemo or useCallback; and a menu item's
 * disabled built by a function in another file than its menu. `!isSuccess`,
 * `status !== 'success'` and `data === undefined` are counted for review, not
 * failed: each also holds after a failed load, which is a reason a button is
 * unavailable.
 */

const SRC = join(import.meta.dirname, '..', '..');

/** TanStack's flags for a request in flight. */
const FLAGS = new Set([
    'isPending', 'isLoading', 'isFetching', 'isFetchingNextPage', 'isFetchingPreviousPage',
    'isRefetching', 'isInitialLoading', 'fetchStatus',
]);

/** TanStack's hooks that count the requests in flight. */
const COUNTING_HOOKS = new Set(['useIsMutating', 'useIsFetching', 'useMutationState']);

/** Words in a name that say a request is in flight, matched case-blind anywhere in it. */
const BUSY_WORDS = ['pending', 'busy', 'saving', 'submitting', 'working', 'inflight', 'loading', 'fetching'];

/** The intrinsic fields, whose disabled is not a button's. An input and a fieldset are decided by isField. */
const FIELD_TAGS = new Set(['input', 'select', 'textarea', 'fieldset', 'option', 'optgroup']);

/** The input types that make an `<input>` a button. */
const BUTTON_INPUT_TYPES = new Set(['submit', 'button', 'reset', 'image']);

/** The hooks whose value, or helper, is made by the function they are given. */
const MEMO_HOOKS = new Set(['useMemo', 'useCallback']);

/**
 * The components whose disabled goes to a field, with what the field is. A
 * button that belongs to one (a picker's toggle) is disabled with its field.
 */
const FIELDS: ReadonlyMap<string, string> = new Map([
    ['Input', 'The text input primitive.'],
    ['Checkbox', 'The checkbox primitive.'],
    ['Typeahead', 'The payee and security search field.'],
    ['DateField', 'A date input with its keyboard shortcuts.'],
    ['AccountCategoryPicker', 'The account and category picker, a field with its list.'],
    ['RetentionInput', 'A number field for how long backups are kept.'],
    ['SettingRow', 'A checkbox row in the upgrade settings.'],
    ['RegisterLeadHeaderCells', 'The register header\'s select-all checkbox.'],
    ['SecurityField', 'The investment editor\'s security search.'],
    ['SharesField', 'The investment editor\'s shares input.'],
    ['PriceField', 'The investment editor\'s price input.'],
    ['AmountField', 'The investment editor\'s amount input.'],
    ['CategoryField', 'The investment editor\'s category picker.'],
    ['TransferField', 'The investment editor\'s transfer account picker.'],
    ['FeeField', 'The investment editor\'s fee amount and fee category.'],
]);

interface Review {
    /** How many counted disabled uses the file holds. */
    count: number;
    /** How many hand-written aria-disabled or aria-busy on a button it holds (none when absent). */
    marks?: number;
    /** Why each is unavailable rather than busy. */
    reason: string;
}

/**
 * Every file with a counted disabled, how many, and why none of them is a
 * request in flight. A component that hands its own `disabled` prop on is
 * listed with who sets that prop.
 */
const REVIEWED: ReadonlyMap<string, Review> = new Map<string, Review>([
    // The primitives: each hands on a disabled its caller sets, and this rule checks the caller.
    ['components/ui/ActionFooter.tsx', {
        count: 3,
        reason: 'Each slot\'s disabled, set by the caller for an unavailable action; a busy one is the slot\'s pending.',
    }],
    ['components/ui/ConfirmDialog.tsx', {
        count: 1,
        reason: 'Confirm is unavailable until the typed phrase matches, or when the caller says so; a request in flight is isConfirming.',
    }],
    ['components/ui/ContextMenu.tsx', {
        count: 1,
        reason: 'A menu item its menu marks as not applying to the row; no menu marks one for a request in flight.',
    }],
    ['components/ui/RowActionsButton.tsx', {
        count: 1,
        reason: 'The caller\'s disabled, handed to IconButton; a row with a request in flight passes pending instead.',
    }],
    ['components/tags/TagSelect.tsx', {
        count: 1,
        reason: 'The tags field\'s edit button when its tags cannot be changed here; while the editor saves it is pending.',
    }],
    ['routes/ledgers/structure/StructureToolbar.tsx', {
        count: 1,
        reason: 'New is unavailable when the list failed to load; while it is still loading, New is pending.',
    }],
    ['routes/ledgers/structure/StructureGroupHeading.tsx', {
        count: 1,
        reason: 'A group cannot be folded while a search is open: the search opens every group with a match.',
    }],
    ['routes/ledgers/register/shell/RegisterToolbar.tsx', {
        count: 1,
        reason: 'New transaction, unavailable while a new transaction is already being written or the account has no currency.',
    }],
    ['routes/ledgers/register/shell/RegisterControlsBar.tsx', {
        count: 1,
        reason: 'Hands the register\'s newDisabled on to New transaction: an editor is open, or the account has no currency.',
    }],
    ['routes/ledgers/register/shell/RegisterBulkActionBar.tsx', {
        count: 3,
        reason: 'Delete, Unhide and Move are unavailable for a selection with a row this account does not own; Unhide in flight is pending.',
    }],
    ['routes/ledgers/reports/RangePicker.tsx', {
        count: 1,
        reason: 'The range button when the host says the range cannot change; no host passes a request in flight.',
    }],
    ['routes/ledgers/reports/BenchmarkPicker.tsx', {
        count: 0,
        marks: 2,
        reason: 'A saved benchmark pick the ledger no longer holds, and a benchmark that cannot be drawn for this '
            + 'reader (no history with Yahoo off, or no rate into the line\'s currency), are listed as options that '
            + 'cannot be picked, focusable so the reason is heard (ADR-0114 D1); neither reads a request.',
    }],
    ['routes/ledgers/home/HomeHeader.tsx', {
        count: 1,
        reason: 'Customise is unavailable when the host says the stored layout failed to load (customiseDisabled); '
            + 'while it loads, Customise is pending.',
    }],
    ['routes/landing/DemoBuildRow.tsx', {
        count: 0,
        marks: 1,
        reason: 'A Demo still being built is a list row, not a button or a link: it has no ledger to open yet '
            + '(ADR-0115 D9). The mark says the row cannot be opened; it reads no request.',
    }],
    // The sites: every one is a choice not yet made, a field still empty, nothing changed, or no permission.
    ['components/imports/LayoutNameDialog.tsx', {
        count: 1,
        reason: 'Save is unavailable while the name is empty or already taken; the write in flight is the slot\'s pending.',
    }],
    ['components/RecoveryCodes.tsx', {
        count: 1,
        reason: 'Continue is unavailable until the reader says the codes are saved.',
    }],
    ['components/RenameConnectionForm.tsx', {
        count: 1,
        reason: 'Save is unavailable for a blank name where a blank is not allowed (a token).',
    }],
    ['components/SidebarAccounts.tsx', {
        count: 1,
        reason: 'The placeholder item of a row menu with no actions, which is never available.',
    }],
    ['lib/api/adminUsers.ts', {
        count: 1,
        reason: 'The request body that turns a user\'s account on or off; not a button at all.',
    }],
    ['routes/account/AccountSecurityPage.tsx', {
        count: 3,
        reason: 'Remove is unavailable for the last passkey with no recovery codes; Add and Generate until a name is typed.',
    }],
    ['routes/imports/ImportLedgerPage.tsx', {
        count: 2,
        reason: 'Analyze needs a file chosen, and Create ledger a name typed.',
    }],
    ['routes/invite/InvitePage.tsx', {
        count: 1,
        reason: 'Create account is unavailable until the username and display name are valid.',
    }],
    ['routes/landing/LandingPage.tsx', {
        count: 1,
        reason: 'Create is unavailable until the new ledger has a name.',
    }],
    ['routes/ledgers/accounts/AccountEditorDialog.tsx', {
        count: 2,
        reason: 'Set up scheduled payment needs a source and a date; Save cannot save an account whose details failed to load.',
    }],
    ['routes/ledgers/accounts/AccountsManagementPage.tsx', {
        count: 1,
        reason: 'The toolbar is unavailable when the accounts failed to load; while they load it is pending.',
    }],
    ['routes/ledgers/bank-edit/fields/PostingRowEditor.tsx', {
        count: 3,
        reason: 'The leg menu\'s Move up and Move down at the ends of the list, and Remove on the last leg it cannot lose.',
    }],
    ['routes/ledgers/BudgetPage.tsx', {
        count: 1,
        reason: 'This month is unavailable when this month is already shown.',
    }],
    ['routes/ledgers/components/AddSecurityDialog.tsx', {
        count: 1,
        reason: 'Save is unavailable until the security form is valid.',
    }],
    ['routes/ledgers/home/HomePage.tsx', {
        count: 4,
        reason: 'Customise is unavailable when the stored layout failed to load; while it loads, Customise is pending. '
            + 'Add a report… and the Add of a hidden tile are unavailable once the most report tiles are shown, and a '
            + 'report already on the screen is listed as added, not offered again; while the saved reports load, '
            + 'Add a report… is pending.',
    }],
    ['routes/ledgers/home/WidgetHost.tsx', {
        count: 2,
        reason: 'Move up on the first widget of a column and Move down on the last.',
    }],
    ['routes/ledgers/home/WidgetPanel.tsx', {
        count: 5,
        reason: 'A widget\'s menu items after the stored layout failed to load (no widget is drawn while it loads), '
            + 'and Save while a setting is incomplete.',
    }],
    ['routes/ledgers/home/widgets/WatchlistWidget.tsx', {
        count: 1,
        reason: 'Settings is unavailable after the stored layout failed to load; no widget is drawn while it loads.',
    }],
    ['routes/ledgers/investment-edit/InvestmentTxnRowEdit.tsx', {
        count: 1,
        reason: 'Save is unavailable for an invalid draft with no merge candidate chosen; a save in flight is pending.',
    }],
    ['routes/ledgers/TxnRowEdit.tsx', {
        count: 1,
        reason: 'Save is unavailable for invalid postings with no merge candidate chosen; a save in flight is pending.',
    }],
    ['routes/ledgers/register/bank/BankRegisterPage.tsx', {
        count: 4,
        reason: 'New while an editor is open or the account has no currency; Delete, Unhide and Move for a selection it does not own.',
    }],
    ['routes/ledgers/register/bank/bankRowMenu.ts', {
        count: 1,
        reason: 'Show other side on a row with no other side.',
    }],
    ['routes/ledgers/register/investment/InvestmentRegisterPage.tsx', {
        count: 5,
        reason: 'Show other side with no other side; New while an editor is open; Delete, Unhide, Move for rows it does not own.',
    }],
    ['routes/ledgers/register/shell/CsvMappingStep.tsx', {
        count: 3,
        reason: 'Save, Check and Continue need a mapping document; while one is read or written they are pending.',
    }],
    ['routes/ledgers/register/shell/ImportFileDialog.tsx', {
        count: 3,
        reason: 'Upload needs a file and a brokerage, Import a chosen statement, and Undo something imported.',
    }],
    ['routes/ledgers/register/shell/MoveToAccountDialog.tsx', {
        count: 1,
        reason: 'Move is unavailable until a target account is chosen.',
    }],
    ['routes/ledgers/reports/SaveReportControl.tsx', {
        count: 1,
        reason: 'Save is unavailable until the report has a name.',
    }],
    ['routes/ledgers/SecurityDetailPage.tsx', {
        count: 2,
        reason: 'Save is unavailable for an invalid security, and for a price with no valid amount or date.',
    }],
    ['routes/ledgers/settings/BenchmarksSection.tsx', {
        count: 1,
        reason: 'Add is unavailable with three benchmarks, with the reader\'s Yahoo opt-in off or not yet read, and until a symbol is typed.',
    }],
    ['routes/ledgers/settings/CategoriesPanel.tsx', {
        count: 3,
        reason: 'Merge where the category cannot merge away, folding a group while a search is open, and the toolbar after a failed load.',
    }],
    ['routes/ledgers/settings/CategoryDialogs.tsx', {
        count: 2,
        reason: 'Move is unavailable when the parent is unchanged, and Merge until a target is chosen.',
    }],
    ['routes/ledgers/settings/components/RestoreSnapshotDialog.tsx', {
        count: 1,
        reason: 'Restore is unavailable until the reader confirms the ledger data will be replaced.',
    }],
    ['routes/ledgers/settings/FeedConnectionsPanel.tsx', {
        count: 3,
        reason: 'Connect needs a setup token, Map a chosen account, Apply a date.',
    }],
    ['routes/ledgers/settings/ExchangeRatesPanel.tsx', {
        count: 1,
        reason: 'Save is unavailable until a valid day and a positive rate are given, and for a day that already holds a rate or has not begun (UTC).',
    }],
    ['routes/ledgers/settings/GeneralPanel.tsx', {
        count: 1,
        reason: 'Rename, offered to the owner alone, needs a new, non-empty name.',
    }],
    ['routes/ledgers/settings/MembersPanel.tsx', {
        count: 1,
        reason: 'Remove is unavailable for the sole owner of the ledger.',
    }],
    ['routes/ledgers/settings/NotificationsPanel.tsx', {
        count: 1,
        reason: 'Add target is unavailable until a subscriber and a URL are given.',
    }],
    ['routes/ledgers/settings/ReportingCurrencyPanel.tsx', {
        count: 1,
        reason: 'Change currency, offered to the owner alone, needs a currency other than the stored one.',
    }],
    ['routes/ledgers/settings/SnapshotsPanel.tsx', {
        count: 1,
        reason: 'A new snapshot is unavailable once the ledger holds the most it keeps.',
    }],
    ['routes/ledgers/settings/TagDialogs.tsx', {
        count: 1,
        reason: 'Merge is unavailable until a target tag is chosen.',
    }],
    ['routes/ledgers/settings/TagsPanel.tsx', {
        count: 2,
        reason: 'Merge needs another tag to merge into, and the toolbar is unavailable after a failed load.',
    }],
    ['routes/login/LoginPage.tsx', {
        count: 1,
        reason: 'Sign in is unavailable until a username is typed.',
    }],
    ['routes/login/RecoveryLoginPage.tsx', {
        count: 1,
        reason: 'Sign in is unavailable until a username and a recovery code are typed.',
    }],
    ['routes/setup/SetupPage.tsx', {
        count: 2,
        reason: 'Create account needs valid names, and Restore a backup file and its passphrase.',
    }],
    ['routes/system/AppearancePanel.tsx', {
        count: 2,
        reason: 'Cancel and Save have nothing to do until the appearance is changed.',
    }],
    ['routes/system/BackupsPanel.tsx', {
        count: 2,
        reason: 'Create backup is unavailable once the schedule has been read with no passphrase set, or failed to '
            + 'read (pending while it is read); the retention Save needs a changed value.',
    }],
    ['routes/system/components/ConnectDriveDialog.tsx', {
        count: 1,
        reason: 'Continue needs a client id and a client secret.',
    }],
    ['routes/system/components/RestoreBackupCard.tsx', {
        count: 1,
        reason: 'Restore needs a file, its passphrase, the confirmation, and the key acknowledged where one is asked for.',
    }],
    ['routes/system/components/SetBackupPassphraseDialog.tsx', {
        count: 1,
        reason: 'Set needs a long enough passphrase, typed the same twice.',
    }],
    ['routes/system/MasterKeyPanel.tsx', {
        count: 1,
        reason: 'Rotate is unavailable until the confirmation phrase is typed.',
    }],
    ['routes/system/McpSettingsPanel.tsx', {
        count: 2,
        reason: 'MCP and AI writes cannot be switched here when the server\'s configuration forces them.',
    }],
    ['routes/system/NotificationsPanel.tsx', {
        count: 1,
        reason: 'Add target is unavailable until a subscriber and a URL are given.',
    }],
    ['routes/system/UpgradePanel.tsx', {
        count: 3,
        reason: 'Upgrade needs the host ready and the phrase typed, the blocked one says why, and Save token needs a token.',
    }],
    ['routes/system/UsersPanel.tsx', {
        count: 3,
        reason: 'The sole enabled admin keeps their admin and their account; the other is the account flag sent to the server.',
    }],
    ['routes/__styleguide/StyleGuidePage.tsx', {
        count: 2,
        reason: 'The style guide\'s samples of a disabled button and a tags field that is not disabled.',
    }],
]);

function sourceFiles(dir: string): string[] {
    const out: string[] = [];
    for (const entry of readdirSync(dir)) {
        if (entry === 'node_modules' || entry === 'dist') continue;
        const full = join(dir, entry);
        if (statSync(full).isDirectory()) out.push(...sourceFiles(full));
        // Test code and its fixtures press buttons; they disable none the app shows.
        else if (/\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry) && !/Fixtures?\.tsx?$/.test(entry)) {
            out.push(full);
        }
    }
    return out;
}

function rel(file: string): string {
    return file.slice(SRC.length + 1).replaceAll('\\', '/');
}

/**
 * The lines where one source text takes TanStack's useMutation, which clears a
 * refusal at the press of a retry, rather than the SPA's (lib/mutation.ts):
 *  - an import of `useMutation` from TanStack Query, under its name or another;
 *  - a re-export of it, under any name, and `export *` or `export * as` of the
 *    module, which re-exports it with the rest;
 *  - a namespace import, an import-equals or a dynamic import of the module,
 *    since what is read through it is not followed: the SPA imports TanStack's
 *    other hooks by name.
 * The module is TanStack Query or any path inside it.
 */
function tanstackMutationTakes(name: string, text: string): number[] {
    const source = ts.createSourceFile(
        name, text, ts.ScriptTarget.Latest, true,
        name.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
    );
    const isTanstack = (specifier: ts.Node | undefined) => specifier !== undefined
        && ts.isStringLiteralLike(specifier)
        && (specifier.text === '@tanstack/react-query' || specifier.text.startsWith('@tanstack/react-query/'));
    const takesHook = (elements: readonly (ts.ImportSpecifier | ts.ExportSpecifier)[]) =>
        elements.some((e) => (e.propertyName ?? e.name).text === 'useMutation');
    const lines: number[] = [];
    const visit = (node: ts.Node) => {
        let takes = false;
        if (ts.isImportDeclaration(node) && isTanstack(node.moduleSpecifier)) {
            const bindings = node.importClause?.namedBindings;
            takes = bindings !== undefined && (ts.isNamespaceImport(bindings) || takesHook(bindings.elements));
        } else if (ts.isExportDeclaration(node) && isTanstack(node.moduleSpecifier)) {
            const clause = node.exportClause;
            takes = clause === undefined || ts.isNamespaceExport(clause) || takesHook(clause.elements);
        } else if (ts.isImportEqualsDeclaration(node) && ts.isExternalModuleReference(node.moduleReference)) {
            takes = isTanstack(node.moduleReference.expression);
        } else if (ts.isCallExpression(node) && node.expression.kind === ts.SyntaxKind.ImportKeyword) {
            takes = isTanstack(node.arguments[0]);
        }
        if (takes) lines.push(source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1);
        ts.forEachChild(node, visit);
    };
    visit(source);
    return lines;
}

/** The text of a name or string literal; undefined for anything else. */
function nameText(node: ts.Node | undefined): string | undefined {
    if (node === undefined) return undefined;
    if (ts.isIdentifier(node) || ts.isPrivateIdentifier(node) || ts.isStringLiteral(node)
        || ts.isNoSubstitutionTemplateLiteral(node)) {
        return node.text;
    }
    if (ts.isComputedPropertyName(node)) return nameText(node.expression);
    return undefined;
}

const saysBusy = (name: string) => {
    const lower = name.toLowerCase();
    return BUSY_WORDS.some((word) => lower.includes(word));
};

/** A field read as a name: `state.busy`. */
const fieldText = (node: ts.PropertyAccessExpression) => node.getText().replace(/\s+/g, '');

/** `node` without the parentheses around it. */
function bare(node: ts.Expression): ts.Expression {
    let n = node;
    while (ts.isParenthesizedExpression(n)) n = n.expression;
    return n;
}

/** The names and fields a file binds to a pending state, and to a request's status. */
interface Aliases {
    /** Bound to a read of a pending state: reading one reads it. */
    pending: Set<string>;
    /** Bound to a request's `status`: pending when compared with 'pending'. */
    status: Set<string>;
}

/**
 * A request's status, read as `x.status`, `x['status']`, `status`, or a name or
 * field bound to one.
 */
function isStatusRead(node: ts.Expression, aliases: Aliases): boolean {
    const n = bare(node);
    if (ts.isPropertyAccessExpression(n)) return n.name.text === 'status' || aliases.status.has(fieldText(n));
    if (ts.isElementAccessExpression(n)) return nameText(n.argumentExpression) === 'status';
    if (ts.isIdentifier(n)) return n.text === 'status' || aliases.status.has(n.text);
    return false;
}

/** A status compared with 'pending', either way round, by any equality. */
function isStatusPending(node: ts.Node, aliases: Aliases): boolean {
    if (!ts.isBinaryExpression(node)) return false;
    const kind = node.operatorToken.kind;
    if (kind !== ts.SyntaxKind.EqualsEqualsEqualsToken && kind !== ts.SyntaxKind.EqualsEqualsToken
        && kind !== ts.SyntaxKind.ExclamationEqualsEqualsToken && kind !== ts.SyntaxKind.ExclamationEqualsToken) {
        return false;
    }
    const isPendingText = (side: ts.Expression) => {
        const n = bare(side);
        return (ts.isStringLiteral(n) || ts.isNoSubstitutionTemplateLiteral(n)) && n.text === 'pending';
    };
    return (isStatusRead(node.left, aliases) && isPendingText(node.right))
        || (isStatusRead(node.right, aliases) && isPendingText(node.left));
}

/**
 * Whether `expr` reads a request's pending state, directly or through a name
 * or field in `aliases`. A nested function is not part of it: a function the
 * file names is followed through its name instead (pendingAliases).
 */
function readsPending(expr: ts.Node, aliases: Aliases, skip?: (node: ts.Node) => boolean): boolean {
    const walk = (node: ts.Node): boolean => {
        if (ts.isFunctionLike(node) || skip?.(node) === true) return false;
        if (isStatusPending(node, aliases)) return true;
        if (ts.isCallExpression(node) && ts.isIdentifier(node.expression) && COUNTING_HOOKS.has(node.expression.text)) {
            return true;
        }
        if (ts.isIdentifier(node)) return FLAGS.has(node.text) || saysBusy(node.text) || aliases.pending.has(node.text);
        if (ts.isPropertyAccessExpression(node)) {
            const name = node.name.text;
            if (FLAGS.has(name) || saysBusy(name) || aliases.pending.has(fieldText(node))) return true;
            return walk(node.expression);
        }
        if (ts.isElementAccessExpression(node)) {
            const field = nameText(node.argumentExpression);
            if (field !== undefined && (FLAGS.has(field) || saysBusy(field))) return true;
        }
        return ts.forEachChild(node, walk) ?? false;
    };
    return walk(expr);
}

/** Whether a function's body reads a pending state: the one function whose boundary is lifted. */
function bodyReadsPending(fn: ts.Node, aliases: Aliases, skip?: (node: ts.Node) => boolean): boolean {
    if ((ts.isArrowFunction(fn) || ts.isFunctionExpression(fn) || ts.isFunctionDeclaration(fn)) && fn.body !== undefined) {
        return readsPending(fn.body, aliases, skip);
    }
    return false;
}

/** Whether `node` is a useMemo or useCallback whose function reads a pending state. */
function memoReadsPending(node: ts.Expression, aliases: Aliases, skip?: (node: ts.Node) => boolean): boolean {
    const n = bare(node);
    return ts.isCallExpression(n) && ts.isIdentifier(n.expression) && MEMO_HOOKS.has(n.expression.text)
        && n.arguments.length > 0 && bodyReadsPending(bare(n.arguments[0]!), aliases, skip);
}

const isAssignment = (kind: ts.SyntaxKind) =>
    kind >= ts.SyntaxKind.FirstAssignment && kind <= ts.SyntaxKind.LastAssignment;

/**
 * Every name and field in the file bound to a read of a pending state, and to
 * a request's status. Bound: a variable, parameter or binding given one, a
 * name or field assigned one, a function whose body reads one, a value or
 * helper made by useMemo or useCallback from such a function, and a
 * destructured property that is a flag or says it is busy, whatever it is read
 * from. A pattern given one binds every name in it. By name rather than by
 * scope, so a name bound to one anywhere in the file counts everywhere in it;
 * that can only report more, never less. A node `skip` says yes to is no read
 * of a pending state, and a destructured property it says yes to binds nothing.
 */
function pendingAliases(source: ts.SourceFile, skip?: (node: ts.Node) => boolean): Aliases {
    const aliases: Aliases = { pending: new Set(), status: new Set() };
    for (let grew = true; grew;) {
        grew = false;
        const note = (set: Set<string>, name: string) => {
            if (!set.has(name)) { set.add(name); grew = true; }
        };
        const bind = (target: ts.Node, set: Set<string> = aliases.pending): void => {
            if (ts.isIdentifier(target)) note(set, target.text);
            else if (ts.isPropertyAccessExpression(target)) note(set, fieldText(target));
            else if (ts.isObjectBindingPattern(target) || ts.isArrayBindingPattern(target)) {
                for (const element of target.elements) if (ts.isBindingElement(element)) bind(element.name, set);
            } else if (ts.isParenthesizedExpression(target)) bind(target.expression, set);
        };
        const visit = (node: ts.Node) => {
            if ((ts.isVariableDeclaration(node) || ts.isParameter(node) || ts.isBindingElement(node))
                && node.initializer !== undefined
                && (readsPending(node.initializer, aliases, skip) || memoReadsPending(node.initializer, aliases, skip)
                    || (ts.isVariableDeclaration(node) && bodyReadsPending(bare(node.initializer), aliases, skip)))) {
                bind(node.name);
            } else if (ts.isBinaryExpression(node) && isAssignment(node.operatorToken.kind)
                && readsPending(node.right, aliases, skip)) {
                bind(node.left);
            }
            if (ts.isFunctionDeclaration(node) && node.name !== undefined && bodyReadsPending(node, aliases, skip)) {
                note(aliases.pending, node.name.text);
            }
            // A destructured property is bound by what it is: `{ isPending: locked }`.
            if (ts.isBindingElement(node) && ts.isObjectBindingPattern(node.parent)
                && skip?.(node.propertyName ?? node.name) !== true) {
                const property = nameText(node.propertyName ?? node.name);
                if (property !== undefined && (FLAGS.has(property) || saysBusy(property))) bind(node.name);
                else if (property === 'status') bind(node.name, aliases.status);
            }
            // A status read into a name: `const st = m.status`.
            if (ts.isVariableDeclaration(node) && node.initializer !== undefined
                && isStatusRead(node.initializer, aliases)) {
                bind(node.name, aliases.status);
            }
            ts.forEachChild(node, visit);
        };
        visit(source);
    }
    return aliases;
}

/** One disabled the rule looked at. */
interface Use {
    line: number;
    /** What it is on, for the report: `<Button> disabled`, `{ disabled }`. */
    on: string;
    /** It reads a pending state. */
    pending: boolean;
}

interface Scan {
    /** Every disabled on a button, by line. */
    uses: Use[];
    /** Every hand-written aria-disabled or aria-busy that reads a pending state. */
    marked: Use[];
    /** Every other hand-written aria-disabled on a non-field, and aria-busy on a button: counted for review. */
    marks: Use[];
    /** Every disabled on a field, by line. */
    fieldUses: Use[];
    /** Every hand-written readOnly, aria-disabled or aria-busy on a field that reads a pending state. */
    fieldMarked: Use[];
    /** Every field that spreads pendingChoiceProps and has its own handler beside it. */
    choiceClashes: Use[];
}

/** The handlers pendingChoiceProps takes, which a field passes to it rather than beside it. */
const CHOICE_HANDLERS = new Set(['onChange', 'onClick', 'onKeyDown', 'onMouseDown']);

const isDisabledName = (name: string) => name === 'disabled' || /[a-z]Disabled$/.test(name);

type Element = ts.JsxOpeningElement | ts.JsxSelfClosingElement;

/** The element a JSX attribute sits on. */
const elementOf = (attribute: ts.JsxAttribute): Element => attribute.parent.parent;

/** The value of an element's attribute when it is written as a string, else undefined. */
function literalAttribute(element: Element, name: string): string | undefined {
    for (const property of element.attributes.properties) {
        if (!ts.isJsxAttribute(property) || nameText(property.name) !== name) continue;
        const value = property.initializer;
        if (value !== undefined && ts.isStringLiteral(value)) return value.text;
        const inner = value !== undefined && ts.isJsxExpression(value) && value.expression !== undefined
            ? bare(value.expression) : undefined;
        if (inner !== undefined && (ts.isStringLiteral(inner) || ts.isNoSubstitutionTemplateLiteral(inner))) {
            return inner.text;
        }
    }
    return undefined;
}

/** Whether an element is a button: a button tag, a button-typed input, role="button", or a component not a field. */
function isButtonLike(element: Element): boolean {
    const tag = element.tagName.getText();
    if (tag === 'button' || literalAttribute(element, 'role') === 'button') return true;
    if (tag === 'input') return BUTTON_INPUT_TYPES.has(literalAttribute(element, 'type') ?? '');
    return /^[A-Z]/.test(tag) && !FIELDS.has(tag);
}

/** Whether a fieldset holds a button, which its disabled disables with it. */
function holdsButton(fieldset: Element): boolean {
    if (!ts.isJsxOpeningElement(fieldset)) return false;
    const walk = (node: ts.Node): boolean => {
        if ((ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) && isButtonLike(node)) return true;
        return ts.forEachChild(node, walk) ?? false;
    };
    return fieldset.parent.children.some(walk);
}

/** Whether an element is a field, whose disabled is not a button's. */
function isField(element: Element): boolean {
    const tag = element.tagName.getText();
    if (tag === 'input') return !BUTTON_INPUT_TYPES.has(literalAttribute(element, 'type') ?? '');
    if (tag === 'fieldset') return !holdsButton(element);
    return FIELD_TAGS.has(tag) || FIELDS.has(tag);
}

/**
 * The element an object literal is given to, if no function lies between:
 * through the attribute it is written in (an ActionFooter slot), or a spread
 * into the element.
 */
function elementHolding(node: ts.Node): { element: Element; attribute?: ts.JsxAttribute } | undefined {
    for (let up = node.parent; up !== undefined; up = up.parent) {
        if (ts.isJsxAttribute(up)) return { element: elementOf(up), attribute: up };
        if (ts.isJsxSpreadAttribute(up)) return { element: up.parent.parent };
        if (ts.isFunctionLike(up) || ts.isSourceFile(up) || ts.isJsxElement(up) || ts.isJsxSelfClosingElement(up)) {
            return undefined;
        }
    }
    return undefined;
}

/**
 * Whether an object literal is a branch of a condition that reads a pending
 * state: `cond ? { … } : …`, `cond ? … : { … }`, `cond && { … }`.
 */
function isPendingBranch(literal: ts.Node, aliases: Aliases): boolean {
    let node: ts.Node = literal;
    while (ts.isParenthesizedExpression(node.parent)) node = node.parent;
    const up = node.parent;
    if (ts.isConditionalExpression(up) && (up.whenTrue === node || up.whenFalse === node)) {
        return readsPending(up.condition, aliases);
    }
    if (ts.isBinaryExpression(up) && up.operatorToken.kind === ts.SyntaxKind.AmpersandAmpersandToken && up.right === node) {
        return readsPending(up.left, aliases);
    }
    return false;
}

/** What one source text holds, named `name`. */
function scanOf(name: string, text: string): Scan {
    const source = ts.createSourceFile(
        name, text, ts.ScriptTarget.Latest, true,
        name.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
    );
    const aliases = pendingAliases(source);
    const out: Scan = { uses: [], marked: [], marks: [], fieldUses: [], fieldMarked: [], choiceClashes: [] };
    const lineOf = (node: ts.Node) => source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1;
    const visit = (node: ts.Node) => {
        if (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) {
            const properties = node.attributes.properties;
            const spreadsChoice = properties.some((p) => {
                if (!ts.isJsxSpreadAttribute(p)) return false;
                const spread = bare(p.expression);
                return ts.isCallExpression(spread) && calleeName(spread) === 'pendingChoiceProps';
            });
            if (spreadsChoice) {
                for (const p of properties) {
                    const name = ts.isJsxAttribute(p) ? nameText(p.name) : undefined;
                    if (name !== undefined && CHOICE_HANDLERS.has(name)) {
                        out.choiceClashes.push({ line: lineOf(p), on: `<${node.tagName.getText()}> ${name}`, pending: false });
                    }
                }
            }
        }
        if (ts.isJsxAttribute(node)) {
            const attribute = nameText(node.name) ?? node.name.getText(source);
            const element = elementOf(node);
            const tag = element.tagName.getText();
            const value = node.initializer;
            const expression = value === undefined ? undefined
                : ts.isJsxExpression(value) ? value.expression : value;
            const pending = expression !== undefined && readsPending(expression, aliases);
            const use = { line: lineOf(node), on: `<${tag}> ${attribute}`, pending };
            if (isDisabledName(attribute)) {
                (isField(element) ? out.fieldUses : out.uses).push(use);
            } else if ((attribute === 'aria-disabled' || attribute === 'aria-busy') && !isField(element)) {
                if (pending) out.marked.push(use);
                else if (attribute === 'aria-disabled' || isButtonLike(element)) out.marks.push(use);
            } else if ((attribute === 'readOnly' || attribute === 'aria-disabled' || attribute === 'aria-busy')
                && isField(element) && pending) {
                out.fieldMarked.push(use);
            }
        }
        if ((ts.isPropertyAssignment(node) || ts.isShorthandPropertyAssignment(node)) && nameText(node.name) === 'disabled'
            && ts.isObjectLiteralExpression(node.parent)) {
            const holder = elementHolding(node.parent);
            const value = ts.isPropertyAssignment(node) ? node.initializer : node.name;
            const tag = holder?.element.tagName.getText();
            const use = {
                line: lineOf(node),
                on: holder === undefined ? '{ disabled }'
                    : holder.attribute === undefined ? `<${tag} {...}> { disabled }`
                        : `<${tag} ${holder.attribute.name.getText(source)}> { disabled }`,
                pending: readsPending(value, aliases) || isPendingBranch(node.parent, aliases),
            };
            (holder !== undefined && isField(holder.element) ? out.fieldUses : out.uses).push(use);
        }
        ts.forEachChild(node, visit);
    };
    visit(source);
    return out;
}

/** What the rule fails a file for, given its review, or none when it is not listed. */
function failuresOf(file: string, scan: Scan, review: Review | undefined): string[] {
    const out: string[] = [];
    for (const use of scan.uses.filter((u) => u.pending)) {
        out.push(`${file}:${use.line} — ${use.on} reads a pending request. A busy button keeps focus: pass the `
            + 'pending state as `pending` (Button, IconButton, an ActionFooter slot), `isConfirming` (ConfirmDialog) '
            + 'or usePendingButtonProps (a raw button), and keep disabled for a button that is unavailable.');
    }
    for (const use of scan.marked) {
        out.push(`${file}:${use.line} — ${use.on} is written by hand from a pending request, which marks the button `
            + 'without refusing its click. Use `pending` or usePendingButtonProps, which do both.');
    }
    const counted = scan.uses.filter((u) => !u.pending);
    const listed = review?.count ?? 0;
    if (counted.length !== listed) {
        out.push(`${file} disables a button ${counted.length} times (lines ${counted.map((u) => u.line).join(', ') || 'none'}), `
            + `REVIEWED says ${listed}. A button is disabled only when it is unavailable, not while it is busy; `
            + 'list each one with why it is unavailable.');
    }
    const marksListed = review?.marks ?? 0;
    if (scan.marks.length !== marksListed) {
        out.push(`${file} writes aria-disabled or aria-busy on a button by hand ${scan.marks.length} times (lines `
            + `${scan.marks.map((u) => u.line).join(', ') || 'none'}), REVIEWED's marks says ${marksListed}. A busy `
            + 'button uses `pending` or usePendingButtonProps, which also refuse its click; list each other one with why.');
    }
    return out;
}

// ---------------------------------------------------------------------------
// The press: it holds its button, a label never narrows it, and an error
// stays until its result.
// ---------------------------------------------------------------------------

/** The attributes of a pending element that a press runs. */
const PRESS_ATTRIBUTES = new Set(['onClick', 'onConfirm']);

/**
 * A component's own press props, run by a button it draws whose pending state
 * it is given (`pending` or `isConfirming`): a press under another name.
 */
const COMPONENT_PRESS_ATTRIBUTES: ReadonlyMap<string, ReadonlySet<string>> = new Map([
    ['RegisterDeleteConfirm', new Set(['onConfirmSingle', 'onConfirmBulk'])],
]);

/** The elements that are a button, whose press a request-starting handler must hold. */
const PRESSABLE_TAGS = new Set(['button', 'Button', 'IconButton', 'RowActionsButton', 'ConfirmDialog']);

/** The calls that start a request. */
const REQUEST_CALLS = new Set(['mutate', 'mutateAsync', 'refetch', 'fetchNextPage', 'fetchPreviousPage']);

/** The calls that put what they are given past the press. */
const DEFERRING_CALLS = new Set(['setTimeout', 'queueMicrotask', 'requestAnimationFrame']);

/** A setter of an error the page shows. */
const ERROR_SETTER = /^set\w*(Errors?|Err|Collision|Refusal|Problems?)$/;

/**
 * The tags whose children are a button's label, and set its width. IconButton
 * is not one: it is square at a fixed size, whatever icon it shows.
 */
const BUTTON_TAGS = new Set(['button', 'Button']);

type FunctionNode = ts.ArrowFunction | ts.FunctionExpression | ts.FunctionDeclaration;

/** PendingLabel and a `pendingLabel` are the mechanism: what a label reads through them is theirs. */
function theMechanism(node: ts.Node): boolean {
    if (ts.isJsxSelfClosingElement(node)) return node.tagName.getText() === 'PendingLabel';
    if (ts.isJsxElement(node)) return node.openingElement.tagName.getText() === 'PendingLabel';
    if (ts.isIdentifier(node)) return node.text === 'pendingLabel';
    if (ts.isPropertyAccessExpression(node)) return node.name.text === 'pendingLabel';
    return false;
}

/** A name for a label (`label`, `savingLabel`, `submittingLabel`): it holds a label's text, whatever word it has. */
const isLabelName = (name: string) => name === 'label' || /[a-z]Label$/.test(name);

/**
 * A name or field that holds a label. In a label, it is no read of a pending
 * state by its name alone, a busy word in it ("saving") included: only by what
 * it is bound to (pressFailuresOf's label aliases).
 */
function holdsLabel(node: ts.Node): boolean {
    const text = ts.isIdentifier(node) ? node.text : ts.isPropertyAccessExpression(node) ? node.name.text : undefined;
    return text !== undefined && isLabelName(text);
}

const isFunctionNode = (node: ts.Node): node is ts.ArrowFunction | ts.FunctionExpression =>
    ts.isArrowFunction(node) || ts.isFunctionExpression(node);

/**
 * The functions a file names, by name: a function declaration, or a const
 * given a function or a useCallback of one. Two components of a file may name
 * one each; both are listed.
 */
function namedFunctions(source: ts.SourceFile): Map<string, FunctionNode[]> {
    const out = new Map<string, FunctionNode[]>();
    const add = (name: string, fn: FunctionNode) => out.set(name, [...(out.get(name) ?? []), fn]);
    const visit = (node: ts.Node) => {
        if (ts.isFunctionDeclaration(node) && node.name !== undefined && node.body !== undefined) add(node.name.text, node);
        if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer !== undefined) {
            let init = bare(node.initializer);
            if (ts.isCallExpression(init) && ts.isIdentifier(init.expression) && init.expression.text === 'useCallback'
                && init.arguments.length > 0) {
                init = bare(init.arguments[0]!);
            }
            if (isFunctionNode(init)) add(node.name.text, init);
        }
        ts.forEachChild(node, visit);
    };
    visit(source);
    return out;
}

/** Every node of a function's own body, not of a function nested in it (a callback runs later). */
function ownNodes(fn: FunctionNode): ts.Node[] {
    const out: ts.Node[] = [];
    const visit = (node: ts.Node) => {
        out.push(node);
        if (ts.isFunctionLike(node)) return;
        ts.forEachChild(node, visit);
    };
    if (fn.body !== undefined) ts.forEachChild(fn, (child) => { if (child === fn.body) visit(child); });
    return out;
}

/** The name a call calls: `f()` → f, `m.mutate()` → mutate, `window.setTimeout()` → setTimeout. */
function calleeName(call: ts.CallExpression): string | undefined {
    const callee = bare(call.expression);
    if (ts.isIdentifier(callee)) return callee.text;
    if (ts.isPropertyAccessExpression(callee)) return callee.name.text;
    return undefined;
}

/**
 * An argument that clears: null, undefined or '', an empty object or list (a
 * map of errors by field emptied), or an updater that returns one of those.
 */
const clears = (node: ts.Expression | undefined): boolean => {
    if (node === undefined) return true;
    const n = bare(node);
    if (ts.isObjectLiteralExpression(n)) return n.properties.length === 0;
    if (ts.isArrayLiteralExpression(n)) return n.elements.length === 0;
    if (ts.isArrowFunction(n) && !ts.isBlock(n.body)) return clears(n.body);
    return n.kind === ts.SyntaxKind.NullKeyword || (ts.isIdentifier(n) && n.text === 'undefined')
        || ((ts.isStringLiteral(n) || ts.isNoSubstitutionTemplateLiteral(n)) && n.text === '');
};

/** A press: the handler of a pending button, or a form's submit handler, which a submit button's press runs. */
interface Press {
    handler: ts.Expression;
    on: string;
}

/**
 * What the press rules fail one source text for, named `name`:
 *  - a same-file function a press runs, called or handed on anywhere that is
 *    not a press: another way into the action calls it past the button, so its
 *    press is not held, where clicking the button would share the hold;
 *  - a press that puts its request past the press (a timer, or a request
 *    after an await): the hold covers what starts while the handler runs;
 *  - a raw button that spreads the pending props and has its own onClick too,
 *    one replacing the other's hold or handler;
 *  - a button whose label reads a pending state: its children, a slot's
 *    `label` or `pendingLabel`, and a `label` or `…Label` attribute, the
 *    `pendingLabel` itself and PendingLabel's own `label` and `pendingLabel`
 *    among them. The label goes through `pendingLabel` or PendingLabel, which
 *    lay both labels out from the first render so the button never changes
 *    size; a label that changes with a pending state of its own would change
 *    the box while it is drawn. A label that swaps for the button's own
 *    request while the button is pending with its group is a PendingLabel
 *    given that request's pending state;
 *  - a press that starts a request and clears an error the page shows (an
 *    error setter given null, an empty map or list, or a reset()), in any
 *    function the press runs: the error stays until the new result replaces
 *    or clears it, or the button moves under the pointer;
 *  - a button that starts a request and is not in the mechanism at all (no
 *    `pending`, no `isConfirming`, no pending props, a slot with no `pending`):
 *    it is neither held nor refused, so a second press sends a second request.
 *    Those are returned apart (`unheld`), for UNHELD_REVIEWED.
 * What a press runs: its handler, a same-file function it names, and a
 * same-file function either of those calls.
 */
function pressFailuresOf(name: string, text: string): { failures: string[]; labels: string[]; unheld: string[] } {
    const source = ts.createSourceFile(
        name, text, ts.ScriptTarget.Latest, true,
        name.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
    );
    // For labels, a name that holds a label is bound only by what it reads, and
    // a PendingLabel read into a name is the mechanism's (its own labels are
    // checked where it is drawn).
    const labelAliases = pendingAliases(source, (node) => holdsLabel(node) || theMechanism(node));
    const functions = namedFunctions(source);
    const lineOf = (node: ts.Node) => source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1;
    const failures: string[] = [];
    const fail = (node: ts.Node, what: string) => failures.push(`${name}:${lineOf(node)} — ${what}`);

    // The makers of a raw button's pending props: `const p = usePendingButtonProps()`.
    const makers = new Set<string>();
    const findMakers = (node: ts.Node) => {
        if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer !== undefined) {
            const init = bare(node.initializer);
            if (ts.isCallExpression(init) && ts.isIdentifier(init.expression) && init.expression.text === 'usePendingButtonProps') {
                makers.add(node.name.text);
            }
        }
        ts.forEachChild(node, findMakers);
    };
    findMakers(source);

    const presses: Press[] = [];
    const labels: string[] = [];
    const labelReadsPending = (node: ts.Node, what: string) => labels.push(`${name}:${lineOf(node)} — ${what} reads a `
        + 'pending state. A label that changes while pending goes through `pendingLabel` (Button, an ActionFooter '
        + 'slot) or PendingLabel, which lay both labels out from the first render so the button never changes size, '
        + 'and neither label reads a pending state itself. A label that swaps for the button\'s own request is a '
        + 'PendingLabel given that request\'s pending state.');
    // A name holds a busy word only inside another word ("spending"), or is a
    // label's own name ("savingLabel"): not a pending state, in a label, unless
    // it is bound to one.
    const notALabelRead = (node: ts.Node) => {
        if (theMechanism(node)) return true;
        const text = ts.isIdentifier(node) ? node.text : ts.isPropertyAccessExpression(node) ? node.name.text : undefined;
        if (text === undefined || FLAGS.has(text) || !saysBusy(text)) return false;
        if (labelAliases.pending.has(ts.isIdentifier(node) ? text : fieldText(node as ts.PropertyAccessExpression))) {
            return false;
        }
        if (isLabelName(text)) return true;
        const words = text.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase().split(/[\s_]+/);
        const pairs = words.slice(1).map((word, i) => words[i] + word);
        return ![...words, ...pairs].some((word) => BUSY_WORDS.includes(word));
    };
    // A label read through a function called in place, `{(() => …)()}`, reads what its body reads.
    const labelReads = (expression: ts.Expression) => {
        const n = bare(expression);
        const callee = ts.isCallExpression(n) ? bare(n.expression) : undefined;
        if (callee !== undefined && isFunctionNode(callee)) return readsPending(callee.body, labelAliases, notALabelRead);
        return readsPending(expression, labelAliases, notALabelRead);
    };
    // A component drawn as the label, given a pending state, may swap its text on it.
    const checkComponentChild = (element: Element, tag: string) => {
        const component = element.tagName.getText(source);
        if (!/^[A-Z]/.test(component) || component === 'PendingLabel') return;
        for (const property of element.attributes.properties) {
            if (!ts.isJsxAttribute(property)) continue;
            const value = property.initializer !== undefined && ts.isJsxExpression(property.initializer)
                ? property.initializer.expression : undefined;
            if (value !== undefined && labelReads(value)) {
                labelReadsPending(property, `<${tag}>'s child <${component}> given ${nameText(property.name) ?? 'a prop'} that`);
            }
        }
    };
    const checkChild = (child: ts.JsxChild, tag: string) => {
        if (ts.isJsxExpression(child)) {
            if (child.expression !== undefined && labelReads(child.expression)) {
                labelReadsPending(child, `<${tag}>'s children`);
            }
        } else if (ts.isJsxElement(child)) {
            checkComponentChild(child.openingElement, tag);
            for (const inner of child.children) checkChild(inner, tag);
        } else if (ts.isJsxSelfClosingElement(child)) {
            checkComponentChild(child, tag);
        } else if (ts.isJsxFragment(child)) {
            for (const inner of child.children) checkChild(inner, tag);
        }
    };
    // Buttons that start a request outside the mechanism, and the handlers they run.
    const unheldPresses: Press[] = [];

    const visit = (node: ts.Node) => {
        if (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) {
            const tag = node.tagName.getText(source);
            let pendingElement = false;
            let spreadsPendingProps = false;
            let ownOnClick: ts.JsxAttribute | undefined;
            const handlers: Array<{ expression: ts.Expression; attribute: string }> = [];
            const pressable = PRESSABLE_TAGS.has(tag) || literalAttribute(node, 'role') === 'button';
            for (const property of node.attributes.properties) {
                if (ts.isJsxAttribute(property)) {
                    const attribute = nameText(property.name) ?? property.name.getText(source);
                    const value = property.initializer !== undefined && ts.isJsxExpression(property.initializer)
                        ? property.initializer.expression : undefined;
                    if (attribute === 'pending' || attribute === 'isConfirming') pendingElement = true;
                    if (attribute === 'onClick') ownOnClick = property;
                    if (value !== undefined
                        && (PRESS_ATTRIBUTES.has(attribute) || COMPONENT_PRESS_ATTRIBUTES.get(tag)?.has(attribute))) {
                        handlers.push({ expression: value, attribute });
                    }
                    // A button's label given as its `children` prop.
                    if (value !== undefined && attribute === 'children'
                        && (BUTTON_TAGS.has(tag) || literalAttribute(node, 'role') === 'button') && labelReads(value)) {
                        labelReadsPending(property, `<${tag}>'s children`);
                    }
                    if (value !== undefined && tag === 'form' && attribute === 'onSubmit') {
                        presses.push({ handler: value, on: '<form> onSubmit' });
                    }
                    // A label named for the pending state (`pendingLabel`, an editor's
                    // `submittingLabel` that it hands to one) and PendingLabel's own
                    // labels are the mechanism's, and read no pending state themselves:
                    // the mechanism lays them out from the first render, and a label
                    // that changes with a pending state would change the box.
                    if (value !== undefined && (attribute === 'label' || /[a-z]Label$/.test(attribute))
                        && !isField(node) && labelReads(value)) {
                        labelReadsPending(property, `<${tag}> ${attribute}`);
                    }
                } else if (ts.isJsxSpreadAttribute(property)) {
                    const spread = bare(property.expression);
                    if (ts.isCallExpression(spread) && ts.isIdentifier(spread.expression) && makers.has(spread.expression.text)) {
                        pendingElement = true;
                        spreadsPendingProps = true;
                        const handler = spread.arguments[1];
                        if (handler !== undefined) handlers.push({ expression: handler, attribute: 'pending props' });
                    }
                }
            }
            if (spreadsPendingProps && ownOnClick !== undefined) {
                fail(ownOnClick, `<${tag}> has its own onClick beside the pending props it spreads; one replaces the `
                    + 'other. Pass the handler to the pending props, which hold the button from its press.');
            }
            if (pendingElement) {
                for (const { expression, attribute } of handlers) presses.push({ handler: expression, on: `<${tag}> ${attribute}` });
            } else if (pressable) {
                for (const { expression, attribute } of handlers) unheldPresses.push({ handler: expression, on: `<${tag}> ${attribute}` });
            }
            if (ts.isJsxOpeningElement(node) && (BUTTON_TAGS.has(tag) || literalAttribute(node, 'role') === 'button')) {
                for (const child of node.parent.children) checkChild(child, tag);
            }
        }
        // An ActionFooter slot, or any object that says it is pending: its onClick is a press, its label a label.
        if (ts.isObjectLiteralExpression(node)) {
            const property = (key: string) => node.properties.find((p) => nameText(p.name) === key);
            const onClick = property('onClick');
            const handler = onClick === undefined ? undefined
                : ts.isPropertyAssignment(onClick) ? onClick.initializer
                    : ts.isShorthandPropertyAssignment(onClick) ? onClick.name : undefined;
            if (property('pending') !== undefined) {
                if (handler !== undefined) presses.push({ handler, on: '{ onClick }' });
            } else if (handler !== undefined && elementHolding(node)?.attribute !== undefined) {
                // A slot given to an element (an ActionFooter's), with no pending state.
                unheldPresses.push({ handler, on: '{ onClick }' });
            }
            // A button's slot, in the mechanism or not: its labels are a button's.
            if (property('pending') !== undefined || onClick !== undefined) {
                const slotLabels = [['label', 'a slot\'s label'], ['pendingLabel', 'a slot\'s pending label']] as const;
                for (const [key, what] of slotLabels) {
                    const label = property(key);
                    if (label !== undefined && ts.isPropertyAssignment(label) && labelReads(label.initializer)) {
                        labelReadsPending(label, what);
                    }
                }
            }
        }
        ts.forEachChild(node, visit);
    };
    visit(source);

    // What each press runs: its inline handler, a same-file function it names,
    // and a same-file function its inline handler calls. The press's way in is
    // a function it names, and one its inline handler calls that starts a
    // request itself; a helper that only edits what is on the page is not.
    const startsRequest = (fn: FunctionNode) =>
        ownNodes(fn).some((n) => ts.isCallExpression(n) && REQUEST_CALLS.has(calleeName(n) ?? ''));
    /**
     * The functions a press runs: its inline handler or the same-file function
     * it names, and each same-file function either of those calls. `wayIn`
     * hears the names that are a way into the press: the function it names,
     * and a callee that starts a request itself.
     */
    const runBy = (press: Press, wayIn: (name: string) => void): FunctionNode[] => {
        const handler = bare(press.handler);
        const first: FunctionNode[] = [];
        if (ts.isIdentifier(handler)) {
            const named = functions.get(handler.text);
            if (named !== undefined) {
                wayIn(handler.text);
                first.push(...named);
            }
        } else if (isFunctionNode(handler)) {
            first.push(handler);
        }
        const out = [...first];
        for (const fn of first) {
            for (const node of ownNodes(fn)) {
                if (!ts.isCallExpression(node) || !ts.isIdentifier(node.expression)) continue;
                const named = functions.get(node.expression.text);
                if (named === undefined) continue;
                if (named.some(startsRequest)) wayIn(node.expression.text);
                for (const callee of named) if (!out.includes(callee)) out.push(callee);
            }
        }
        return out;
    };
    const pressNames = new Set<string>();
    const inline = new Set<ts.Node>();
    const handlerNodes = new Set<ts.Node>(presses.map((p) => p.handler));
    const ran: Array<{ fn: FunctionNode; press: Press }> = [];
    for (const press of presses) {
        const handler = bare(press.handler);
        if (isFunctionNode(handler)) inline.add(handler);
        for (const fn of runBy(press, (n) => pressNames.add(n))) ran.push({ fn, press });
    }

    for (const { fn, press } of ran) {
        const own = ownNodes(fn);
        const firstAwait = own.find(ts.isAwaitExpression);
        const requests = own.filter((n): n is ts.CallExpression => ts.isCallExpression(n) && REQUEST_CALLS.has(calleeName(n) ?? ''));
        for (const node of own) {
            if (ts.isCallExpression(node) && DEFERRING_CALLS.has(calleeName(node) ?? '')) {
                fail(node, `${press.on} defers with ${calleeName(node)}. A press holds its button for the requests `
                    + 'that start while its handler runs; start the request in the press.');
            }
        }
        if (firstAwait !== undefined) {
            // A request the await waits on is the press's own; one after the await is not.
            for (const request of requests.filter((r) => r.getStart(source) >= firstAwait.getEnd())) {
                fail(request, `${press.on} starts a request after an await, past the press that would hold its button; `
                    + 'start it before the await, or await it so the promise the handler returns holds the button.');
            }
        }
    }
    // An error cleared anywhere a press runs, when the press starts a request
    // anywhere it runs: the request may start in a callee, the clear in the handler.
    for (const press of presses) {
        const fns = ran.filter((r) => r.press === press).map((r) => r.fn);
        if (!fns.some(startsRequest)) continue;
        for (const fn of fns) {
            for (const node of ownNodes(fn)) {
                if (!ts.isCallExpression(node)) continue;
                const callee = calleeName(node) ?? '';
                if ((ERROR_SETTER.test(callee) && clears(node.arguments[0])) || callee === 'reset') {
                    fail(node, `${press.on} clears an error as its request starts (${callee}). The error stays shown until `
                        + 'the new result replaces or clears it: a cleared alert moves the button under the pointer.');
                }
            }
        }
    }

    // A button that starts a request with no pending state at all.
    const unheld: string[] = [];
    for (const press of unheldPresses) {
        if (!runBy(press, () => {}).some(startsRequest)) continue;
        unheld.push(`${name}:${lineOf(press.handler)} — ${press.on} starts a request, and the button is not in the `
            + 'mechanism: nothing holds it or refuses a second press. Give it `pending` (`isConfirming` on a '
            + 'ConfirmDialog, a slot\'s `pending`, a raw button\'s pending props) from the request it starts.');
    }

    const pressBodies = new Set<ts.Node>([...inline, ...ran.map((r) => r.fn)]);
    const insidePress = (node: ts.Node) => {
        for (let up = node.parent; up !== undefined; up = up.parent) if (pressBodies.has(up)) return true;
        return false;
    };
    const findBypasses = (node: ts.Node) => {
        if (ts.isIdentifier(node) && pressNames.has(node.text) && !handlerNodes.has(node) && !insidePress(node)) {
            const up = node.parent;
            const called = ts.isCallExpression(up) && up.expression === node;
            const handedOn = (ts.isJsxExpression(up) && ts.isJsxAttribute(up.parent))
                || (ts.isPropertyAssignment(up) && up.initializer === node) || ts.isShorthandPropertyAssignment(up);
            if (called || handedOn) {
                fail(node, `${node.text}, a pending button's press, is ${called ? 'called' : 'handed on'} past the button. `
                    + 'Another way into the action clicks the button, so the press holds it as the button\'s own does.');
            }
        }
        ts.forEachChild(node, findBypasses);
    };
    findBypasses(source);
    // A name two components of the file each declare is followed into both, once.
    return { failures: [...new Set(failures)], labels, unheld: [...new Set(unheld)] };
}

/**
 * Buttons that start a request outside the mechanism, per file, with why. A
 * count that differs fails, so a new one is reviewed. Empty: every button that
 * starts a request is held.
 */
const UNHELD_REVIEWED: ReadonlyMap<string, Review> = new Map<string, Review>([]);

/**
 * Labels that read a name the detector takes for a pending state, per file,
 * with why none is one. A count that differs fails, so a new one is reviewed.
 */
const LABELS_REVIEWED: ReadonlyMap<string, Review> = new Map<string, Review>([
    ['routes/ledgers/register/shell/RegisterDeleteConfirm.tsx', {
        count: 1,
        reason: 'The confirm label names what is to be deleted, read from the delete awaiting confirmation (a prop named '
            + 'pending); the request in flight is isConfirming, whose label is Working….',
    }],
]);

/** What the press rules fail a file for, its reviewed labels and unheld buttons taken out. */
function pressFailuresIn(file: string, text: string): string[] {
    const { failures, labels, unheld } = pressFailuresOf(file, text);
    const listed = LABELS_REVIEWED.get(file)?.count ?? 0;
    const unheldListed = UNHELD_REVIEWED.get(file)?.count ?? 0;
    // A count that differs fails either way. One that rose names each new site; one that
    // fell names the stale review, which otherwise stays listed and would excuse the next
    // site added to the file without anyone reviewing it.
    const differs = (found: string[], reviewed: number, registry: string) =>
        found.length === reviewed ? []
            : found.length > reviewed ? found
                : [`${file} has ${found.length}, ${registry} says ${reviewed}. Lower the count, or drop the entry.`];
    return [
        ...failures,
        ...differs(labels, listed, 'LABELS_REVIEWED'),
        ...differs(unheld, unheldListed, 'UNHELD_REVIEWED'),
    ];
}

let everyPressFailure: string[] | undefined;

/** Every source file's press failures. */
function allPressFailures(): string[] {
    everyPressFailure ??= sourceFiles(SRC).flatMap((file) => pressFailuresIn(rel(file), readFileSync(file, 'utf8')));
    return everyPressFailure;
}

let everyFile: Map<string, Scan> | undefined;

/** Every source file's scan. Every file is parsed: a text filter in front could drift from the detector. */
function allScans(): Map<string, Scan> {
    everyFile ??= new Map(sourceFiles(SRC).map((file) => [rel(file), scanOf(rel(file), readFileSync(file, 'utf8'))]));
    return everyFile;
}

const scanIn = (snippet: string) => scanOf('snippet.tsx', snippet);
const pendingIn = (snippet: string) => scanIn(snippet).uses.filter((u) => u.pending);

describe('one rule for a busy button', () => {
    // Guard the guard: each of these disables a button from a pending request.
    it.each([
        ['a mutation\'s flag', `const f = () => <Button disabled={save.isPending}>Save</Button>;`],
        ['a flag beside a reason', `const f = () => <Button disabled={!valid || save.isPending}>Save</Button>;`],
        ['a query\'s loading flag', `const f = () => <button disabled={q.isLoading}>Load</button>;`],
        ['a query\'s fetching flag', `const f = () => <button disabled={q.isFetching}>Load</button>;`],
        ['the next page in flight', `const f = () => <Button disabled={q.isFetchingNextPage}>More</Button>;`],
        ['fetchStatus', `const f = () => <Button disabled={q.fetchStatus === 'fetching'}>Load</Button>;`],
        ['a status compared with pending', `const f = () => <Button disabled={m.status === 'pending'}>Save</Button>;`],
        ['a destructured flag', `function F() { const { isPending } = useMutation(o); return <Button disabled={isPending} />; }`],
        ['a local bound to a flag', `function F() { const busy2 = a.isPending || b.isPending; return <Button disabled={busy2} />; }`],
        ['a local of a local', `function F() { const p = a.isPending; const q = !ok || p; return <IconButton disabled={q} />; }`],
        ['a name that says so', `function F({ isSaving }) { return <Button disabled={isSaving} />; }`],
        ['another name that says so', `function F({ submitting }) { return <button disabled={submitting} />; }`],
        ['a counting hook', `function F() { const n = useIsMutating(); return <Button disabled={n > 0} />; }`],
        ['a component\'s disabled prop', `const f = () => <SnapshotRow disabled={m.isPending} />;`],
        ['a prop named for a button', `const f = () => <ConfirmDialog confirmDisabled={m.isPending} />;`],
        ['a footer slot', `const f = () => <ActionFooter confirm={{ label: 'Save', disabled: m.isPending }} />;`],
        ['a shorthand slot', `function F() { const disabled = m.isPending; return <ActionFooter confirm={{ disabled }} />; }`],
        ['a menu item built outside JSX', `const items = [{ id: 'merge', disabled: merge.isPending }];`],
        ['a flag read by element access', `const f = () => <Button disabled={m['isPending']} />;`],
        ['a renamed destructured flag', `function F() { const { isPending: locked } = useMutation(o); return <Button disabled={locked} />; }`],
        ['a same-file arrow helper', `function F() { const lock = () => m.isPending; return <Button disabled={lock()} />; }`],
        ['a same-file function', `function locked(m) { return m.isPending; } const f = () => <Button disabled={locked(m)} />;`],
        ['a value made by useMemo', `function F() { const locked = useMemo(() => a.isPending || b.isPending, [a, b]); return <Button disabled={locked} />; }`],
        ['a status read into a name', `function F() { const st = m.status; return <Button disabled={st === 'pending'} />; }`],
        ['a destructured status', `function F() { const { status: st } = m; return <Button disabled={st === 'pending'} />; }`],
        ['a status read by element access', `const f = () => <Button disabled={m['status'] === 'pending'} />;`],
        ['a disabled spread in on a pending condition', `const f = () => <Button {...(save.isPending ? { disabled: true } : {})} />;`],
        ['a disabled spread in after a pending &&', `const f = () => <Button {...(save.isPending && { disabled: true })} />;`],
        ['a submit input', `const f = () => <input type="submit" disabled={m.isPending} />;`],
        ['a button input', `const f = () => <input type="button" disabled={m.isPending} />;`],
        ['a fieldset around a button', `const f = () => <fieldset disabled={m.isPending}><Button type="submit">Save</Button></fieldset>;`],
    ])('fails %s', (_shape, snippet) => {
        expect(pendingIn(snippet)).toHaveLength(1);
        expect(failuresOf('snippet.tsx', scanIn(snippet), { count: 0, reason: 'none' }).length).toBeGreaterThan(0);
    });

    // Each disables a button for a reason other than a request in flight, or is not a button's.
    it.each([
        ['an empty field', `const f = () => <Button disabled={name.trim() === ''}>Save</Button>;`, 1],
        ['the pending state given as pending', `const f = () => <Button disabled={!valid} pending={save.isPending}>Save</Button>;`, 1],
        ['a field disabled while saving', `const f = () => <input disabled={save.isPending} />;`, 0],
        ['a field component disabled while saving', `const f = () => <Typeahead disabled={isSaving} />;`, 0],
        ['a fieldset', `const f = () => <fieldset disabled={m.isPending} />;`, 0],
        ['a fieldset that holds only a checkbox',
            `const f = () => <fieldset disabled={m.isPending}><label><input type="checkbox" /> Sample</label></fieldset>;`, 0],
        ['a checkbox input', `const f = () => <input type="checkbox" disabled={m.isPending} />;`, 0],
        ['a mutation\'s argument in a handler', `const f = () => <Button onClick={() => m.mutate({ disabled: !u.isDisabled })} />;`, 1],
        ['a comment', `// <Button disabled={save.isPending} />`, 0],
        ['a string', `const text = '<Button disabled={save.isPending} />';`, 0],
    ])('does not fail %s as pending', (_shape, snippet, counted) => {
        expect(pendingIn(snippet)).toEqual([]);
        expect(scanIn(snippet).uses).toHaveLength(counted);
    });

    it('fails a hand-written aria-disabled that reads a pending request', () => {
        const scan = scanIn(`const f = () => <button aria-disabled={m.isPending} onClick={go}>Go</button>;`);
        expect(scan.marked).toHaveLength(1);
        expect(failuresOf('a.tsx', scan, undefined).some((f) => f.includes('without refusing its click'))).toBe(true);
    });

    it('fails a hand-written aria-disabled read through a renamed destructure', () => {
        const scan = scanIn(`function F() { const { isPending: locked } = useMutation(o);
            return <button aria-disabled={locked} onClick={go}>Go</button>; }`);
        expect(scan.marked).toHaveLength(1);
        expect(failuresOf('a.tsx', scan, undefined).some((f) => f.includes('without refusing its click'))).toBe(true);
    });

    it('counts every other hand-written aria-disabled, and aria-busy on a button, for review', () => {
        // A state the detector cannot see as pending still needs a reason.
        const scan = scanIn(`function F({ locked }) { return <>
            <button aria-disabled={locked} onClick={go}>Go</button>
            <Button aria-busy={locked}>Save</Button>
            <div role="button" aria-busy={locked}>Pick</div>
        </>; }`);
        expect(scan.marked).toEqual([]);
        expect(scan.marks).toHaveLength(3);
        expect(failuresOf('a.tsx', scan, undefined).some((f) => f.includes('by hand 3 times'))).toBe(true);
        expect(failuresOf('a.tsx', scan, { count: 0, marks: 3, reason: 'Three marks reviewed, for the test of the count.' }))
            .toEqual([]);
    });

    it('counts no aria-busy on a region that is loading, nor any on a field', () => {
        const scan = scanIn(`const f = () => <>
            <div aria-busy="true" aria-label="Loading the list" />
            <input type="radio" disabled aria-disabled="true" />
        </>;`);
        expect(scan.marked).toEqual([]);
        expect(scan.marks).toEqual([]);
        expect(scan.uses).toEqual([]);
    });

    it('counts a reviewed file as it was reviewed, and fails one more', () => {
        const review: Review = { count: 1, reason: 'Unavailable until a name is typed, for the test.' };
        expect(failuresOf('a.tsx', scanIn(`const f = () => <Button disabled={!name}>Save</Button>;`), review)).toEqual([]);
        expect(failuresOf('a.tsx', scanIn(`const f = () => <><Button disabled={!name} /><Button disabled={!other} /></>;`), review))
            .not.toEqual([]);
    });

    it('keeps every disabled on a button in the SPA unavailable, never busy', () => {
        const failures: string[] = [];
        for (const [file, scan] of allScans()) failures.push(...failuresOf(file, scan, REVIEWED.get(file)));
        for (const file of REVIEWED.keys()) {
            if (!allScans().has(file)) failures.push(`${file} is in REVIEWED and is not in the source.`);
        }
        expect(failures).toEqual([]);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    it('keeps every entry honest', () => {
        // A field component no file renders any more covers nothing; and the
        // catalogue going stale all at once is how a blind detector shows.
        const tags = new Set<string>();
        for (const file of sourceFiles(SRC)) {
            for (const match of readFileSync(file, 'utf8').matchAll(/<([A-Z][A-Za-z]*)\b/g)) tags.add(match[1]!);
        }
        for (const [component, what] of FIELDS) {
            expect(tags.has(component), component).toBe(true);
            expect(what.length, component).toBeGreaterThan(10);
        }
        for (const [file, { count, marks, reason }] of REVIEWED) {
            expect(count + (marks ?? 0), file).toBeGreaterThan(0);
            expect(reason.length, file).toBeGreaterThan(40);
        }
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);
});

/**
 * The press (ADR-0023 §L, pending.ts). A pending button's press holds the
 * button until the requests it started have settled, so it is refused before
 * the pending state has rendered; a label that changes while pending never
 * changes the button's size; and an error stays shown until the new result
 * replaces it.
 * Each is held by the mechanism, so each fails here where a site steps past
 * it: pressFailuresOf says how.
 *
 * Not caught: a press handed in from another file under a prop name (an
 * `onSave` a row calls), and another way into it there; a press given to a
 * pending element under a prop other than onClick, onConfirm and the props in
 * COMPONENT_PRESS_ATTRIBUTES; a request started by a function the press calls
 * that is not named in the same file, or two calls deep in it; an alias of a
 * press (`const go = save`); an error cleared by a press that starts its
 * request through a prop, or held under a setter not named for an error
 * (setMessage, setNotice); a button whose press starts its request through a
 * prop, or a menu item's; a label held in a name assigned inside a branch on
 * the pending state (`let label = 'Save'; if (m.isPending) label = …`), or
 * drawn by a component that reads the pending state itself; a pending label
 * that changes on a flag not named for a request in flight (a passkey
 * ceremony's stage, which nests a PendingLabel instead); a button drawn in
 * place of another while pending; and an alert that
 * reads a mutation's own `error`, which TanStack Query clears when the next
 * mutate starts.
 */
describe('the press of a pending button', () => {
    const pressIn = (snippet: string) => pressFailuresIn('snippet.tsx', snippet);

    // Guard the guard: each of these steps past the mechanism.
    it.each([
        ['a field\'s Enter calling the press', `function F() {
            function save() { m.mutate(); }
            return <><input onKeyDown={(e) => { if (e.key === 'Enter') save(); }} />
                <Button pending={m.isPending} onClick={save}>Save</Button></>; }`],
        ['the press handed to a field', `function F() {
            const save = () => m.mutate();
            return <><input onBlur={save} /><Button pending={m.isPending} onClick={save}>Save</Button></>; }`],
        ['a slot\'s press called from a field', `function F() {
            function save() { m.mutate(); }
            return <><input onKeyDown={() => save()} />
                <ActionFooter confirm={{ label: 'Save', onClick: save, pending: m.isPending }} /></>; }`],
        ['a press called from an inline press\'s helper outside', `function F() {
            const go = () => m.mutate();
            useEffect(() => { go(); }, []);
            return <Button pending={m.isPending} onClick={() => go()}>Go</Button>; }`],
        ['a timer in the press', `const f = () => <Button pending={m.isPending} onClick={() => setTimeout(() => m.mutate(), 0)}>Go</Button>;`],
        ['a request after an await', `const f = () => <Button pending={m.isPending}
            onClick={async () => { await check(); m.mutate(); }}>Go</Button>;`],
        ['a raw button\'s own onClick beside its pending props', `function F() {
            const p = usePendingButtonProps();
            return <button {...p(m.isPending, go)} onClick={go}>Go</button>; }`],
        ['children that read a pending state', `const f = () => <Button pending={m.isPending}>{m.isPending ? 'Saving…' : 'Save'}</Button>;`],
        ['nested children that read a pending state', `const f = () => <button><span>{busy ? 'Saving…' : 'Save'}</span></button>;`],
        ['a label made in place by a function that reads one', `const f = () => <Button pending={m.isPending}>
            {(() => { if (m.isPending) return 'Saving…'; return 'Save'; })()}</Button>;`],
        ['a label made by a helper that reads one', `function F() {
            const label = () => (m.isPending ? 'Saving…' : 'Save');
            return <Button pending={m.isPending}>{label()}</Button>; }`],
        ['a slot\'s label', `const f = () => <ActionFooter confirm={{ label: m.isPending ? 'Saving…' : 'Save', pending: m.isPending }} />;`],
        ['a confirm label', `const f = () => <ConfirmDialog confirmLabel={m.isPending ? 'Working…' : 'Delete'} />;`],
        ['an error cleared at the press', `function F() {
            function save() { setError(null); if (!ok) { setError('Required.'); return; } m.mutate(); }
            return <Button pending={m.isPending} onClick={save}>Save</Button>; }`],
        ['an error cleared in an inline press', `const f = () => <Button pending={m.isPending}
            onClick={() => { setSaveError(null); m.mutate(); }}>Save</Button>;`],
        ['a mutation reset at the press', `const f = () => <Button pending={m.isPending} onClick={() => { m.reset(); m.mutate(); }}>Go</Button>;`],
        ['an error cleared in a form\'s submit', `const f = () => <form onSubmit={(e) => { e.preventDefault(); setError(null); m.mutate(); }} />;`],
        ['a refusal cleared at the press', `function F() {
            function save() { setRefusal(null); m.mutate(); }
            return <Button pending={m.isPending} onClick={save}>Save</Button>; }`],
        ['a collision cleared at the press', `function F() {
            const save = () => { setCollision(null); m.mutate(); };
            return <Button pending={m.isPending} onClick={save}>Save</Button>; }`],
        ['a named press\'s same-file callee called from a field', `function F() {
            function doSave() { m.mutate(); }
            function save() { doSave(); }
            return <><input onKeyDown={() => doSave()} /><Button pending={m.isPending} onClick={save}>Save</Button></>; }`],
        ['a named press\'s callee that defers', `function F() {
            function later() { setTimeout(() => m.mutate(), 0); }
            function save() { later(); }
            return <Button pending={m.isPending} onClick={save}>Save</Button>; }`],
        ['an error cleared by a press whose request starts in a callee', `function F() {
            function doSave() { m.mutate(); }
            return <Button pending={m.isPending} onClick={() => { setError(null); doSave(); }}>Save</Button>; }`],
        ['errors by field emptied at the press', `const f = () => <Button pending={m.isPending}
            onClick={() => { setErrors({}); m.mutate(); }}>Save</Button>;`],
        ['an error cleared by an updater at the press', `const f = () => <Button pending={m.isPending}
            onClick={() => { setError(() => null); m.mutate(); }}>Save</Button>;`],
        ['a component\'s own press prop clearing an error', `const f = () => <RegisterDeleteConfirm isConfirming={m.isPending}
            onConfirmSingle={(t) => { setDeleteError(null); m.mutate(t); }} />;`],
        ['a slot\'s label with no pending state', `const f = () => <ActionFooter confirm={{ label: m.isPending ? 'Saving…' : 'Save', onClick: save }} />;`],
        ['a button\'s children prop', `const f = () => <Button pending children={m.isPending ? 'Saving…' : 'Save'} />;`],
        ['a component drawn as the label, given the pending state', `const f = () => <Button pending={m.isPending}>
            <SaveLabel busy={m.isPending} /></Button>;`],
        ['a pending label that reads a pending state', `const f = () => <Button pending={busy}
            pendingLabel={m.isPending ? 'Saving…' : 'Save'}>Save</Button>;`],
        ['a pending label held in a name that reads one', `function F() {
            const savingLabel = m.isPending ? 'Saving…' : 'Save';
            return <Button pending={busy} pendingLabel={savingLabel}>Save</Button>; }`],
        ['a slot\'s pending label that reads one', `const f = () => <ActionFooter
            confirm={{ label: 'Save', pendingLabel: m.isPending ? 'Saving…' : 'Save', pending: busy }} />;`],
        ['a PendingLabel\'s label that reads one', `const f = () => <button>
            <PendingLabel pending={busy} label={m.isPending ? 'Saving…' : 'Save'} pendingLabel="Saving…" /></button>;`],
        ['a PendingLabel\'s pending label that reads one', `const f = () => <button>
            <PendingLabel pending={busy} label="Save" pendingLabel={m.isPending ? 'Saving…' : 'Save'} /></button>;`],
        ['an editor\'s submitting label that reads one', `const f = () => <TxnRowEdit submittingLabel={m.isPending ? 'Posting…' : 'Post'} />;`],
    ])('fails %s', (_shape, snippet) => {
        expect(pressIn(snippet)).not.toEqual([]);
    });

    // A button that starts a request outside the mechanism is neither held nor
    // refused: a second press sends a second request.
    it.each([
        ['an inline mutate', `const f = () => <Button onClick={() => m.mutate()}>Go</Button>;`],
        ['a same-file function that refetches', `function F() {
            function retry() { void q.refetch(); }
            return <Button variant="link" onClick={retry}>Retry</Button>; }`],
        ['a same-file function an inline handler calls', `function F() {
            function go() { m.mutateAsync(); }
            return <IconButton aria-label="Go" onClick={() => go()} />; }`],
        ['a raw button', `const f = () => <button type="button" onClick={() => m.mutate()}>Go</button>;`],
        ['a confirm with no isConfirming', `const f = () => <ConfirmDialog open onConfirm={() => m.mutate()} />;`],
        ['a slot with no pending', `const f = () => <ActionFooter confirm={{ label: 'Save', onClick: () => m.mutate() }} />;`],
    ])('fails a button outside the mechanism: %s', (_shape, snippet) => {
        expect(pressFailuresOf('snippet.tsx', snippet).unheld).toHaveLength(1);
        expect(pressIn(snippet)).not.toEqual([]);
    });

    it.each([
        ['a button in the mechanism', `const f = () => <Button pending={m.isPending} onClick={() => m.mutate()}>Go</Button>;`],
        ['a button that starts nothing', `const f = () => <Button onClick={() => setOpen(true)}>Open</Button>;`],
        ['a slot in the mechanism', `const f = () => <ActionFooter confirm={{ label: 'Save', onClick: () => m.mutate(), pending: m.isPending }} />;`],
        ['a press handed in as a prop', `function F({ onRetry }) { return <Button onClick={onRetry}>Retry</Button>; }`],
    ])('does not fail %s as outside the mechanism', (_shape, snippet) => {
        expect(pressFailuresOf('snippet.tsx', snippet).unheld).toEqual([]);
    });

    it('counts a reviewed button outside the mechanism as it was reviewed, and fails one more', () => {
        const one = `const f = () => <Button onClick={() => m.mutate()}>Go</Button>;`;
        expect(pressFailuresOf('a.tsx', one).unheld).toHaveLength(1);
        expect(pressFailuresIn('a.tsx', one)).not.toEqual([]);
        for (const [file, { count, reason }] of UNHELD_REVIEWED) {
            expect(count, file).toBeGreaterThan(0);
            expect(reason.length, file).toBeGreaterThan(40);
            expect(allScans().has(file), file).toBe(true);
        }
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    // Each goes through the mechanism.
    it.each([
        ['a field\'s Enter clicking the button', `function F() {
            const saveRef = useRef(null);
            function save() { m.mutate(); }
            return <><input onKeyDown={(e) => { if (e.key === 'Enter') saveRef.current?.click(); }} />
                <Button ref={saveRef} pending={m.isPending} onClick={save}>Save</Button></>; }`],
        ['the pending label', `const f = () => <Button pending={m.isPending} pendingLabel="Saving…">Save</Button>;`],
        ['a raw button\'s PendingLabel', `const f = () => <button><PendingLabel pending={busy} label="Save" pendingLabel="Saving…" /></button>;`],
        ['a slot\'s pending label', `const f = () => <ActionFooter confirm={{ label: 'Save', pendingLabel: 'Saving…', pending: m.isPending }} />;`],
        ['a label that swaps for the button\'s own request', `const f = () => <Button pending={busy}>
            <PendingLabel pending={m.isPending} label="Save" pendingLabel="Saving…" /></Button>;`],
        ['a pending label held in a name for a label', `function F({ submittingLabel }) {
            const savingLabel = merging ? 'Folding…' : (submittingLabel ?? 'Saving…');
            return <Button pending={m.isPending} pendingLabel={savingLabel}>Save</Button>; }`],
        ['a pending label in stages', `const f = () => <Button pending={m.isPending}
            pendingLabel={<PendingLabel pending={started} label="Starting…" pendingLabel="Tap your authenticator…" />}>Add</Button>;`],
        ['a pending label in stages held in a name', `function F() {
            const submittingLabel = <PendingLabel pending={started} label="Creating account…" pendingLabel="Tap…" />;
            return <Button pending={m.isPending} pendingLabel={submittingLabel}>Create account</Button>; }`],
        ['a refusal that replaces the error', `function F() {
            function save() { if (!ok) { setError('Required.'); return; } m.mutate(); }
            return <Button pending={m.isPending} onClick={save}>Save</Button>; }`],
        ['an error cleared by a press that starts nothing', `const f = () => <Button pending={m.isPending}
            onClick={() => { preview.reset(); setStep('pick'); }}>Back</Button>;`],
        ['an awaited request', `const f = () => <Button pending={m.isPending}
            onClick={async () => { await m.mutateAsync(); close(); }}>Go</Button>;`],
        ['a timer in a callback that runs after the request', `const f = () => <Button pending={m.isPending}
            onClick={() => m.mutate(undefined, { onSuccess: () => setTimeout(close, 10) })}>Go</Button>;`],
        ['a press shared by two pending buttons', `function F() {
            const run = (k) => m.mutate(k);
            return <><Button pending={m.isPending} onClick={() => run('a')}>A</Button>
                <ConfirmDialog isConfirming={m.isPending} onConfirm={() => run('b')} /></>; }`],
        ['a raw button\'s pending props', `function F() {
            const p = usePendingButtonProps();
            return <button {...p(m.isPending, go)}>Go</button>; }`],
    ])('does not fail %s', (_shape, snippet) => {
        expect(pressIn(snippet)).toEqual([]);
    });

    it('keeps every press in the SPA on the mechanism', () => {
        expect(allPressFailures()).toEqual([]);
        for (const file of LABELS_REVIEWED.keys()) expect(allScans().has(file), file).toBe(true);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    it('does not take a busy word inside another word for a pending state, in a label', () => {
        expect(pressIn(`const f = () => <button>{monthLabel(data.latestMonthWithSpending)}</button>;`)).toEqual([]);
        expect(pressIn(`const f = () => <button>{inFlight ? 'Mapping…' : 'Map'}</button>;`)).not.toEqual([]);
    });

    it('counts a reviewed label as it was reviewed, and fails one more or one fewer', () => {
        const reviewed = 'routes/ledgers/register/shell/RegisterDeleteConfirm.tsx';
        const one = `function F({ run }) { return <button>{run.txnsStillPending} new</button>; }`;
        expect(pressFailuresOf('a.tsx', one).labels).toHaveLength(1);
        expect(pressFailuresIn(reviewed, one)).toEqual([]);
        expect(pressFailuresIn(reviewed, `${one}\n${one.replace('F(', 'G(')}`)).not.toEqual([]);
        // The stale review: the reviewed label went, the entry stayed, and would excuse
        // the next one added without anyone reviewing it.
        expect(pressFailuresIn(reviewed, `function F() { return <button>Delete</button>; }`))
            .toEqual([`${reviewed} has 0, LABELS_REVIEWED says 1. Lower the count, or drop the entry.`]);
    });

    // A mutation's error stays shown while a retry is in flight: the SPA's
    // useMutation (lib/mutation.ts) keeps it, where TanStack's own clears it as
    // the next mutate starts, and the alert would go at the press.
    it('takes every mutation from the useMutation that keeps its error through a retry', () => {
        // Guard the guard: every way to take TanStack's hook is seen, and nothing else is.
        for (const [shape, text] of [
            ['a named import', `import { useMutation, useQuery } from '@tanstack/react-query';`],
            ['an alias', `import { useMutation as useM } from '@tanstack/react-query';`],
            ['a re-export', `export { useMutation } from '@tanstack/react-query';`],
            ['a re-export under another name', `export { useMutation as useSave } from '@tanstack/react-query';`],
            ['a re-export of everything', `export * from '@tanstack/react-query';`],
            ['a namespace re-export', `export * as rq from '@tanstack/react-query';`],
            ['a namespace import', `import * as rq from '@tanstack/react-query';\nconst m = rq.useMutation({});`],
            ['a subpath', `import { useMutation } from '@tanstack/react-query/build/modern/useMutation';`],
            ['an import-equals', `import rq = require('@tanstack/react-query');`],
            ['a dynamic import', `const load = () => import('@tanstack/react-query');`],
        ] as const) {
            expect(tanstackMutationTakes('a.tsx', text), shape).toHaveLength(1);
        }
        for (const [shape, text] of [
            ['the wrapper', `import { useQuery } from '@tanstack/react-query';\nimport { useMutation } from '@/lib/mutation';`],
            ['another hook of the same name start', `import { useMutationState } from '@tanstack/react-query';`],
            ['a re-export of another name', `export { useQuery } from '@tanstack/react-query';`],
            ['a type', `import type { UseMutationResult } from '@tanstack/react-query';`],
            ['another module\'s namespace', `import * as api from '@/lib/api';\nexport * from './mutation';`],
            ['a comment naming the module', `// useMutation from '@tanstack/react-query' is not taken here\nconst x = 1;`],
        ] as const) {
            expect(tanstackMutationTakes('a.tsx', text), shape).toEqual([]);
        }

        // The wrapper is the one file that takes TanStack's hook, and it does, so the
        // exclusion below names a file that exists and is the wrapper.
        const files = sourceFiles(SRC);
        const wrapper = files.find((file) => rel(file) === 'lib/mutation.ts');
        expect(wrapper, 'lib/mutation.ts').toBeDefined();
        expect(tanstackMutationTakes(wrapper!, readFileSync(wrapper!, 'utf8'))).toHaveLength(1);

        const stale = files
            .filter((file) => file !== wrapper)
            .flatMap((file) => tanstackMutationTakes(file, readFileSync(file, 'utf8')).map((line) => `${rel(file)}:${line}`));
        expect(stale).toEqual([]);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    it('leaves no raw button on a helper that cannot hold it', () => {
        // The plain helper had no QueryClient to watch a press's requests in.
        const stale = sourceFiles(SRC).filter((file) => /\bpendingButtonProps\(/.test(readFileSync(file, 'utf8')));
        expect(stale.map(rel)).toEqual([]);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);
});

// ---------------------------------------------------------------------------
// The change: a choice that writes holds from the change, as a press does.
// ---------------------------------------------------------------------------

/**
 * What the change rule fails one source text for (pending.ts, ADR-0023 §L): a
 * choice whose change starts a request (its onChange, or a same-file function
 * it names or calls, calls a REQUEST_CALLS method) that is not held from the
 * change:
 *  - a raw choice spreading the plain pendingChoiceProps, which refuses only
 *    once the pending state has rendered: the file takes its maker from
 *    usePendingChoiceProps (`const pendingChoiceProps = usePendingChoiceProps()`);
 *  - a Checkbox with no `pending`, which leaves it outside the mechanism;
 *  - a raw checkbox, radio or select with its own onChange and no pending props.
 */
function changeFailuresOf(name: string, text: string): string[] {
    const source = ts.createSourceFile(
        name, text, ts.ScriptTarget.Latest, true,
        name.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
    );
    const functions = namedFunctions(source);
    const lineOf = (node: ts.Node) => source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1;
    const startsRequest = (fn: FunctionNode) =>
        ownNodes(fn).some((n) => ts.isCallExpression(n) && REQUEST_CALLS.has(calleeName(n) ?? ''));
    // A change's handler starts a request: itself, a same-file function it names, or one it calls.
    const writes = (expression: ts.Expression): boolean => {
        const handler = bare(expression);
        const first = ts.isIdentifier(handler) ? functions.get(handler.text) ?? []
            : isFunctionNode(handler) ? [handler] : [];
        return first.some((fn) => startsRequest(fn) || ownNodes(fn).some((n) => ts.isCallExpression(n)
            && ts.isIdentifier(n.expression) && (functions.get(n.expression.text) ?? []).some(startsRequest)));
    };
    let hookBound = false;
    const findHook = (node: ts.Node) => {
        if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.name.text === 'pendingChoiceProps'
            && node.initializer !== undefined) {
            const init = bare(node.initializer);
            if (ts.isCallExpression(init) && calleeName(init) === 'usePendingChoiceProps') hookBound = true;
        }
        ts.forEachChild(node, findHook);
    };
    findHook(source);

    const failures: string[] = [];
    const visit = (node: ts.Node) => {
        if (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) {
            const tag = node.tagName.getText(source);
            let onChange: ts.Expression | undefined;
            let pending = false;
            let spreadsChoice = false;
            for (const property of node.attributes.properties) {
                if (ts.isJsxAttribute(property)) {
                    const attribute = nameText(property.name);
                    const value = property.initializer !== undefined && ts.isJsxExpression(property.initializer)
                        ? property.initializer.expression : undefined;
                    if (attribute === 'pending') pending = true;
                    if (attribute === 'onChange' && value !== undefined) onChange = value;
                } else if (ts.isJsxSpreadAttribute(property)) {
                    const spread = bare(property.expression);
                    if (!ts.isCallExpression(spread) || calleeName(spread) !== 'pendingChoiceProps') continue;
                    spreadsChoice = true;
                    const handlers = spread.arguments[1] === undefined ? undefined : bare(spread.arguments[1]);
                    const given = handlers !== undefined && ts.isObjectLiteralExpression(handlers)
                        ? handlers.properties.find((p) => nameText(p.name) === 'onChange') : undefined;
                    const handler = given === undefined ? undefined
                        : ts.isPropertyAssignment(given) ? given.initializer
                            : ts.isShorthandPropertyAssignment(given) ? given.name : undefined;
                    if (handler !== undefined && !hookBound && writes(handler)) {
                        failures.push(`${name}:${lineOf(property)} — <${tag}> writes on change through the plain `
                            + 'pendingChoiceProps, which refuses only once the pending state has rendered. Take the maker '
                            + 'from usePendingChoiceProps, which holds the choice and its group from the change.');
                    }
                }
            }
            const rawChoice = tag === 'select' || (tag === 'input'
                && ['checkbox', 'radio'].includes(literalAttribute(node, 'type') ?? ''));
            if (onChange !== undefined && !spreadsChoice && writes(onChange)
                && ((tag === 'Checkbox' && !pending) || rawChoice)) {
                failures.push(`${name}:${lineOf(node)} — <${tag}> writes on change outside the mechanism: nothing holds it `
                    + 'or refuses a second change. Give it `pending` (Checkbox), or the pending props of '
                    + 'usePendingChoiceProps (a raw choice).');
            }
        }
        ts.forEachChild(node, visit);
    };
    visit(source);
    return failures;
}

describe('the change of a choice that writes', () => {
    const changeIn = (snippet: string) => changeFailuresOf('snippet.tsx', snippet);

    // Guard the guard: each of these writes on change without a hold from the change.
    it.each([
        ['a raw switch on the plain helper', `const f = () => <input type="checkbox"
            {...pendingChoiceProps(m.isPending, { onChange: (e) => m.mutate(e.target.checked) })} />;`],
        ['a raw select on the plain helper, through a named function', `function F() {
            function pick(e) { setRole.mutate(e.target.value); }
            return <select {...pendingChoiceProps(busy, { onChange: pick })} />; }`],
        ['a Checkbox with no pending', `const f = () => <Checkbox label="Sync" onChange={(e) => m.mutate(e.target.checked)} />;`],
        ['a raw checkbox with its own onChange', `function F() {
            const toggle = (on) => save.mutate({ on });
            return <input type="checkbox" onChange={(e) => toggle(e.target.checked)} />; }`],
    ])('fails %s', (_shape, snippet) => {
        expect(changeIn(snippet)).toHaveLength(1);
    });

    // Each holds from the change, or writes nothing.
    it.each([
        ['the hook\'s maker', `function F() {
            const pendingChoiceProps = usePendingChoiceProps();
            return <input type="checkbox" {...pendingChoiceProps(m.isPending, { onChange: () => m.mutate() })} />; }`],
        ['a Checkbox given pending', `const f = () => <Checkbox pending={m.isPending} onChange={() => m.mutate()} />;`],
        ['a choice that writes nothing', `const f = () => <input type="checkbox"
            {...pendingChoiceProps(busy, { onChange: (e) => setOn(e.target.checked) })} />;`],
        ['a Checkbox that writes nothing', `const f = () => <Checkbox onChange={(e) => setOn(e.target.checked)} />;`],
    ])('does not fail %s', (_shape, snippet) => {
        expect(changeIn(snippet)).toEqual([]);
    });

    it('keeps every choice that writes in the SPA held from the change', () => {
        const failures = sourceFiles(SRC).flatMap((file) => changeFailuresOf(rel(file), readFileSync(file, 'utf8')));
        expect(failures).toEqual([]);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);
});

// ---------------------------------------------------------------------------
// The field: the button rule applies to fields.
// ---------------------------------------------------------------------------

/**
 * Every file with a field disabled for a reason other than a request in flight,
 * how many, and why. A field component that hands its own `disabled` prop on is
 * listed with who sets that prop.
 */
const FIELD_REVIEWED: ReadonlyMap<string, Review> = new Map<string, Review>([
    // The field components: each hands on a disabled its caller sets, and this rule checks the caller.
    ['components/register/AccountCategoryPicker.tsx', {
        count: 1,
        reason: 'The caller\'s disabled, handed to the input; a picker whose request is in flight is given pending.',
    }],
    ['components/tags/TagCombobox.tsx', {
        count: 1,
        reason: 'The caller\'s disabled, handed to the input; no caller passes one.',
    }],
    ['components/ui/Typeahead.tsx', {
        count: 1,
        reason: 'The caller\'s disabled, handed to the input; a field whose request is in flight is given pending.',
    }],
    ['routes/ledgers/components/DateField.tsx', {
        count: 1,
        reason: 'The caller\'s disabled, handed to the input; a field whose request is in flight is given pending.',
    }],
    ['routes/ledgers/register/shell/RegisterRowLead.tsx', {
        count: 1,
        reason: 'The select-all checkbox, which the register disables while it shows no rows to select.',
    }],
    // The sites: a choice that does not apply, no permission, or a sample of the look.
    ['components/ScheduleControl.tsx', {
        count: 2,
        reason: 'The switch cannot turn on while its precondition is missing (a backup passphrase), and the time is '
            + 'unavailable while the schedule is off; while either is saved it is pending.',
    }],
    ['components/tags/TagSelect.tsx', {
        count: 1,
        reason: 'A tag that is not on the transaction, once the transaction carries the most tags it may.',
    }],
    ['routes/__styleguide/StyleGuidePage.tsx', {
        count: 1,
        reason: 'The style guide\'s sample of a disabled input.',
    }],
    ['routes/ledgers/accounts/AccountEditorDialog.tsx', {
        count: 2,
        reason: 'Two computed values shown in field form, which are never typed in.',
    }],
    ['routes/ledgers/components/SecurityForm.tsx', {
        count: 2,
        reason: 'A quote option that needs a quote symbol typed first, and one that needs the security to have a symbol.',
    }],
    ['routes/ledgers/home/CategoriesSetting.tsx', {
        count: 1,
        reason: 'The category picker before the categories have loaded or failed to, and once the most it may hold are chosen.',
    }],
    ['routes/ledgers/home/WidgetPanel.tsx', {
        count: 1,
        reason: 'The last option of a set still chosen: a set showing nothing has no visible effect.',
    }],
    ['routes/ledgers/register/bank/BankRegisterPage.tsx', {
        count: 1,
        reason: 'The select-all checkbox, while the register shows no row to select.',
    }],
    ['routes/ledgers/register/investment/InvestmentRegisterPage.tsx', {
        count: 1,
        reason: 'The select-all checkbox, while the register shows no row to select.',
    }],
    ['routes/ledgers/register/shell/ImportFileDialog.tsx', {
        count: 1,
        reason: 'The marker beside a statement account the importer does not support, which can never be picked.',
    }],
    ['routes/ledgers/reminders/RecurrenceBuilder.tsx', {
        count: 1,
        reason: 'The day of the month while Last day is chosen instead.',
    }],
    ['routes/ledgers/settings/BenchmarksSection.tsx', {
        count: 1,
        reason: 'The symbol field is off with three benchmarks, and with the reader\'s Yahoo opt-in off or not yet read.',
    }],
    ['routes/ledgers/settings/CategoryDialogs.tsx', {
        count: 3,
        reason: 'The parent is fixed when the dialog was opened for one, and the parent picker is off while Top level is chosen.',
    }],
    ['routes/ledgers/settings/MembersPanel.tsx', {
        count: 1,
        reason: 'The role of the sole owner, which cannot change.',
    }],
    ['routes/ledgers/structure/StructureToolbar.tsx', {
        count: 2,
        reason: 'Search and Show inactive after the list failed to load; while it loads they are pending.',
    }],
]);

/** What the field rule fails a file for, given its review, or none when it is not listed. */
function fieldFailuresOf(file: string, scan: Scan, review: Review | undefined): string[] {
    const out: string[] = [];
    for (const use of scan.fieldUses.filter((u) => u.pending)) {
        out.push(`${file}:${use.line} — ${use.on} reads a pending request. A busy field keeps focus: pass the pending `
            + 'state as `pending` (Input, Checkbox, a field component), or spread pendingTextProps or pendingChoiceProps '
            + '(a raw field), and keep disabled for a field that is unavailable.');
    }
    for (const use of scan.fieldMarked) {
        out.push(`${file}:${use.line} — ${use.on} is written by hand from a pending request. Use \`pending\`, `
            + 'pendingTextProps or pendingChoiceProps, which say it and refuse the change.');
    }
    for (const use of scan.choiceClashes) {
        out.push(`${file}:${use.line} — ${use.on} sits beside the pendingChoiceProps it spreads; one replaces the `
            + 'other. Pass the handler to pendingChoiceProps, which refuses it while pending.');
    }
    const counted = scan.fieldUses.filter((u) => !u.pending);
    const listed = review?.count ?? 0;
    if (counted.length !== listed) {
        out.push(`${file} disables a field ${counted.length} times (lines ${counted.map((u) => u.line).join(', ') || 'none'}), `
            + `FIELD_REVIEWED says ${listed}. A field is disabled only when it is unavailable, not while its request `
            + 'is in flight; list each one with why it is unavailable.');
    }
    return out;
}

/**
 * One rule for a busy field (ADR-0023 §L): the button rule applies to fields.
 *
 * THE RULE. A field whose request is pending keeps focus and refuses input. It
 * says so through the shared mechanism in pending.ts: `pending` on Input,
 * Checkbox and the field components, pendingTextProps on a raw text-like field
 * (readOnly with aria-busy and aria-disabled), and pendingChoiceProps on a raw
 * choice (aria-busy and aria-disabled, its change, click, press and keys
 * refused). It never says so with the native disabled attribute: a focused
 * field that becomes disabled drops focus to the page.
 *
 * WHAT IT CHECKS. The disabled uses the button rule finds that sit on a field
 * (an input other than a button-typed one, a select, textarea, option or
 * optgroup, a fieldset that holds no button, a component in FIELDS, or an
 * object given to one), with the same reading of a pending state:
 *  - one that reads a pending state fails, by file and line;
 *  - a hand-written readOnly, aria-disabled or aria-busy on a field that reads
 *    a pending state fails: it marks the field without refusing a choice's
 *    change, so the mechanism says it instead;
 *  - a field that spreads pendingChoiceProps and has its own onChange,
 *    onClick, onKeyDown or onMouseDown beside it fails: whichever comes later
 *    replaces the other, so the refusal or the handler is lost;
 *  - every other is counted per file in FIELD_REVIEWED, with why the field is
 *    unavailable. A count that differs fails, and so does a file that is not
 *    listed, so a new one fails until it is reviewed.
 *
 * Not caught, beyond what the button rule states: a field's handler beside a
 * spread of a local wrapper of pendingChoiceProps rather than of the helper
 * itself; a field component's own way of changing its value (a list, a
 * shortcut) left working while it is pending, which each component's tests
 * hold; and a field that feeds a request and carries no pending marker at all
 * (no `pending`, no pendingTextProps or pendingChoiceProps, no disabled), which
 * keeps taking input while the request is in flight. Nothing in the source says
 * which request a field feeds, so each surface's tests hold that its fields
 * refuse input while its request is in flight.
 */
describe('one rule for a busy field', () => {
    const fieldPendingIn = (snippet: string) => scanIn(snippet).fieldUses.filter((u) => u.pending);
    const fieldFailuresIn = (snippet: string) => fieldFailuresOf('snippet.tsx', scanIn(snippet), undefined);

    // Guard the guard: each of these disables a field from a pending request.
    it.each([
        ['an input', `const f = () => <input disabled={save.isPending} />;`],
        ['a checkbox input', `const f = () => <input type="checkbox" disabled={m.isPending} />;`],
        ['a select', `const f = () => <select disabled={m.isPending}><option>a</option></select>;`],
        ['a textarea', `const f = () => <textarea disabled={busy} />;`],
        ['a fieldset of fields', `const f = () => <fieldset disabled={m.isPending}><input type="checkbox" /></fieldset>;`],
        ['the Input primitive', `const f = () => <Input disabled={login.isPending} />;`],
        ['the Checkbox primitive', `const f = () => <Checkbox disabled={create.isPending} />;`],
        ['a field component', `function F({ isSaving }) { return <Typeahead disabled={isSaving} />; }`],
        ['a flag beside a reason', `const f = () => <Input disabled={!isOwner || rename.isPending} />;`],
        ['a local bound to a flag', `function F() { const busy2 = a.isPending; return <select disabled={busy2} />; }`],
        ['a disabled spread into a field', `const f = () => <input {...(save.isPending ? { disabled: true } : {})} />;`],
    ])('fails %s', (_shape, snippet) => {
        expect(fieldPendingIn(snippet)).toHaveLength(1);
        expect(fieldFailuresIn(snippet).length).toBeGreaterThan(0);
    });

    it.each([
        ['a hand-written readOnly', `const f = () => <input readOnly={save.isPending} />;`],
        ['a hand-written aria-disabled', `const f = () => <select aria-disabled={m.isPending} />;`],
        ['a hand-written aria-busy', `const f = () => <Input aria-busy={busy} />;`],
    ])('fails %s that reads a pending request', (_shape, snippet) => {
        expect(scanIn(snippet).fieldMarked).toHaveLength(1);
        expect(fieldFailuresIn(snippet).some((f) => f.includes('written by hand'))).toBe(true);
    });

    it.each([
        ['its own onChange', `const f = () => <select onChange={go} {...pendingChoiceProps(busy, {})} />;`],
        ['its own onClick', `const f = () => <input type="checkbox" {...pendingChoiceProps(busy, { onChange: go })} onClick={x} />;`],
        ['its own onKeyDown', `const f = () => <input type="radio" onKeyDown={k} {...pendingChoiceProps(busy)} />;`],
    ])('fails a choice with %s beside the pending props', (_shape, snippet) => {
        expect(scanIn(snippet).choiceClashes).toHaveLength(1);
        expect(fieldFailuresIn(snippet).some((f) => f.includes('beside the pendingChoiceProps'))).toBe(true);
    });

    // Each goes through the mechanism, or is unavailable for another reason.
    it.each([
        ['the pending state given as pending', `const f = () => <Input disabled={!isOwner} pending={rename.isPending} />;`, 1],
        ['a raw text field\'s pending props', `const f = () => <input {...pendingTextProps(save.isPending)} />;`, 0],
        ['a raw choice\'s pending props', `const f = () => <select {...pendingChoiceProps(m.isPending, { onChange: go })} />;`, 0],
        ['a field unavailable for a reason', `const f = () => <select disabled={isSoleOwner} />;`, 1],
        ['a readOnly that reads no pending state', `const f = () => <input readOnly disabled />;`, 1],
    ])('does not fail %s', (_shape, snippet, counted) => {
        const scan = scanIn(snippet);
        expect(scan.fieldUses.filter((u) => u.pending)).toEqual([]);
        expect(scan.fieldMarked).toEqual([]);
        expect(scan.choiceClashes).toEqual([]);
        expect(scan.fieldUses).toHaveLength(counted);
    });

    it('counts a reviewed file as it was reviewed, and fails one more', () => {
        const review: Review = { count: 1, reason: 'Only an owner may change it, for the test of the count.' };
        expect(fieldFailuresOf('a.tsx', scanIn(`const f = () => <Input disabled={!isOwner} />;`), review)).toEqual([]);
        expect(fieldFailuresOf('a.tsx', scanIn(`const f = () => <><Input disabled={!isOwner} /><select disabled={x} /></>;`), review))
            .not.toEqual([]);
        // A file not listed fails on its first.
        expect(fieldFailuresOf('a.tsx', scanIn(`const f = () => <Input disabled={!isOwner} />;`), undefined)).not.toEqual([]);
    });

    it('keeps every disabled on a field in the SPA unavailable, never busy', () => {
        const failures: string[] = [];
        for (const [file, scan] of allScans()) failures.push(...fieldFailuresOf(file, scan, FIELD_REVIEWED.get(file)));
        for (const file of FIELD_REVIEWED.keys()) {
            if (!allScans().has(file)) failures.push(`${file} is in FIELD_REVIEWED and is not in the source.`);
        }
        expect(failures).toEqual([]);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    it('keeps every entry honest', () => {
        for (const [file, { count, reason }] of FIELD_REVIEWED) {
            expect(count, file).toBeGreaterThan(0);
            expect(reason.length, file).toBeGreaterThan(40);
        }
    });
});

import { EditorDialogFrame } from '@/components/ui/EditorDialog';

/**
 * Style-guide sample for the editor dialog's chrome (EditorDialog): the header
 * with its title, the muted subtitle and the close button, and the padded body
 * an editor fills. Rendered inline, in cards drawn like the dialog's panel,
 * because the real one opens over the whole page. Each card is the same
 * `@container/dialog` the real panel is, so the narrow one shows the phone
 * header: the subtitle on its own line and a 44px close button.
 *
 * Placeholder text only; nothing here fetches.
 */
export function EditorDialogSamples() {
    const frame = (
        <EditorDialogFrame
            title="Sample Payee"
            subtitle="Oct 28, 2026 · Sample Account"
            onClose={() => {}}
            bodyClassName="space-y-3"
        >
            <p className="text-sm text-text-muted">The editor renders here, with its own footer.</p>
        </EditorDialogFrame>
    );
    return (
        <div className="space-y-4">
            <div className="@container/dialog max-w-5xl rounded-lg border border-border bg-surface shadow-xl">
                {frame}
            </div>
            <div className="@container/dialog max-w-sm rounded-lg border border-border bg-surface shadow-xl">
                {frame}
            </div>
        </div>
    );
}

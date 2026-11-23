import { useEffect, useState, type ReactNode } from 'react';

/** One captioned sample: a title, the sample on the surface, and an optional note. */
export function Board({ title, note, children }: { title: string; note?: string; children: ReactNode }) {
    return (
        <div className="space-y-1.5">
            <p className="text-xs font-semibold text-text">{title}</p>
            <div className="overflow-hidden rounded border border-border bg-surface">{children}</div>
            {note ? <p className="text-[0.6875rem] text-text-muted">{note}</p> : null}
        </div>
    );
}

/**
 * The editor's Date field takes focus when it mounts, as it does when a row is
 * opened in a register. Here several editors mount with the page, and that focus
 * scrolls the page down to the last of them on load, which in turn closes the
 * tag panels the gallery opens above. `inert` makes focus a no-op for the mount
 * only; it is lifted right after, and the editors take focus as anywhere else.
 */
export function WithoutMountFocus({ className, children }: { className: string; children: ReactNode }) {
    const [mounting, setMounting] = useState(true);
    useEffect(() => { setMounting(false); }, []);
    // React 18 has no `inert` prop; an unknown attribute is passed through as given.
    return <div className={className} {...(mounting ? { inert: '' } : {})}>{children}</div>;
}

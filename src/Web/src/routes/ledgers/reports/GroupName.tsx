import { PATH_SEPARATOR, fullGroupName, type GroupNameParts } from './groupLabel';

/**
 * A group's name in a report: its own name, after the categories above it when
 * it has any (`Salary/Base`).
 *
 * ONE COLOUR. The whole name takes the colour of the text around it; no part is
 * quieter than another, since a muted parent reads as a highlighted leaf
 * (ADR-0104 D4, amended 2026-11-21).
 *
 * THE LEAF IS WHAT SURVIVES. A name cut from the end at a chart's label width
 * would keep `Income/Salary/` and lose `Base`, the one part that tells two
 * series apart. So the parents give way first (`Inc…/Base`); the leaf truncates
 * only once they are gone. The whole name is the title, on hover.
 *
 * `wrap` is for a table cell, which has no width of its own to cut to: an
 * auto-layout column takes the widest unbreakable name, so a name kept on one
 * line there widens the sticky column until it covers the figures. Wrapped, the
 * name is plain text and breaks onto more lines as plain text does.
 */
export function GroupName({ parts, wrap = false }: { parts: GroupNameParts; wrap?: boolean }) {
    const full = fullGroupName(parts);
    if (wrap) return <span title={full}>{full}</span>;
    if (parts.parents.length === 0) {
        return <span className="min-w-0 truncate" title={full}>{parts.leaf}</span>;
    }
    return (
        <span className="flex min-w-0" title={full} data-group-path>
            <span className="min-w-0 truncate">{parts.parents.join(PATH_SEPARATOR)}</span>
            <span className="max-w-full shrink-0 truncate">
                {PATH_SEPARATOR}
                {parts.leaf}
            </span>
        </span>
    );
}

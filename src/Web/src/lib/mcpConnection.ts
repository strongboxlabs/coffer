// What a list of MCP connections says about when each was last used. Shared by Account →
// Security and System → MCP so a person and an admin read the same words for the same fact.

/**
 * "Last used", in words. A null time means two different things, and saying so is the point:
 * a personal token with no time has never been used, but an OAuth connection with none may
 * simply predate use being recorded, so "never" would be a claim nothing supports.
 */
export function lastUsedText(kind: 'token' | 'oauth', lastUsedAt: string | null): string {
    if (lastUsedAt === null) {
        return kind === 'token' ? 'never used' : 'no use recorded';
    }
    return `last used ${new Date(lastUsedAt).toLocaleString([], {
        dateStyle: 'medium',
        timeStyle: 'short',
    })}`;
}

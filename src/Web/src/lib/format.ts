/**
 * Human-readable byte size.
 *
 * Extracted from local copies that had drifted: two rendered MB to different
 * precision, and a third carried a GB tier the others lacked — so a large
 * backup could render as "2.00 GB" in one place and "2048.0 MB" in another,
 * on the same screen. `decimals` keeps each caller's MB output exactly what it
 * was, so this is a de-duplication rather than a silent change to anyone's
 * display.
 *
 * GB always carries one more figure than MB does by default, because at that
 * size a single decimal hides a difference of a hundred megabytes.
 */
export function formatBytes(bytes: number, decimals = 1): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(decimals)} MB`;
    return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

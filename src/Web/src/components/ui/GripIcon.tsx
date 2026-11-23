// GripIcon — the six-dot drag handle glyph. Only the glyph: the element that
// carries it decides its role, its keyboard path and when it shows. Decorative,
// so aria-hidden; a drag handle's name belongs on its wrapper.

export function GripIcon({ size = 10 }: { size?: number }) {
    return (
        <svg width={size} height={size} viewBox="0 0 16 16" fill="currentColor" aria-hidden="true">
            <circle cx="6" cy="3" r="1.3" /><circle cx="10" cy="3" r="1.3" />
            <circle cx="6" cy="8" r="1.3" /><circle cx="10" cy="8" r="1.3" />
            <circle cx="6" cy="13" r="1.3" /><circle cx="10" cy="13" r="1.3" />
        </svg>
    );
}

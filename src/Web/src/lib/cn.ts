import { clsx, type ClassValue } from 'clsx';
import { twMerge } from 'tailwind-merge';

/**
 * Compose Tailwind classes with conflict resolution.
 *
 *   cn('p-4', condition && 'p-2')    →  'p-2'   when condition is true
 *   cn('text-red-500', 'text-sm')    →  'text-red-500 text-sm'
 *
 * clsx handles conditional class composition; tailwind-merge resolves
 * conflicts (later class wins, but in a Tailwind-semantic way: e.g.
 * `p-4 p-2` collapses to `p-2`, not just string-concat).
 *
 * This is the same `cn` helper shadcn/ui ships in its generated
 * components. Keeping it here lets us hand-write primitives in the
 * same idiom.
 */
export function cn(...inputs: ClassValue[]): string {
    return twMerge(clsx(inputs));
}

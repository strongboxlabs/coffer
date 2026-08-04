import { LineChart } from 'lucide-react';

/** App brand mark for the unauthenticated card screens (login / setup / invite). */
export function BrandHeader() {
    return (
        <div className="mb-6 flex items-center gap-2">
            <LineChart className="h-5 w-5 text-accent" strokeWidth={2.25} aria-hidden />
            <span className="text-base font-bold tracking-tight">Coffer</span>
        </div>
    );
}

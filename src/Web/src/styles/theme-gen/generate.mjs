// Writes src/styles/themes.generated.css from the palette model.
// Run with: npm run themes:gen
import { writeFileSync } from 'node:fs';
import { emit } from './emit.ts';

const out = new URL('../themes.generated.css', import.meta.url);
writeFileSync(out, emit());
console.log(`wrote ${out.pathname}`);

// `defineConfig` from `vitest/config` is a superset of vite's that
// also types the `test` block — vite's own defineConfig rejects the
// test config with TS2769 since vite doesn't know about Vitest.
import { defineConfig } from 'vitest/config';
import path from 'node:path';
import { execSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// ADR-0044: the UI version axis, stamped into the bundle at build time.
// Mirrors the API's three pieces — semver (package.json), a monotonic
// build number (git commit count), and the short SHA — plus the commit
// date. Each git call is guarded so a .git-less build (a Docker layer
// without the repo) degrades to build 0 / commit "dev" rather than
// failing the build.
function git(args: string, fallback: string): string {
    try {
        return execSync(`git ${args}`, { stdio: ['ignore', 'pipe', 'ignore'] })
            .toString()
            .trim();
    } catch {
        return fallback;
    }
}

const pkg = JSON.parse(
    readFileSync(path.resolve(__dirname, 'package.json'), 'utf8'),
) as { version: string };

const appVersion = {
    version: pkg.version,
    build: Number(git('rev-list --count HEAD', '0')),
    commit: git('rev-parse --short HEAD', 'dev'),
    commitDate: git('log -1 --format=%cd --date=short', ''),
};

// Vite config for the Coffer SPA (ADR-0007). Two notable bits:
//
//   1. /api proxy. The .NET API runs on http://localhost:5000 by default;
//      Vite's dev server runs on :5173. Routing /api/* through Vite means
//      cookies + same-origin AJAX work transparently in dev — the browser
//      treats /api requests as same-origin (no preflight, no CORS dance),
//      and the auth cookie (SameSite=Strict, HttpOnly) flows back without
//      special handling. Production deployments serve the SPA behind the
//      same reverse proxy as the API so this setup carries over.
//
//   2. Code-based routing. TanStack Router supports file-based routing
//      via a Vite plugin that generates `routeTree.gen.ts`. We use the
//      code-based API (defined in src/App.tsx) instead: the route tree
//      is hand-authored and reads top-to-bottom, no codegen step in the
//      build pipeline, no generated file to keep in sync with source.
//      Trade-off is a slightly more verbose route declaration; gain is
//      explicit-over-magic, which is the engineering posture for this
//      project (see memory feedback_frontend_engineering_posture).
export default defineConfig({
    // Tailwind v4 ships as a Vite plugin (no postcss.config.js, no
    // tailwind.config.ts). The theme tokens live in src/index.css
    // inside an @theme block per ADR-0021.
    plugins: [react(), tailwindcss()],
    // ADR-0044: compile-time constants for the About panel's UI row.
    // Declared in src/vite-env.d.ts. JSON.stringify so each value is
    // inlined as a literal (numbers as numbers, strings quoted).
    define: {
        __APP_VERSION__: JSON.stringify(appVersion.version),
        __APP_BUILD__: JSON.stringify(appVersion.build),
        __APP_COMMIT__: JSON.stringify(appVersion.commit),
        __APP_COMMIT_DATE__: JSON.stringify(appVersion.commitDate),
    },
    resolve: {
        alias: {
            '@': path.resolve(__dirname, './src'),
        },
    },
    server: {
        port: 5173,
        // Fail loudly if :5173 is taken instead of silently walking to
        // 5174+ — a drifted port lands the SPA on an origin the API's
        // Fido2 allow-list doesn't cover, breaking WebAuthn. Kill the
        // stale dev server rather than run on a surprise port.
        strictPort: true,
        proxy: {
            '/api': {
                target: 'http://localhost:5000',
                changeOrigin: false,
                // changeOrigin=false keeps the Host header so ASP.NET
                // Core's cookie auth sees the browser's actual origin —
                // matters for cookie scope checks.
            },
        },
    },
    test: {
        globals: true,
        environment: 'jsdom',
        setupFiles: ['./vitest.setup.ts'],
        css: false,
        // Use the process-fork pool, not the default worker-thread pool: the
        // thread pool hangs on some Windows hosts (workers never report ready →
        // "Vitest failed to find the runner", every file fails at setup). Forks
        // are the more portable pool and run identically on the Linux CI, so
        // `npm run test` works everywhere without a per-invocation flag.
        pool: 'forks',
    },
});

# Coffer Web

The Coffer SPA, per [ADR-0007](../../docs/decisions/0007-react-spa-over-nextjs.md).

## Stack (pinned, boring on purpose)

- **Vite 5** + **React 18** + **TypeScript 5.7** (strict). Pinned to the most-trodden tier of each — see [`feedback_frontend_engineering_posture`](#) (memory) and [engineering-standards §4.3.1](../../docs/engineering-standards.md).
- **TanStack Router** (code-based, no codegen) + **TanStack Query** (one client, 30s staleTime).
- **Tailwind v3** + hand-built shadcn-style primitives (`components/ui/{Button,Input,Label}.tsx`).
- **`@simplewebauthn/browser`** for the login ceremony.
- **Vitest** + Testing Library for component tests.

## Scripts

| Command | What it does |
|---|---|
| `npm run dev` | Vite dev server on `:5173`. Proxies `/api/*` to `http://localhost:5000` (the .NET API). |
| `npm run typecheck` | `tsc -b --noEmit` over the project references. |
| `npm run lint` | ESLint flat config (typescript-eslint + react-hooks + react-refresh). |
| `npm test` | Vitest (one-shot). `npm run test:watch` for interactive. |
| `npm run build` | `tsc -b && vite build` → `dist/`. |
| `npm run preview` | Serves the production build for sanity-check. |

## Layout

See [`engineering-standards §4.3.2`](../../docs/engineering-standards.md) for the layout convention. In short:

- `src/main.tsx` — mount point.
- `src/App.tsx` — pure components (`RootLayout`, `AuthedOutlet`).
- `src/router.ts` — code-based route tree + auth-check `beforeLoad`.
- `src/lib/` — `api.ts` (typed fetch + `ApiError`), `auth.ts` (WebAuthn), `cn.ts`, `types.ts`.
- `src/components/ui/` — primitives (`Button`, `Input`, `Label`).
- `src/routes/<route>/` — per-route folder with component + tests.

## Security posture

- Cookie auth is HttpOnly + SameSite=Strict (set server-side per ADR-0013). The SPA can't read it; the only authoritative "am I authenticated" answer comes from `GET /api/auth/me`.
- Every `fetch` sets `credentials: 'include'` explicitly so the cookie travels.
- No `dangerouslySetInnerHTML`, no `eval`, no auth state in `localStorage`. Errors from the API surface verbatim via `ApiError.detail`; we don't try to localise or categorise.
- Same-origin in dev via the Vite `/api` proxy; production deploys the SPA behind the same reverse proxy as the API.

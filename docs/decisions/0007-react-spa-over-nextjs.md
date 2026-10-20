# 0007 — Vite + React SPA over Next.js

* Status: Accepted
* Date: 2026-05-08

## Context

The frontend is a single-user app talking to a dedicated .NET API. There is no SEO, no public marketing surface, no anonymous traffic pattern that would benefit from server-side rendering or static generation, and no content-mainly pages.

Next.js's primary value propositions are SSR, SSG, ISR, image optimization, and a full-stack development experience where the same codebase serves API routes alongside pages. Almost none of these apply here:

- We already have a dedicated, opinionated backend (.NET).
- The user is authenticated and the app is internet-accessible behind Traefik auth — there's no anonymous SEO surface.
- Everything is interactive; no benefit from SSG.
- API routes would create a parallel and confusing "where does this endpoint live" question.

## Decision

The frontend is a **Vite + React + TypeScript** SPA, served as static files via Traefik (or as static middleware on the .NET app, configurable). State is managed by **TanStack Query** (server state) and either Zustand or React Context (UI state). No Redux. Styling via **Tailwind** + **shadcn/ui** primitives. Charts via **Recharts**.

## Consequences

**Positive**
- Build pipeline is small and fast (Vite).
- One backend, not two. The .NET API is the single source of business logic.
- Trivial to deploy: `npm run build` produces static files; serve them anywhere.
- Mobile (phone browser) works without separate code paths.

**Negative**
- No SSR; the SPA must boot before showing meaningful content. For an authenticated single-user app, the cold-start is paid once per session and is negligible.
- We give up Next.js's image optimization. Not relevant to a finance UI.

## Alternatives considered

- **Next.js (App Router).** Adds complexity for benefits we don't need. Rejected.
- **Blazor WebAssembly.** A legitimate option that keeps the stack in C#. The .NET backend is unchanged either way. If React proves frustrating after the first few phases, Blazor is the documented fallback. Not chosen now because the React/TanStack/shadcn ecosystem has more mature finance-UI primitives (react-virtuoso for big registers, Recharts) than the Blazor equivalent.
- **HTMX + .NET Razor pages.** Lightweight server-rendered pattern. Capable but harder to do interactive virtualized lists and live-updating SSE feeds well. Rejected; can revisit if React tooling burns out.
- **Plain Vite + plain HTML/JS.** Too thin for the merge-review queue and dashboard work. Rejected.

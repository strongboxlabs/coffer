/// <reference types="vite/client" />

// ADR-0044: build-time constants injected by Vite's `define` (see
// vite.config.ts). They form the About panel's UI version row.
declare const __APP_VERSION__: string;
declare const __APP_BUILD__: number;
declare const __APP_COMMIT__: string;
declare const __APP_COMMIT_DATE__: string;

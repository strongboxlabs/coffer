import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';

import { createAppRouter } from './router';
import './index.css';

// One QueryClient per app. The 30s staleTime suppresses
// thundering-herd refetches when the user clicks between routes that
// share queries (e.g. the auth-check query keyed by ['me']);
// refetchOnWindowFocus stays at TanStack's default (true) so the app
// freshens itself when the user returns to the tab.
//
// retry: 1 — one retry is enough for transient network blips. We
// don't want to mask a real 4xx by retrying further.
const queryClient = new QueryClient({
    defaultOptions: {
        queries: {
            staleTime: 30_000,
            retry: 1,
        },
    },
});

const router = createAppRouter(queryClient);

const rootElement = document.getElementById('root');
if (!rootElement) {
    throw new Error('Root element #root is missing from index.html.');
}

createRoot(rootElement).render(
    <StrictMode>
        <QueryClientProvider client={queryClient}>
            <RouterProvider router={router} />
        </QueryClientProvider>
    </StrictMode>,
);

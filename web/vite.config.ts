import cascadeLayers from '@csstools/postcss-cascade-layers';
import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
    plugins: [react(), tailwindcss()],
    // Customer servers run whatever Edge shipped with Windows Server 2022 (Chromium 92).
    // Tailwind puts all its CSS in @layer, which Chromium < 99 drops entirely, so the
    // layers are flattened into plain specificity, and JS/CSS syntax is lowered to match.
    css: {
        postcss: { plugins: [cascadeLayers()] },
    },
    build: {
        outDir: '../src/TraceMQ.Api/wwwroot',
        emptyOutDir: true,
        target: ['chrome92', 'edge92'],
        cssTarget: ['chrome92', 'edge92'],
    },
    server: {
        port: 5173,
        proxy: { '/api': 'http://localhost:5027' },
    },
    test: {
        environment: 'jsdom',
        globals: true,
        include: ['src/**/*.test.ts', 'src/**/*.test.tsx'],
    },
});

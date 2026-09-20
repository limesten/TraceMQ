import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
    plugins: [react(), tailwindcss()],
    build: {
        outDir: '../src/TraceMQ.Api/wwwroot',
        emptyOutDir: true,
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

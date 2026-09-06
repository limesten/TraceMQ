import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// https://vite.dev/config/
export default defineConfig({
    plugins: [react()],
    build: {
        outDir: '../src/TraceMQ.Api/wwwroot',
        emptyOutDir: true,
    },
    server: {
        port: 5173,
        proxy: { '/api': 'http://localhost:5027' },
    },
});

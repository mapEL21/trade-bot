import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    // Runtime files and launcher locks are not frontend source files.
    watch: {
      ignored: [
        '**/artifacts/**',
        '**/.tools/**',
        // Downloaded candle history: tens of thousands of files that made the dev server start for half a minute.
        '**/data/**',
        '**/research/**',
        '**/server/**/bin/**',
        '**/server/**/obj/**',
      ],
    },
    proxy: {
      '/api': process.env.TRADE_BOT_API_URL || 'http://127.0.0.1:5080',
      '/openapi': process.env.TRADE_BOT_API_URL || 'http://127.0.0.1:5080',
    },
  },
});

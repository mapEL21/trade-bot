import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': process.env.TRADE_BOT_API_URL || 'http://127.0.0.1:5080',
      '/openapi': process.env.TRADE_BOT_API_URL || 'http://127.0.0.1:5080',
    },
  },
});

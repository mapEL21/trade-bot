import { defineConfig } from '@playwright/test';
import { resolve } from 'node:path';

export default defineConfig({
  testDir: './tests/browser',
  fullyParallel: true,
  use: {
    baseURL: 'http://127.0.0.1:5175',
    channel: process.env.PLAYWRIGHT_CHANNEL || undefined,
    viewport: { width: 1440, height: 1000 },
    trace: 'retain-on-failure',
  },
  outputDir: 'artifacts/test-results',
  webServer: [
    {
      command:
        'node scripts/dotnet.mjs run --no-build --project server/TradeBot.Api -- --Api:Port=5081',
      url: 'http://127.0.0.1:5081/api/v1/health',
      env: {
        Storage__Path: resolve('artifacts/browser-workspace.db'),
        Ui__Origin: 'http://127.0.0.1:5175',
      },
      reuseExistingServer: false,
    },
    {
      command: 'pnpm dev --port 5175 --strictPort',
      url: 'http://127.0.0.1:5175',
      env: { TRADE_BOT_API_URL: 'http://127.0.0.1:5081' },
      reuseExistingServer: false,
    },
  ],
});

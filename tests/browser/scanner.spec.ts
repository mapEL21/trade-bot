import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const minute = 60000;
const start = 28_333_333 * minute;
const candles = Array.from({ length: 60 }, (_, i) => ({
  openMs: start + i * minute,
  open: 99,
  high: i === 10 || i === 30 ? 100 : 99.5,
  low: 98.5,
  close: 99.3,
  quoteVolume: 1000,
}));
const candidate = {
  id: 'XUSDT-1',
  symbol: 'XUSDT',
  side: 'long',
  atMs: start + 59 * minute,
  level: 100,
  price: 100.06,
  touches: 3,
  score: 73,
  items: [
    { name: 'Касания', points: 18, detail: '3 касания уровня' },
    { name: 'Активность ленты', points: 20, detail: 'Объём за 10 с в 5× выше среднего' },
    { name: 'Подлёт', points: -20, detail: 'За 5 мин цена прошла к уровню 2% без проторговки' },
  ],
  result: 'target',
  maxFavorablePct: 1.62,
  maxAdversePct: -0.12,
  lastPct: 1.6,
  resolvedMs: start + 61 * minute,
};
const level = (side: string, price: number) => ({
  side,
  price,
  zoneLow: price - 0.3,
  zoneHigh: price + 0.3,
  touches: 2,
  distancePct: 0.4,
});
const scanning = {
  id: '11111111-1111-1111-1111-111111111111',
  state: 'scanning',
  explanation: 'Сканер следит за 2 монетами.',
  startedUtc: new Date().toISOString(),
  seconds: 3600,
  events: 12345,
  bytes: 52428800,
  ignored: 0,
  symbols: [
    {
      symbol: 'XUSDT',
      quoteVolume: 300_000_000,
      price: 99.6,
      resistance: level('resistance', 100),
      support: null,
      candidates: 1,
    },
    {
      symbol: 'YUSDT',
      quoteVolume: 180_000_000,
      price: 2.5,
      resistance: null,
      support: level('support', 2.4),
      candidates: 0,
    },
  ],
  candidates: [candidate],
  settings: { targetPct: 1.5, stopBufferPct: 0.15, outcomeMinutes: 10, breakoutBufferPct: 0.05 },
};

async function server(page: Page) {
  const state = {
    snapshot: { ...scanning, state: 'idle', id: null, symbols: [], candidates: [] } as object,
    started: null as unknown,
  };
  await page.route('**/api/v1/workspace', (route) =>
    route.fulfill({ json: { schemaVersion: 1, bots: [], events: [] } }),
  );
  await page.route('**/api/v1/strategies', (route) => route.fulfill({ json: [] }));
  const group = (name: string, count: number, targets: number, meanR: number) => ({
    name,
    count,
    targets,
    stops: count - targets,
    timeouts: 0,
    targetShare: count ? (targets * 100) / count : 0,
    meanPct: meanR / 3,
    meanR,
    sumR: meanR * count,
  });
  await page.route('**/api/v1/scanner/stats*', (route) =>
    route.fulfill({
      json: {
        journals: 3,
        unreadable: 0,
        total: 42,
        open: 2,
        feePct: 0.1,
        all: group('Все пробои', 40, 14, 0.25),
        byScore: [group('Оценка 0–39', 10, 2, -0.4), group('Оценка 60–79', 30, 12, 0.47)],
        bySide: [group('Лонг', 25, 9, 0.3), group('Шорт', 15, 5, 0.17)],
        features: [
          {
            name: 'Крупный принт',
            with: group('С признаком', 12, 7, 0.9),
            without: group('Без признака', 28, 7, -0.03),
          },
        ],
        notes: ['Завершённых пробоев 40: для выводов нужно хотя бы 200–300.'],
      },
    }),
  );
  await page.route('**/api/v1/storage', (route) =>
    route.fulfill({
      json: {
        enabled: true,
        checkedUtc: new Date().toISOString(),
        recordings: 12,
        recordingBytes: 8 * 1073741824,
        retentionDays: 14,
        freeBytes: 70 * 1073741824,
        totalBytes: 100 * 1073741824,
        lastBackup: 'workspace-20261012-000000.db',
        error: null,
      },
    }),
  );
  await page.route('**/api/v1/scanner', (route) => {
    if (route.request().method() === 'POST') {
      state.started = route.request().postDataJSON();
      state.snapshot = scanning;
      return route.fulfill({ status: 202, json: { id: scanning.id } });
    }
    return route.fulfill({ json: state.snapshot });
  });
  await page.route('**/api/v1/scanner/symbols/*', (route) =>
    route.fulfill({
      json: {
        symbol: 'XUSDT',
        candles,
        levels: [level('resistance', 100)],
        candidates: [candidate],
      },
    }),
  );
  await page.route('**/api/v1/scanner/*/stop', (route) => {
    state.snapshot = { ...scanning, state: 'stopped' };
    return route.fulfill({ status: 202, json: { id: scanning.id } });
  });
  return state;
}

test('scanner starts with chosen limits, shows scored breakouts, coins and a chart with level zones', async ({
  page,
}) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  const state = await server(page);
  await page.goto('/');
  await page.getByRole('button', { name: 'Лаборатория', exact: true }).first().click();
  await expect(page.getByRole('heading', { name: /Сканер пробоев уровня/ })).toBeVisible();
  // Statistics over the saved journals: overall edge, by score band and by each sign.
  const stats = page.locator('.scan-stats');
  await expect(stats).toContainText('Завершённых пробоев');
  await expect(stats).toContainText('+0,25 R');
  await expect(stats).toContainText('+10% депозита');
  await expect(stats.getByRole('table', { name: 'По оценке' })).toContainText('Оценка 60–79');
  await expect(stats.getByRole('table', { name: 'По признакам оценки' })).toContainText(
    '12 · 58% · +0,9 R',
  );
  await expect(stats).toContainText('нужно хотя бы 200–300');
  await page.getByLabel('Объём за 24 ч от, млн $').fill('200');
  await page.getByLabel('Монет не больше').fill('15');
  await page.getByLabel('Исключить').fill('btc, ethusdt');
  await page.getByLabel(/^Непрерывно/).check();
  await page.getByRole('button', { name: 'Запустить сканер' }).click();
  await expect
    .poll(() => state.started)
    .toEqual({
      seconds: 3600,
      minQuoteVolume: 200_000_000,
      maxSymbols: 15,
      exclude: ['BTCUSDT', 'ETHUSDT'],
      continuous: true,
    });
  await expect(
    page.getByText(/Диск: свободно 70 ГБ из 100 ГБ · записи 8 ГБ \(12\) · хранятся 14 дн\./),
  ).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'Сканирует' })).toBeVisible();
  const breakouts = page.getByRole('table', { name: /Пробои уровней/ });
  await expect(breakouts).toContainText('XUSDT');
  await expect(breakouts).toContainText('Дошёл до цели');
  await breakouts.getByText('73').click();
  await expect(breakouts).toContainText('Подлёт: За 5 мин цена прошла к уровню 2% без проторговки');
  await expect(page.getByRole('table', { name: /Монеты сканера/ })).toContainText('300 млн $');
  await page
    .getByRole('table', { name: /Монеты сканера/ })
    .getByRole('button', { name: 'XUSDT' })
    .click();
  await expect(
    page.getByRole('img', { name: /XUSDT: 60 минутных свечей, уровней 1, пробоев 1/ }),
  ).toBeVisible();
  await page.screenshot({ path: 'artifacts/scanner-desktop.png', fullPage: true });
  expect((await new AxeBuilder({ page }).include('.scanner-page').analyze()).violations).toEqual(
    [],
  );
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.getByRole('button', { name: 'Остановить' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Остановлен' })).toBeVisible();
  expect(errors).toEqual([]);
});

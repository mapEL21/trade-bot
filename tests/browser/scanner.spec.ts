import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const scanning = {
  id: '11111111-1111-1111-1111-111111111111',
  state: 'scanning',
  explanation: 'Пишем рынок 2 монет.',
  startedUtc: new Date().toISOString(),
  seconds: 3600,
  events: 12345,
  bytes: 52428800,
  segment: 1,
  symbols: [
    { symbol: 'XUSDT', quoteVolume: 300_000_000, trades: 4321, liquidations: 7, rangePct: 9.4 },
    { symbol: 'YUSDT', quoteVolume: 180_000_000, trades: 950, liquidations: 0, rangePct: 5 },
  ],
};

async function server(page: Page) {
  const state = {
    snapshot: { ...scanning, state: 'idle', id: null, symbols: [] } as object,
    started: null as unknown,
  };
  await page.route('**/api/v1/workspace', (route) =>
    route.fulfill({ json: { schemaVersion: 1, bots: [], events: [] } }),
  );
  await page.route('**/api/v1/strategies', (route) => route.fulfill({ json: [] }));
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
  await page.route('**/api/v1/scanner/*/continuous', (route) => {
    const { continuous } = route.request().postDataJSON() as { continuous: boolean };
    state.snapshot = { ...(state.snapshot as typeof scanning), continuous };
    return route.fulfill({ json: state.snapshot });
  });
  await page.route('**/api/v1/scanner/*/stop', (route) => {
    state.snapshot = { ...scanning, state: 'stopped' };
    return route.fulfill({ status: 202, json: { id: scanning.id } });
  });
  return state;
}

test('data recording starts with chosen limits and shows what arrives per coin', async ({
  page,
}) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  const state = await server(page);
  await page.goto('/');
  await page
    .getByRole('button', { name: /^Лаборатория/ })
    .first()
    .click();
  await page.getByRole('button', { name: 'Запись данных', exact: true }).click();
  await expect(page.getByRole('heading', { name: /Запись данных · Binance/ })).toBeVisible();
  await page.getByLabel('Объём за 24 ч от, млн $').fill('200');
  await page.getByLabel('Монет не больше').fill('15');
  await page.getByLabel('Размах за 24 ч от, %').fill('5');
  await page.getByLabel('Исключить').fill('btc, ethusdt');
  await page.getByLabel(/^Непрерывно/).check();
  await page.getByRole('button', { name: 'Начать запись' }).click();
  await expect
    .poll(() => state.started)
    .toEqual({
      seconds: 3600,
      minQuoteVolume: 200_000_000,
      maxSymbols: 15,
      exclude: ['BTCUSDT', 'ETHUSDT'],
      continuous: true,
      minRangePct: 5,
    });
  await expect(
    page.getByText(/Диск: свободно 70 ГБ из 100 ГБ · записи 8 ГБ \(12\) · хранятся 14 дн\./),
  ).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'Пишет' })).toBeVisible();
  const coins = page.getByRole('table', { name: /Монеты записи/ });
  await expect(coins).toContainText('300 млн $');
  await expect(coins).toContainText('4 321');
  await expect(coins).toContainText('9,4%');
  // Auto-renewal is switched on and off while the recording runs.
  const renew = page.getByLabel(/^Авто-продление/);
  await expect(renew).not.toBeChecked();
  await renew.click();
  await expect(renew).toBeChecked();
  await expect(page.getByText(/Непрерывный режим · запись/)).toBeVisible();
  await renew.click();
  await expect(renew).not.toBeChecked();
  // The live breakout search is gone from this page.
  await expect(page.getByText(/Пробои|Статистика пробоев/)).toHaveCount(0);
  await page.screenshot({ path: 'artifacts/scanner-desktop.png', fullPage: true });
  expect((await new AxeBuilder({ page }).include('.scanner-page').analyze()).violations).toEqual(
    [],
  );
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.getByRole('button', { name: 'Остановить' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Остановлена' })).toBeVisible();
  expect(errors).toEqual([]);
});

test('signal lab runs the recordings, ranks signals after fees and charts the chosen one', async ({
  page,
}) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  let state = 'idle';
  await page.route('**/api/v1/workspace', (route) =>
    route.fulfill({ json: { schemaVersion: 1, bots: [], events: [] } }),
  );
  await page.route('**/api/v1/strategies', (route) => route.fulfill({ json: [] }));
  const signal = (type: string, episodes: number, meanPct: number | null) => ({
    type,
    mode: 'follow',
    horizon: 300,
    episodes,
    needed: 50,
    meanPct,
    winRate: meanPct === null ? null : 60,
    firstHalf: null,
    secondHalf: null,
    verdict: 'collecting',
  });
  await page.route('**/api/v1/lab/hypothesis', (route) =>
    route.fulfill({
      json: {
        statement:
          'После выноса цены или каскада ликвидаций сделка по движению с удержанием 5 минут даёт плюс после комиссий 0,1%.',
        fromMs: Date.UTC(2026, 9, 15, 21),
        feePct: 0.1,
        hours: 12.5,
        signals: [signal('Вынос цены', 12, 0.4), signal('Каскад ликвидаций', 0, null)],
        verdict: 'collecting',
        explanation: 'Собираем данные: каждому сигналу нужно 50 эпизодов с начала проверки.',
      },
    }),
  );
  await page.route('**/api/v1/lab/status', (route) =>
    route.fulfill({
      json: {
        state,
        explanation: 'Обработано записей: 2 из 2.',
        done: 2,
        total: 2,
        lines: 400000,
        current: null,
      },
    }),
  );
  await page.route('**/api/v1/lab/run', (route) => {
    state = 'finished';
    return route.fulfill({ status: 202 });
  });
  const row = (
    type: string,
    mode: string,
    horizon: number,
    count: number,
    mean: number,
    stable: boolean,
  ) => ({
    type,
    mode,
    horizon,
    count,
    meanPct: mean,
    winRate: 55,
    firstHalf: mean + 0.01,
    secondHalf: stable ? mean - 0.01 : -0.02,
    stable,
    episodes: count,
  });
  await page.route('**/api/v1/lab/report*', (route) =>
    route.fulfill({
      json: {
        recordings: 2,
        hours: 1.5,
        events: 420,
        feePct: 0.1,
        counts: [
          { type: 'Каскад ликвидаций', count: 140, perHour: 93.3, episodes: 120 },
          { type: 'Перекос стакана', count: 280, perHour: 186.7, episodes: 250 },
        ],
        rows: [
          row('Перекос стакана', 'follow', 5, 280, -0.09, false),
          row('Каскад ликвидаций', 'fade', 15, 140, 0.05, true),
          row('Каскад ликвидаций', 'follow', 300, 12, 0.3, false),
        ],
        notes: ['Комбинаций 48: часть может оказаться в плюсе случайно.'],
        fromMs: new URL(route.request().url()).searchParams.has('fromMs')
          ? Number(new URL(route.request().url()).searchParams.get('fromMs'))
          : null,
        session: new URL(route.request().url()).searchParams.get('session'),
      },
    }),
  );
  await page.route('**/api/v1/lab/sessions*', (route) =>
    route.fulfill({
      json: [
        {
          session: 'asia',
          name: 'Азия · 03:00–11:00 МСК',
          episodes: 40,
          meanPct: -0.12,
          winRate: 40,
          hours: 8,
        },
        {
          session: 'europe',
          name: 'Европа · 11:00–16:00 МСК',
          episodes: 30,
          meanPct: 0.21,
          winRate: 57,
          hours: 5,
        },
        {
          session: 'us',
          name: 'США и Европа · 16:00–00:00 МСК',
          episodes: 50,
          meanPct: 0.08,
          winRate: 52,
          hours: 8,
        },
        {
          session: 'late',
          name: 'Тихие часы · 00:00–03:00 МСК',
          episodes: 0,
          meanPct: null,
          winRate: null,
          hours: 3,
        },
      ],
    }),
  );
  await page.route('**/api/v1/lab/spreads*', (route) =>
    route.fulfill({
      json: [
        {
          symbol: 'ZUSDT',
          snapshots: 7200,
          meanBps: 6.4,
          above4Pct: 71.2,
          above10Pct: 12.5,
          recordings: 2,
        },
        {
          symbol: 'XUSDT',
          snapshots: 7200,
          meanBps: 1.1,
          above4Pct: 0.4,
          above10Pct: 0,
          recordings: 2,
        },
      ],
    }),
  );
  await page.route('**/api/v1/lab/trades*', (route) =>
    route.fulfill({
      json: [0, 1, 2].map((i) => ({
        id: `lab-${i}`,
        at: new Date(Date.parse('2026-10-15T10:00:00Z') + i * 60000).toISOString(),
        side: 'short',
        gross: i === 1 ? -0.05 : 0.2,
        fee: 0.1,
        entry: 100,
        exit: 99.8,
        quantity: 1,
        runId: 'lab',
        simulated: true,
        symbol: 'XUSDT',
        exchange: 'Binance',
        holdSeconds: 15,
        notional: 100,
        exitReason: '4 ликвидации шортов за 10 с',
      })),
    }),
  );
  await page.goto('/');
  await page
    .getByRole('button', { name: /^Лаборатория/ })
    .first()
    .click();
  await expect(page.getByRole('heading', { name: /Лаборатория сигналов/ })).toBeVisible();
  // The pre-registered check: progress per signal, no verdict before 50 episodes.
  const hypothesis = page.locator('.hypothesis');
  await expect(hypothesis).toContainText('Собираем данные');
  await expect(hypothesis).toContainText('Только события с 16.10.2026, 00:00 (МСК)');
  await expect(hypothesis).toContainText('12 из 50');
  await expect(hypothesis).toContainText('пока +0,4% в среднем');
  await expect(hypothesis).toContainText('эпизодов пока нет');
  await page.getByRole('button', { name: 'Обработать новые записи' }).click();
  await expect(page.getByText('Устойчиво в плюсе после комиссий: 1')).toBeVisible();
  await expect(page.locator('.signal-lab [role="alert"]')).toHaveCount(0);
  const table = page.getByRole('table', { name: /Результат сигналов/ });
  // Rare combinations (under 30 events) are hidden until asked for; the best one comes first.
  await expect(table.locator('tbody tr')).toHaveCount(2);
  await expect(table.locator('tbody tr').first()).toContainText('Каскад ликвидаций');
  await expect(table.locator('tbody tr').first()).toContainText('Против движения');
  // A hypothesis is checked on new data only: the report is asked for events from the chosen moment.
  await page.getByLabel('Только события с').fill('2026-10-16T00:00');
  const fromRequest = page.waitForRequest(
    (r) => r.url().includes('/lab/report') && r.url().includes('fromMs='),
  );
  await page.getByRole('button', { name: 'Пересчитать' }).click();
  expect(new URL((await fromRequest).url()).searchParams.get('fromMs')).toBe(
    String(new Date('2026-10-16T00:00').getTime()),
  );
  await expect(page.getByText(/Учтены только события с/)).toBeVisible();
  await page.getByRole('button', { name: 'Все данные' }).click();
  await expect(page.getByText(/Учтены только события с/)).toHaveCount(0);
  // One trading session at a time.
  const sessionRequest = page.waitForRequest((r) => r.url().includes('session=asia'));
  await page.getByLabel('Торговая сессия').selectOption('asia');
  await sessionRequest;
  await expect(page.getByText(/Только сессия «Азия/)).toBeVisible();
  await page.getByLabel('Торговая сессия').selectOption('');
  // Scalping check: where the spread alone pays two maker fees.
  const spreads = page.getByRole('table', { name: /Спред лучших цен/ });
  await expect(spreads.locator('tbody tr').first()).toContainText('ZUSDT');
  await expect(spreads).toContainText('71,2% времени');
  await page.getByLabel(/Показывать и редкие/).check();
  await expect(table.locator('tbody tr')).toHaveCount(3);
  await table.locator('tbody tr').nth(1).getByRole('button', { name: 'Каскад ликвидаций' }).click();
  await expect(
    page.getByRole('heading', { name: /Каскад ликвидаций · против движения · 15 с/ }),
  ).toBeVisible();
  await expect(page.getByRole('img', { name: /Кривая накопленного результата/ })).toBeVisible();
  const bySession = page.getByRole('table', { name: /по торговым сессиям/ });
  await expect(bySession.locator('tbody tr')).toHaveCount(4);
  await expect(bySession.locator('tbody tr').nth(1)).toContainText('+0,21%');
  await expect(page.locator('.trades-panel')).toContainText('Расчёт по записям рынка');
  expect((await new AxeBuilder({ page }).include('.signal-lab').analyze()).violations).toEqual([]);
  await page.screenshot({ path: 'artifacts/lab-desktop.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(errors).toEqual([]);
});

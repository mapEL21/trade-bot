import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const idle = {
  id: null,
  state: 'idle',
  active: false,
  coin: null,
  symbol: null,
  seconds: 0,
  elapsedSeconds: 0,
  written: 0,
  bytes: 0,
  gaps: 0,
  directory: null,
  error: null,
  rawDifferencePercent: null,
  feeds: [],
  events: [],
  executionEnabled: false,
};
const quote = {
  bid: '1.00',
  ask: '1.02',
  bidSize: '100',
  askSize: '50',
  mid: '1.01',
  spreadBps: 198.02,
  receivedUtc: '2026-10-09T10:00:00Z',
  ageMs: 10,
  stale: false,
};
const live = {
  ...idle,
  id: '11111111-1111-1111-1111-111111111111',
  state: 'recording',
  active: true,
  coin: 'ARB',
  symbol: 'ARBUSDT',
  seconds: 60,
  written: 300,
  bytes: 2097152,
  elapsedSeconds: 12,
  rawDifferencePercent: 0.1234,
  feeds: ['hyperliquid', 'binance-public', 'binance-market'].map((id) => ({
    id,
    status: 'live',
    messages: 100,
    gaps: 0,
    lastEventUtc: quote.receivedUtc,
    eventAgeMs: 10,
    quote: id === 'binance-market' ? null : quote,
  })),
};

// Browser fixtures replace only the transport. Production code never contains synthetic quotes.
async function installFeed(page: Page, snapshot: unknown) {
  await page.route('**/api/v1/research/markets?*', (route) => {
    const exchange = new URL(route.request().url()).searchParams.get('exchange') ?? 'binance';
    return route.fulfill({
      json: {
        exchange,
        sourceExchange:
          new URL(route.request().url()).searchParams.get('sourceExchange') ?? 'hyperliquid',
        updatedUtc: '2026-10-10T10:00:00Z',
        instruments: ['ARB', 'SOL'].map((coin) => ({
          coin,
          symbol:
            exchange === 'hyperliquid'
              ? coin
              : exchange === 'okx'
                ? `${coin}-USDT-SWAP`
                : `${coin}USDT`,
        })),
      },
    });
  });
  await page.addInitScript((initial) => {
    const state = window as unknown as { researchFixture: unknown; researchSilence: boolean };
    state.researchFixture = initial;
    state.researchSilence = false;
    class FixtureSource {
      onmessage: ((e: { data: string }) => void) | null = null;
      onerror: (() => void) | null = null;
      timer = setInterval(() => {
        if (!state.researchSilence)
          this.onmessage?.({ data: JSON.stringify(state.researchFixture) });
      }, 100);
      close() {
        clearInterval(this.timer);
      }
    }
    Object.defineProperty(window, 'EventSource', { value: FixtureSource });
  }, snapshot);
}
async function emit(page: Page, value: unknown) {
  await page.evaluate((snapshot) => {
    (window as unknown as { researchFixture: unknown }).researchFixture = snapshot;
  }, value);
}
async function openResearch(page: Page) {
  await page.goto('/');
  const menu = page.getByRole('button', { name: 'Открыть меню' });
  if (await menu.isVisible()) await menu.click();
  await page.getByRole('button', { name: 'Исследование рынка', exact: true }).click();
}

test('recording controls use server commands and session survives navigation; silent stream hides comparison', async ({
  page,
}) => {
  await installFeed(page, idle);
  let starts = 0,
    stops = 0;
  await page.route('**/api/v1/research', async (route) => {
    expect(route.request().method()).toBe('POST');
    expect(route.request().postDataJSON()).toEqual({
      coin: 'ARB',
      exchange: 'binance',
      sourceExchange: 'hyperliquid',
      seconds: 60,
      maxMb: 256,
    });
    starts++;
    await emit(page, live);
    await route.fulfill({
      status: 202,
      contentType: 'application/json',
      body: JSON.stringify({ id: live.id }),
    });
  });
  await page.route(`**/api/v1/research/${live.id}/stop`, async (route) => {
    stops++;
    await emit(page, { ...live, state: 'completed', active: false, rawDifferencePercent: null });
    await route.fulfill({
      status: 202,
      contentType: 'application/json',
      body: JSON.stringify({ id: live.id }),
    });
  });
  await openResearch(page);
  await expect(page.getByRole('button', { name: 'Начать запись' })).toBeEnabled();
  await page.getByRole('button', { name: 'Начать запись' }).click();
  await expect(page.getByText('Идёт запись', { exact: true })).toBeVisible();
  await expect(page.getByTestId('research-difference')).toHaveText('+0.1234%');
  await expect(page.getByRole('button', { name: 'Начать запись' })).toBeDisabled();
  await page.getByRole('button', { name: 'Обзор', exact: true }).click();
  await page.getByRole('button', { name: 'Исследование рынка', exact: true }).click();
  await expect(page.getByText('Идёт запись', { exact: true })).toBeVisible();
  expect(starts).toBe(1);
  await page.evaluate(() => {
    (window as unknown as { researchSilence: boolean }).researchSilence = true;
  });
  await expect(page.getByText('Связь с сервером потеряна', { exact: true })).toBeVisible({
    timeout: 6000,
  });
  await expect(page.getByTestId('research-difference')).toHaveText('—');
  await expect(page.getByRole('button', { name: 'Остановить', exact: true })).toBeDisabled();
  await page.evaluate(() => {
    (window as unknown as { researchSilence: boolean }).researchSilence = false;
  });
  await expect(page.getByRole('button', { name: 'Остановить', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Остановить', exact: true }).click();
  await expect(page.getByText('Запись завершена', { exact: true })).toBeVisible();
  expect(stops).toBe(1);
});

test('finished BBO ages stay frozen across updates and navigation; a new recording resumes the clock', async ({
  page,
}) => {
  const completed = {
    ...live,
    state: 'completed',
    active: false,
    rawDifferencePercent: null,
    feeds: live.feeds.map((f) => ({
      ...f,
      status: 'stopped',
      quote: f.quote ? { ...f.quote, ageMs: 123, stale: true } : null,
    })),
  };
  await installFeed(page, live);
  await openResearch(page);
  await expect(page.getByText('Идёт запись', { exact: true })).toBeVisible();
  await page.clock.install();
  await emit(page, completed);
  await page.clock.runFor(200);
  const ages = page
    .locator('.research-quote-footer span')
    .filter({ hasText: 'Возраст BBO при остановке' });
  await expect(ages).toHaveCount(2);
  await expect(ages.first()).toHaveText('Возраст BBO при остановке: 123 мс');
  await page.clock.runFor(2000);
  await expect(ages.first()).toHaveText('Возраст BBO при остановке: 123 мс');
  await expect(page.getByRole('button', { name: 'Начать запись' })).toBeEnabled();
  await page.getByRole('button', { name: 'Обзор', exact: true }).click();
  await page.getByRole('button', { name: 'Исследование рынка', exact: true }).click();
  await page.clock.runFor(200);
  await expect(ages.first()).toHaveText('Возраст BBO при остановке: 123 мс');
  await emit(page, { ...live, id: 'new-session' });
  await page.clock.runFor(200);
  await page.evaluate(() => {
    (window as unknown as { researchSilence: boolean }).researchSilence = true;
  });
  // Allow the 500 ms display timer to pass a full second after the last snapshot.
  await page.clock.runFor(1700);
  const runningAge = page
    .locator('.research-quote-footer span')
    .filter({ hasText: 'Возраст BBO:' })
    .first();
  expect(
    Number((await runningAge.locator('strong').innerText()).replace(/\D/g, '')),
  ).toBeGreaterThan(1000);
  await expect(page.getByTestId('research-difference')).toHaveText('+0.1234%');
});

test('rejected start remains idle and shows error; stale quotes never show a gap as a signal', async ({
  page,
}) => {
  await installFeed(page, idle);
  await page.route('**/api/v1/research', (route) =>
    route.fulfill({
      status: 400,
      contentType: 'application/problem+json',
      body: JSON.stringify({ title: 'Нет активного инструмента на обеих площадках.' }),
    }),
  );
  await openResearch(page);
  await page.getByRole('button', { name: 'Начать запись' }).click();
  await expect(page.getByRole('alert')).toContainText('Нет активного инструмента');
  await expect(page.getByText('Запись не запущена', { exact: true })).toBeVisible();
  await emit(page, {
    ...live,
    feeds: live.feeds.map((f) => ({
      ...f,
      quote: f.quote ? { ...f.quote, stale: true, ageMs: 5000 } : null,
    })),
  });
  await expect(page.getByText('Последняя котировка', { exact: true })).toHaveCount(2);
  await expect(page.getByTestId('research-difference')).toHaveText('—');
});

for (const width of [375, 768, 1440]) {
  test(`research is readable and keyboard accessible at ${width}px`, async ({ page }) => {
    const errors: string[] = [];
    page.on('pageerror', (error) => errors.push(error.message));
    await page.setViewportSize({ width, height: 1100 });
    await installFeed(page, live);
    await openResearch(page);
    await expect(page.getByText('Идёт запись', { exact: true })).toBeVisible();
    await page.evaluate(() => document.fonts.ready);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
      true,
    );
    await page.screenshot({ path: `artifacts/research-${width}.png`, fullPage: true });
    expect(
      (
        await new AxeBuilder({ page })
          .withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'])
          .analyze()
      ).violations,
    ).toEqual([]);
    await emit(page, idle);
    await expect(page.getByLabel('Торговая пара')).toBeEnabled();
    await page.getByLabel('Торговая пара').focus();
    await expect(page.getByRole('listbox', { name: 'Доступные торговые пары' })).toBeVisible();
    expect(
      (
        await new AxeBuilder({ page })
          .withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'])
          .analyze()
      ).violations,
    ).toEqual([]);
    await page.screenshot({ path: `artifacts/research-search-${width}.png`, fullPage: true });
    await page.keyboard.press('Tab');
    await expect(page.getByLabel('Длительность, сек')).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(page.getByLabel('Лимит файла, МБ')).toBeFocused();
    expect(errors).toEqual([]);
  });
}

test('exchange and pair dropdowns drive the recording request and preserve session selection', async ({
  page,
}) => {
  await installFeed(page, idle);
  await openResearch(page);
  await expect(page.getByLabel('Торговая пара')).toBeEnabled();
  await page.getByLabel('Биржа сравнения').selectOption('okx');
  await expect(page.getByLabel('Торговая пара')).toBeEnabled();
  await page.getByLabel('Торговая пара').fill('sol-usdt');
  await page.getByRole('option', { name: /SOL.*SOL-USDT-SWAP/ }).click();
  await page.route('**/api/v1/research', async (route) => {
    expect(route.request().postDataJSON()).toEqual({
      exchange: 'okx',
      sourceExchange: 'hyperliquid',
      coin: 'SOL',
      seconds: 60,
      maxMb: 256,
    });
    await emit(page, {
      ...live,
      exchange: 'okx',
      coin: 'SOL',
      symbol: 'SOL-USDT-SWAP',
      feeds: live.feeds.map((f) => ({ ...f, id: f.id.replace('binance', 'okx') })),
    });
    await route.fulfill({ status: 202, json: { id: live.id } });
  });
  await page.getByRole('button', { name: 'Начать запись' }).click();
  await expect(page.getByLabel('Биржа сравнения')).toBeDisabled();
  await expect(page.getByRole('heading', { name: 'OKX', exact: true })).toBeVisible();
  await expect(page.getByText('Объём: 100 контрактов')).toBeVisible();
  await page.getByRole('button', { name: 'Обзор', exact: true }).click();
  await page.getByRole('button', { name: 'Исследование рынка', exact: true }).click();
  await expect(page.getByLabel('Биржа сравнения')).toHaveValue('okx');
  await expect(page.getByLabel('Торговая пара')).toHaveValue(/SOL/);
});

test('catalog failure blocks starting and retry restores available pairs', async ({ page }) => {
  await installFeed(page, idle);
  let fails = true;
  await page.route('**/api/v1/research/markets?*', (route) =>
    route.fulfill(
      fails
        ? { status: 503, json: { title: 'Не удалось загрузить инструменты с бирж.' } }
        : {
            json: {
              exchange: 'binance',
              updatedUtc: '2026-10-10T10:00:00Z',
              instruments: [{ coin: 'SOL', symbol: 'SOLUSDT' }],
            },
          },
    ),
  );
  await openResearch(page);
  await expect(page.getByRole('alert')).toContainText('Не удалось загрузить');
  await expect(page.getByRole('button', { name: 'Начать запись' })).toBeDisabled();
  fails = false;
  await page.getByRole('button', { name: 'Обновить список пар' }).click();
  await expect(page.getByLabel('Торговая пара')).toHaveValue(/SOL/);
  await expect(page.getByRole('button', { name: 'Начать запись' })).toBeEnabled();
});

test('source selection filters the common list, search is keyboard accessible and CEX pair survives navigation', async ({
  page,
}) => {
  await installFeed(page, idle);
  await page.route('**/api/v1/research/markets?*', (route) => {
    const query = new URL(route.request().url()).searchParams;
    const exchange = query.get('exchange')!;
    const sourceExchange = query.get('sourceExchange')!;
    expect(sourceExchange).not.toBe(exchange);
    const coins = sourceExchange === 'bybit' && exchange === 'okx' ? ['SOL'] : ['ARB', 'SOL'];
    return route.fulfill({
      json: {
        exchange,
        sourceExchange,
        updatedUtc: '2026-10-10T10:00:00Z',
        instruments: coins.map((coin) => ({
          coin,
          symbol: exchange === 'okx' ? `${coin}-USDT-SWAP` : `${coin}USDT`,
        })),
      },
    });
  });
  await openResearch(page);
  await page.getByLabel('Источник сигнала', { exact: true }).selectOption('bybit');
  await page.getByLabel('Биржа сравнения').selectOption('okx');
  const pair = page.getByLabel('Торговая пара');
  await expect(pair).toHaveValue(/SOL/);
  await pair.fill('ARB');
  await expect(page.getByText('Совпадений нет. Попробуйте другой тикер.')).toBeVisible();
  await expect(page.getByRole('listbox').getByRole('option')).toHaveCount(0);
  await pair.fill('sol/usdt');
  await expect(page.getByRole('listbox').getByRole('option')).toHaveCount(1);
  await pair.press('ArrowDown');
  await pair.press('Enter');
  await expect(pair).toHaveValue('SOL · SOLUSDT ↔ SOL-USDT-SWAP');
  await page.route('**/api/v1/research', async (route) => {
    expect(route.request().postDataJSON()).toEqual({
      coin: 'SOL',
      sourceExchange: 'bybit',
      exchange: 'okx',
      seconds: 60,
      maxMb: 256,
    });
    await emit(page, {
      ...live,
      sourceExchange: 'bybit',
      exchange: 'okx',
      coin: 'SOL',
      symbol: 'SOL-USDT-SWAP',
      feeds: ['bybit-public', 'bybit-market', 'okx-public', 'okx-market'].map((id) => ({
        ...live.feeds[0],
        id,
        quote: id.endsWith('public') ? quote : null,
      })),
    });
    await route.fulfill({ status: 202, json: { id: live.id } });
  });
  await page.getByRole('button', { name: 'Начать запись' }).click();
  await expect(page.getByRole('heading', { name: 'Bybit', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Hyperliquid', exact: true })).toHaveCount(0);
  await expect(page.getByText('(Mid Bybit / Mid OKX − 1) × 100%')).toBeVisible();
  await expect(page.getByLabel('Источник сигнала', { exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Обзор', exact: true }).click();
  await page.getByRole('button', { name: 'Исследование рынка', exact: true }).click();
  await expect(page.getByLabel('Источник сигнала', { exact: true })).toHaveValue('bybit');
  await expect(page.getByLabel('Биржа сравнения')).toHaveValue('okx');
});

test('choosing the other exchange swaps the pair and Escape preserves the selected instrument', async ({
  page,
}) => {
  await installFeed(page, idle);
  await openResearch(page);
  await page.getByLabel('Источник сигнала', { exact: true }).selectOption('binance');
  await expect(page.getByLabel('Биржа сравнения')).toHaveValue('hyperliquid');
  const pair = page.getByLabel('Торговая пара');
  await expect(pair).toHaveValue('ARB · ARBUSDT ↔ ARB');
  await pair.fill('does-not-exist');
  await pair.press('Escape');
  await expect(pair).toHaveValue('ARB · ARBUSDT ↔ ARB');
  await expect(page.getByRole('listbox')).toHaveCount(0);
});

const analysisRecording = {
  id: '20261010T100000Z-11111111111111111111111111111111',
  coin: 'SOL',
  sourceExchange: 'hyperliquid',
  exchange: 'binance',
  startedUtc: '2026-10-10T10:00:00Z',
  status: 'completed',
  hasReport: true,
};
const analysisReport = {
  recording: analysisRecording,
  settings: {
    impulseBps: 3,
    windowMs: 250,
    entryDelayMs: 100,
    holdMs: 1000,
    maxAgeMs: 1000,
    feeBps: 5,
    slippageBps: 1,
  },
  durationSeconds: 60,
  events: 1000,
  gaps: 0,
  invalidQuotes: 0,
  outOfOrderQuotes: 0,
  coveragePercent: 99,
  feeds: [
    { exchange: 'hyperliquid', quotes: 500, averageSpreadBps: 1 },
    { exchange: 'binance', quotes: 500, averageSpreadBps: 1 },
  ],
  directions: [
    {
      sourceExchange: 'hyperliquid',
      exchange: 'binance',
      impulses: 3,
      horizons: [{ horizonMs: 100, samples: 3, sameDirectionPercent: 100, meanMoveBps: 2 }],
      model: {
        trades: 3,
        excluded: 0,
        winPercent: 0,
        meanGrossBps: 2,
        meanNetBps: -8,
        meanFeeBps: 10,
      },
    },
  ],
  notes: [
    'Запись короче 10 минут: подходит для проверки подключения, но не для вывода о стратегии.',
  ],
};

test('saved analysis reloads, recalculates chosen costs, and remains accessible on mobile', async ({
  page,
}) => {
  await installFeed(page, idle);
  await page.route('**/api/v1/research/recordings', (route) =>
    route.fulfill({ json: { items: [analysisRecording], unavailable: 0 } }),
  );
  let fee = 5;
  await page.route('**/api/v1/research/recordings/*/analysis', (route) => {
    if (route.request().method() === 'POST') fee = route.request().postDataJSON().feeBps;
    return route.fulfill({
      json: { ...analysisReport, settings: { ...analysisReport.settings, feeBps: fee } },
    });
  });
  await openResearch(page);
  await expect(page.getByRole('heading', { name: 'Отчёт: SOL' })).toBeVisible();
  await page.getByText('Параметры расчёта', { exact: true }).click();
  await page.getByLabel('Комиссия за сторону, б.п.', { exact: true }).fill('2');
  await page.getByRole('button', { name: 'Проанализировать запись' }).click();
  await expect(page.getByText(/Параметры этого отчёта:/)).toContainText('комиссия 2');
  await page.reload();
  await page.getByRole('button', { name: 'Исследование рынка', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Отчёт: SOL' })).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(
    (await new AxeBuilder({ page }).include('.research-analysis').analyze()).violations,
  ).toEqual([]);
});

test('analysis shows a server error without fabricated report', async ({ page }) => {
  await installFeed(page, idle);
  await page.route('**/api/v1/research/recordings', (route) =>
    route.fulfill({
      json: { items: [{ ...analysisRecording, hasReport: false }], unavailable: 0 },
    }),
  );
  await page.route('**/api/v1/research/recordings/*/analysis', (route) =>
    route.fulfill({ status: 422, json: { title: 'Повреждённая строка 2. Анализ прерван.' } }),
  );
  await openResearch(page);
  await page.getByRole('button', { name: 'Проанализировать запись' }).click();
  await expect(page.getByRole('alert')).toContainText('Повреждённая строка 2');
  await expect(page.getByRole('heading', { name: 'Отчёт: SOL' })).toHaveCount(0);
});

const replayFixture = {
  status: 'evaluated',
  explanation: 'Поводырь выбран только на начальном участке.',
  version: '0.1.0',
  leader: 'binance',
  executionExchange: 'okx',
  normalBasisBps: 1,
  durationSeconds: 600,
  settings: {
    calibrationSeconds: 120,
    impulseBps: 3,
    windowMs: 250,
    maxAgeMs: 500,
    feeBps: 5,
    slippageBps: 1,
    minEdgeBps: 2,
    stopLossBps: 20,
    maxHoldMs: 2000,
    leaderAdvantageBps: 0.5,
  },
  candidates: [
    { sourceExchange: 'binance', exchange: 'okx', samples: 15, responseBps: 2 },
    { sourceExchange: 'okx', exchange: 'binance', samples: 12, responseBps: 0.2 },
  ],
  scenarios: [10, 25, 50, 100].flatMap((delayMs) =>
    ['follow', 'counter'].map((mode) => ({
      mode,
      delayMs,
      trades: 1,
      meanNetBps: -2,
      winPercent: 0,
      sumNetBps: -2,
      closedTradeDrawdownBps: 2,
      unresolvedPositions: 0,
      skipped: 2,
      signals: 3,
      journalTruncated: false,
      journal: [
        {
          seconds: 121,
          action: 'пропуск',
          reason: 'Оставшееся отставание меньше порога расходов и запаса.',
          side: null,
          price: null,
          netBps: null,
        },
      ],
      tradeLog: [
        {
          entrySeconds: 125,
          exitSeconds: 126,
          side: 'long',
          entryPrice: 100,
          exitPrice: 100.01,
          netBps: -2,
          exitReason: 'Лимит времени удержания',
        },
      ],
    })),
  ),
  notes: ['Симуляция не отправляет реальные заявки.'],
};

test('strategy replay compares eight scenarios, shows journals and restores a saved result', async ({
  page,
}) => {
  await installFeed(page, idle);
  await page.route('**/api/v1/research/recordings', (route) =>
    route.fulfill({
      json: { items: [{ ...analysisRecording, hasReport: false }], unavailable: 0 },
    }),
  );
  let saved = false;
  await page.route('**/api/v1/research/recordings/*/replay', (route) => {
    if (route.request().method() === 'POST') {
      expect(route.request().postDataJSON().calibrationSeconds).toBe(120);
      saved = true;
    }
    return saved
      ? route.fulfill({ json: replayFixture })
      : route.fulfill({ status: 404, json: { title: 'Нет отчёта' } });
  });
  await openResearch(page);
  await page.getByRole('button', { name: 'Проверить первую стратегию' }).click();
  await expect(page.getByRole('heading', { name: 'Результат стратегии v0.1.0' })).toBeVisible();
  await expect(
    page.getByRole('table', { name: 'Сравнение независимых сценариев' }).getByRole('row'),
  ).toHaveCount(9);
  await page.getByLabel('Журнал сценария').selectOption('counter-10');
  await page.getByText('Причины входов, пропусков и выходов', { exact: true }).click();
  await expect(
    page.getByText('Оставшееся отставание меньше порога расходов и запаса.', { exact: true }),
  ).toBeVisible();
  await page.getByText('Условные сделки', { exact: true }).click();
  await expect(page.getByText('Лимит времени удержания', { exact: true })).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect((await new AxeBuilder({ page }).include('.strategy-replay').analyze()).violations).toEqual(
    [],
  );
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.reload();
  await page.getByRole('button', { name: 'Исследование рынка', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Результат стратегии v0.1.0' })).toBeVisible();
});

test('strategy without a leader explains why no trades were simulated', async ({ page }) => {
  await installFeed(page, idle);
  await page.route('**/api/v1/research/recordings', (route) =>
    route.fulfill({
      json: { items: [{ ...analysisRecording, hasReport: false }], unavailable: 0 },
    }),
  );
  await page.route('**/api/v1/research/recordings/*/replay', (route) =>
    route.fulfill({
      json: {
        ...replayFixture,
        status: 'no-leader',
        leader: null,
        executionExchange: null,
        scenarios: [],
        explanation: 'Поводырь не определён. Сделки не моделировались.',
      },
    }),
  );
  await openResearch(page);
  await expect(
    page.getByText('Поводырь не определён. Сделки не моделировались.', { exact: true }),
  ).toBeVisible();
  await expect(page.getByRole('table', { name: 'Сравнение независимых сценариев' })).toHaveCount(0);
});

test('depth replay submits size settings and exposes partial fills and remaining position', async ({
  page,
}) => {
  await installFeed(page, idle);
  await page.route('**/api/v1/research/recordings', (route) =>
    route.fulfill({ json: { items: [analysisRecording], unavailable: 0 } }),
  );
  await page.route('**/api/v1/research/recordings/*/replay', (route) => {
    if (route.request().method() !== 'POST') return route.fulfill({ status: 404 });
    const body = route.request().postDataJSON();
    expect(body.executionModel).toBe('depth');
    expect(body.orderNotional).toBe(25);
    expect(body.priceLimitBps).toBe(3);
    return route.fulfill({
      json: {
        ...replayFixture,
        version: '0.2.0',
        settings: body,
        instrument: {
          quantityStep: 0.1,
          minimumQuantity: 0.1,
          minimumNotional: 5,
          maximumQuantity: 1000,
          quoteCurrency: 'USDT',
          ruleSource: 'Метаданные записи',
        },
        scenarios: replayFixture.scenarios.map((s) => ({
          ...s,
          partialEntries: 1,
          partialExits: 1,
          unresolvedQuantity: 0.2,
          unresolvedPositions: 1,
          closedNetQuote: 0,
          trades: 0,
          tradeLog: [],
          journal: [
            {
              seconds: 125,
              action: 'частичный выход',
              reason: 'Недостаточно объёма до предела цены',
              quantity: 0.1,
            },
          ],
        })),
      },
    });
  });
  await openResearch(page);
  await page.getByText('Настройки стратегии', { exact: true }).click();
  await expect(page.getByLabel('Модель исполнения')).toHaveValue('depth');
  await page.getByLabel('Модель исполнения').selectOption('bbo');
  await expect(page.getByLabel('Номинал заявки в валюте расчёта')).toBeDisabled();
  await page.getByLabel('Модель исполнения').selectOption('depth');
  await page.getByLabel('Номинал заявки в валюте расчёта').fill('25');
  await page.getByLabel('Предел отклонения цены заявки, б.п.').fill('3');
  await page.getByRole('button', { name: 'Проверить первую стратегию' }).click();
  await expect(page.getByRole('heading', { name: 'Результат стратегии v0.2.0' })).toBeVisible();
  await expect(page.getByText(/Незакрытый остаток:/)).toContainText('0,2 монет');
  await expect(page.getByText(/Частичных входов:/)).toContainText('Частичных выходов: 1');
  await page.getByText('Причины входов, пропусков и выходов', { exact: true }).click();
  await expect(page.getByText('Недостаточно объёма до предела цены')).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect((await new AxeBuilder({ page }).include('.strategy-replay').analyze()).violations).toEqual(
    [],
  );
});

test('maker entry submits its settings and shows how many resting orders filled', async ({
  page,
}) => {
  await installFeed(page, idle);
  await page.route('**/api/v1/research/recordings', (route) =>
    route.fulfill({ json: { items: [analysisRecording], unavailable: 0 } }),
  );
  await page.route('**/api/v1/research/recordings/*/replay', (route) => {
    if (route.request().method() !== 'POST') return route.fulfill({ status: 404 });
    const body = route.request().postDataJSON();
    expect(body.entryOrder).toBe('maker');
    expect(body.makerFeeBps).toBe(1.5);
    return route.fulfill({
      json: {
        ...replayFixture,
        version: '0.3.0',
        settings: body,
        scenarios: replayFixture.scenarios.map((s) => ({ ...s, makerOrders: 7, makerFills: 2 })),
      },
    });
  });
  await openResearch(page);
  await page.getByText('Настройки стратегии', { exact: true }).click();
  await expect(page.getByLabel('Комиссия maker за вход, б.п.')).toBeDisabled();
  await page.getByLabel('Вход в сделку').selectOption('maker');
  await page.getByLabel('Комиссия maker за вход, б.п.').fill('1.5');
  await page.getByRole('button', { name: 'Проверить первую стратегию' }).click();
  await expect(page.getByRole('heading', { name: 'Результат стратегии v0.3.0' })).toBeVisible();
  const table = page.getByRole('table', { name: 'Сравнение независимых сценариев' });
  await expect(table.getByRole('columnheader', { name: 'Исполнено maker-заявок' })).toBeVisible();
  await expect(table.getByRole('cell', { name: '2 из 7' }).first()).toBeVisible();
  expect((await new AxeBuilder({ page }).include('.strategy-replay').analyze()).violations).toEqual(
    [],
  );
});

test('series check sends the chosen recordings with current settings and shows combined totals', async ({
  page,
}) => {
  await installFeed(page, idle);
  const second = {
    ...analysisRecording,
    id: '20261011T100000Z-22222222222222222222222222222222',
    coin: 'MAGIC',
  };
  await page.route('**/api/v1/research/recordings', (route) =>
    route.fulfill({
      json: {
        items: [
          { ...analysisRecording, hasReport: false },
          { ...second, hasReport: false },
        ],
        unavailable: 0,
      },
    }),
  );
  await page.route('**/api/v1/research/recordings/*/replay', (route) =>
    route.fulfill({ status: 404 }),
  );
  await page.route('**/api/v1/research/series/replay', (route) => {
    if (route.request().method() !== 'POST') return route.fulfill({ status: 404 });
    const body = route.request().postDataJSON();
    expect(body.ids).toEqual([second.id]);
    expect(body.settings.entryOrder).toBe('maker');
    return route.fulfill({
      json: {
        version: '0.3.2',
        createdUtc: '2026-10-11T12:00:00Z',
        settings: body.settings,
        recordings: [
          {
            ...second,
            status: 'evaluated',
            explanation: 'Поводырь выбран.',
            leader: 'binance',
            executionExchange: 'okx',
            scenarios: [],
          },
        ],
        totals: [
          {
            mode: 'follow',
            delayMs: 10,
            recordings: 3,
            recordingsPositive: 2,
            ordersSent: 40,
            entriesFilled: 12,
            ordersUnfilled: 25,
            trades: 11,
            unresolved: 3,
            sumNetBps: -4.5,
            meanNetBps: -0.4,
            winPercent: 45.5,
            drawdownBps: 9,
            closedNetQuote: -0.01,
            drawdownFromTruncatedLog: false,
          },
        ],
        notes: ['Итоги складывают только проверенные записи.'],
      },
    });
  });
  await openResearch(page);
  await page.getByText('Настройки стратегии', { exact: true }).click();
  await page.getByLabel('Вход в сделку').selectOption('maker');
  const series = page.locator('.strategy-series');
  await series.getByRole('checkbox', { name: /^SOL/ }).uncheck();
  await series.getByRole('button', { name: 'Проверить на выбранных записях (1)' }).click();
  const totals = page.getByRole('table', {
    name: 'Итоги по сценариям на всех проверенных записях',
  });
  await expect(totals.getByRole('cell', { name: '2 из 3' })).toBeVisible();
  await expect(page.getByRole('table', { name: 'Записи серии' })).toContainText('binance → okx');
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect((await new AxeBuilder({ page }).include('.strategy-series').analyze()).violations).toEqual(
    [],
  );
});

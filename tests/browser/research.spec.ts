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

test('demo research cannot launch real recording', async ({ page }) => {
  let requests = 0;
  page.on('request', (request) => {
    if (request.url().includes('/api/v1/research')) requests++;
  });
  await page.goto('/?demo=1');
  await page.getByRole('button', { name: 'Исследование рынка', exact: true }).click();
  await expect(
    page.getByRole('heading', { name: 'Исследование работает на сервере' }),
  ).toBeVisible();
  await expect(page.getByRole('button', { name: 'Начать запись' })).toHaveCount(0);
  expect(requests).toBe(0);
});

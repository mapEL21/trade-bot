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
    expect(route.request().postDataJSON()).toEqual({ coin: 'ARB', seconds: 60, maxMb: 256 });
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
    await expect(page.getByLabel('Тикер альткоина')).toBeEnabled();
    await page.getByLabel('Тикер альткоина').focus();
    await page.keyboard.press('Tab');
    await expect(page.getByLabel('Длительность, сек')).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(page.getByLabel('Лимит файла, МБ')).toBeFocused();
    expect(errors).toEqual([]);
  });
}

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

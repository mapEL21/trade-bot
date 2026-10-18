import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { defaultSignalParameters, exportConfig, type Bot, type Trade } from '../../src/model';

const hour = 3600000;
// Closed simulated trades as the server returns them inside a bot: two within 24 hours, five within 7 days.
// Each keeps the coin it was made on: a signal bot trades every coin of the recording.
const trades: Trade[] = [1, 5, 30, 60, 100].map((hours, i) => ({
  id: `run-1-${i}`,
  at: new Date(Date.now() - hours * hour).toISOString(),
  openedAt: new Date(Date.now() - hours * hour - 274).toISOString(),
  holdSeconds: 0.274,
  symbol: 'STRKUSDT',
  exchange: 'Binance',
  notional: 10.2,
  exitReason: i === 4 ? 'стоп' : 'время',
  maxFavorableBps: 130,
  maxAdverseBps: -5,
  side: i % 2 ? 'short' : 'long',
  gross: i % 3 ? 0.05 : -0.02,
  fee: 0.01,
  entry: 0.4,
  exit: 0.405,
  quantity: 25,
  runId: 'run-1',
  simulated: true,
}));
const catalog = [
  {
    id: 'signal',
    version: '0.2.0',
    name: 'Сигнал лаборатории',
    description: 'Торгует сигнал лаборатории.',
    implementationStatus: 'paper',
    canCreate: true,
    canRun: true,
    exchanges: ['Binance'],
  },
];

// An in-memory stand-in for the local API, enough for the workspace screens.
async function server(page: Page) {
  const bots: (Bot & { revision: number })[] = [
    {
      id: '11111111-1111-1111-1111-111111111111',
      name: 'Вынос цены · 5 мин',
      exchange: 'Binance',
      symbol: '*',
      budget: 20,
      lossLimit: 2,
      strategy: 'signal',
      strategyVersion: '0.2.0',
      signal: defaultSignalParameters,
      createdAt: '2026-10-01T10:00:00Z',
      revision: 1,
      status: 'stopped',
      trades,
    },
  ];
  const events: { id: string; at: string; botId: string; botName: string; text: string }[] = [];
  const log = (bot: Bot, text: string) =>
    events.unshift({
      id: crypto.randomUUID(),
      at: new Date().toISOString(),
      botId: bot.id,
      botName: bot.name,
      text,
    });
  await page.route('**/api/v1/strategies', (route) => route.fulfill({ json: catalog }));
  await page.route('**/api/v1/workspace', (route) =>
    route.fulfill({ json: { schemaVersion: 1, bots, events } }),
  );
  await page.route('**/api/v1/bots', (route) => {
    const bot = {
      ...route.request().postDataJSON(),
      id: crypto.randomUUID(),
      createdAt: new Date().toISOString(),
      revision: 1,
      status: 'draft',
      trades: [],
    };
    bots.push(bot);
    log(bot, 'Бот создан.');
    return route.fulfill({ status: 201, json: bot });
  });
  await page.route('**/api/v1/bots/*', (route) => {
    const id = route.request().url().split('/').pop()!;
    const index = bots.findIndex((b) => b.id === id);
    if (route.request().method() === 'DELETE') {
      bots.splice(index, 1);
      return route.fulfill({ status: 204 });
    }
    bots[index] = {
      ...bots[index],
      ...route.request().postDataJSON(),
      revision: bots[index].revision + 1,
    };
    log(bots[index], 'Настройки бота обновлены.');
    return route.fulfill({ json: bots[index] });
  });
  await page.route('**/api/v1/bots/*/signal', (route) => route.fulfill({ json: [] }));
  return bots;
}

test('overview, trade periods and bot navigation work without console errors', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await server(page);
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Обзор', level: 1 })).toBeVisible();
  // One quiet mark instead of a banner on every page: nothing is traded for real.
  await expect(page.getByText('СИМУЛЯЦИЯ', { exact: true })).toBeVisible();
  // The result chart: with fees (solid) and without (dashed); line or bars, the choice kept in the browser.
  const chart = page.locator('.performance');
  await expect(chart).toContainText('без комиссий');
  await expect(chart.locator('.chart-legend')).toContainText('С комиссиями');
  await expect(chart.locator('.chart-legend')).toContainText('Без комиссий');
  await expect(chart.locator('path.chart-gross')).toHaveCount(1);
  await expect(chart.getByRole('img', { name: /Кривая накопленного результата/ })).toBeVisible();
  await chart.getByRole('button', { name: 'Столбцы', exact: true }).click();
  await expect(chart.getByRole('img', { name: /Результат по сделкам/ })).toBeVisible();
  await expect(chart.locator('rect.chart-gross')).toHaveCount(5);
  await page.screenshot({ path: 'artifacts/overview-desktop.png', fullPage: true });
  await page.reload();
  await expect(
    page.locator('.performance').getByRole('img', { name: /Результат по сделкам/ }),
  ).toBeVisible();
  await page.locator('.performance').getByRole('button', { name: 'Линия', exact: true }).click();
  await page.getByRole('button', { name: 'Статистика' }).click();
  await expect(page.getByRole('heading', { name: 'Вынос цены · 5 мин' })).toBeVisible();
  // The common trade quality block groups exit reasons; a retried exit counts under its original reason.
  const quality = page.locator('.quality-panel');
  await expect(quality.getByRole('heading', { name: 'Качество сделок' })).toBeVisible();
  await expect(quality.locator('tbody tr')).toHaveCount(2);
  await expect(quality.locator('tbody tr').first()).toContainText('время');
  await expect(quality.locator('tbody tr').nth(1)).toContainText('стоп');
  await expect(quality).toContainText('выводы предварительные');
  await expect(page.locator('.bot-facts')).toContainText('Плотность · v0.2.0');
  await expect(page.locator('.bot-facts')).toContainText('1,5% депозита с комиссиями');
  await page.screenshot({ path: 'artifacts/bot-overview-desktop.png', fullPage: true });
  await page.locator('.detail-tabs').getByRole('button', { name: 'Сделки', exact: true }).click();
  await expect(page.locator('.trades-panel tbody tr')).toHaveCount(5);
  await page.getByRole('button', { name: '24 часа', exact: true }).click();
  await expect(page.locator('.trades-panel tbody tr')).toHaveCount(2);
  await expect(page.locator('.trades-panel')).toContainText('SIM');
  const row = page.locator('.trades-panel tbody tr').first();
  for (const text of ['STRKUSDT', 'Binance', '0,27 с', '10,20', 'время'])
    await expect(row).toContainText(text);
  // Small moves on cheap coins stay visible: 0.4 → 0.405 is +1.25%.
  await expect(row).toContainText('+1,25%');
  await page.getByRole('button', { name: 'Настройки', exact: true }).click();
  await page.getByLabel('Название бота').fill('Мой бот');
  await page.getByRole('button', { name: 'Сохранить изменения' }).click();
  await expect(page.getByRole('heading', { name: 'Мой бот' })).toBeVisible();
  await page.locator('.detail-tabs').getByRole('button', { name: 'Журнал', exact: true }).click();
  await expect(page.getByText('Настройки бота обновлены.')).toBeVisible();
  // The workspace-wide journal page is gone from the menu.
  await expect(page.getByRole('navigation').getByRole('button', { name: /Журнал/ })).toHaveCount(0);
  expect(errors).toEqual([]);
});

for (const width of [375, 768, 1440]) {
  test(`responsive overview and accessible forms at ${width}px`, async ({ page }) => {
    const failures: string[] = [];
    page.on('response', (response) => {
      if (response.status() >= 400) failures.push(`${response.status()} ${response.url()}`);
    });
    page.on('pageerror', (error) => failures.push(error.message));
    await server(page);
    await page.setViewportSize({ width, height: 1000 });
    await page.goto('/');
    await expect(page.getByText('Данные загружены с сервера')).toBeVisible();
    await page.evaluate(() => document.fonts.ready);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
      true,
    );
    await page.screenshot({ path: `artifacts/overview-${width}.png`, fullPage: true });
    const audit = () =>
      new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
    const violations = async () =>
      (await audit()).violations.map((v) => ({
        id: v.id,
        nodes: v.nodes.map((n) => ({ target: n.target, reason: n.failureSummary })),
      }));
    expect(await violations()).toEqual([]);
    const create = page.getByRole('button', { name: 'Создать бота', exact: true }).first();
    await create.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByLabel('Название бота')).toBeFocused();
    expect(await violations()).toEqual([]);
    await page.keyboard.press('Escape');
    await expect(create).toBeFocused();
    expect(failures).toEqual([]);
  });
}

test('create, export, import and delete a bot on the server', async ({ page }) => {
  const bots = await server(page);
  await page.goto('/');
  await page.getByRole('button', { name: 'Создать бота', exact: true }).first().click();
  await page.getByLabel('Название бота').fill('Тестовый бот');
  await page.getByRole('dialog').getByRole('button', { name: 'Создать бота', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Тестовый бот' })).toBeVisible();
  await expect(page.getByText('Не запускался', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Здесь появится кривая результата')).toBeVisible();
  expect(bots.some((b) => b.name === 'Тестовый бот' && b.strategy === 'signal')).toBe(true);
  await page.getByRole('button', { name: 'Настройки', exact: true }).click();
  const downloadPromise = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Экспорт', exact: true }).click();
  expect((await downloadPromise).suggestedFilename()).toBe('signal-bot.json');
  await page.getByRole('button', { name: 'Все боты', exact: true }).click();
  await page.getByRole('button', { name: 'Импорт бота', exact: true }).click();
  await page
    .getByLabel('Или вставьте содержимое JSON')
    .fill(exportConfig({ ...bots[0], name: 'Импортированный бот' }));
  await page.getByRole('button', { name: 'Импортировать', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Импортированный бот' })).toBeVisible();
  const imported = bots.find((b) => b.name === 'Импортированный бот')!;
  expect(imported.signal?.signal).toBe('Плотность');
  await page.getByRole('button', { name: 'Настройки', exact: true }).click();
  await page.getByRole('button', { name: 'Удалить', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Удалить', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Импортированный бот Сигнал лаборатории v0.2.0' }),
  ).toHaveCount(0);
  expect(bots.some((b) => b.name === 'Импортированный бот')).toBe(false);
});

test('where a bot trades is chosen when it is created: on the demo account the deposit is the balance', async ({
  page,
}) => {
  const bots = await server(page);
  await page.goto('/');
  await page.getByRole('button', { name: 'Создать бота', exact: true }).first().click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Название бота').fill('Плотность на демо');
  await dialog.getByLabel('Где торгует').selectOption('testnet');
  await expect(dialog.getByLabel('Депозит бота, USDT')).toHaveCount(0);
  await expect(dialog.getByText('Депозит — баланс счёта в момент запуска')).toBeVisible();
  await dialog.getByLabel('Лимит убытка, % депозита').fill('10');
  // The fee comes from the account's tariff; the size is the strategy's, within the leverage.
  await expect(dialog.getByLabel('Комиссии круга, %')).toHaveCount(0);
  await expect(dialog.getByText('Комиссию бот берёт по тарифу счёта')).toBeVisible();
  // A scroll of the long form (a click on the dialog itself, inside its box) keeps it open.
  const box = (await dialog.boundingBox())!;
  await page.mouse.click(box.x + box.width - 3, box.y + box.height / 2);
  await expect(dialog).toBeVisible();
  // The spike strategy has no live mode: the choice falls back to the simulation.
  await dialog.getByLabel('Стратегия').selectOption('Вынос цены');
  await expect(dialog.getByLabel('Где торгует')).toHaveValue('simulation');
  await dialog.getByLabel('Стратегия').selectOption('Плотность');
  await dialog.getByLabel('Где торгует').selectOption('testnet');
  await dialog.getByRole('button', { name: 'Создать бота', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Плотность на демо' })).toBeVisible();
  const created = bots.find((b) => b.name === 'Плотность на демо')!;
  expect([created.execution, created.budget]).toEqual(['testnet', 100]);
  await expect(page.getByText('демо-счёт Binance', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: /^Боты/ }).first().click();
  const card = page.locator('.bot-card', { hasText: 'Плотность на демо' });
  await expect(card).toContainText('Баланс · демо-счёт');
});

test('invalid imports are rejected before reaching the server', async ({ page }) => {
  const bots = await server(page);
  await page.goto('/');
  await page.getByRole('button', { name: 'Импорт бота', exact: true }).click();
  await page.getByLabel('Или вставьте содержимое JSON').fill('{"schemaVersion":5}');
  await page.getByRole('button', { name: 'Импортировать', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('версии 1');
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(bots).toHaveLength(1);
});

test('mobile navigation and modal fit the viewport', async ({ page }) => {
  await server(page);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: 'artifacts/overview-mobile.png', fullPage: true });
  await page.getByRole('button', { name: 'Открыть меню' }).click();
  await page.getByRole('button', { name: /^Боты/ }).first().click();
  await page.getByRole('button', { name: 'Создать бота', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.screenshot({ path: 'artifacts/create-mobile.png', fullPage: true });
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('the demo space is gone: ?demo=1 opens the server workspace', async ({ page }) => {
  await server(page);
  await page.goto('/?demo=1');
  await expect(page.getByText('Данные загружены с сервера')).toBeVisible();
  await expect(page.getByText(/Демонстрационный режим|демопример/)).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Вынос цены · 5 мин' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Вынос цены · 5 мин/ }).first()).toBeVisible();
});

test('a live run shows the coins the bot watches and the positions it holds', async ({ page }) => {
  const bots = await server(page);
  bots[0].status = 'running';
  await page.route('**/api/v1/bots/*/signal', (route) =>
    route.fulfill({
      json: [
        {
          run: {
            id: 'r',
            botId: bots[0].id,
            fromMs: Date.now() - 3600000,
            toMs: null,
            startedUtc: new Date().toISOString(),
            budget: 20,
            lossLimit: 2,
            parameters: bots[0].signal,
            live: true,
          },
          state: 'running',
          result: {
            trades: [],
            skipped: {},
            signals: 1,
            incomplete: 0,
            deposit: 20,
            recordings: 1,
            lastDataMs: Date.now(),
            entriesBlocked: null,
            pending: 0,
            unrealized: 0.12,
          },
          live: {
            watching: [{ symbol: 'KAVAUSDT', side: 1, wall: 0.068, distancePct: 0.42 }],
            open: [
              {
                symbol: 'KAVAUSDT',
                side: 'long',
                openedMs: Date.now() - 60000,
                entry: 0.0681,
                mark: 0.0684,
                fraction: 5,
                entryReason: 'отскок от плотности 308 тыс. $ на 0,068',
              },
            ],
            segment: '20261018T100000Z',
          },
        },
      ],
    }),
  );
  await page.goto('/');
  await page.getByRole('button', { name: /^Боты/ }).first().click();
  await page
    .getByRole('button', { name: /Вынос цены · 5 мин/ })
    .first()
    .click();
  const panel = page.locator('.signal-run');
  await expect(panel.getByRole('heading', { name: 'Живой режим' })).toBeVisible();
  await expect(panel).toContainText('Следит за монетами: 1');
  await expect(panel).toContainText('KAVAUSDT — плотность на покупку 0.068, цена в 0,42%');
  await expect(panel).toContainText('KAVAUSDT лонг от 0.0681, сейчас 0.0684 — отскок от плотности');
  expect((await new AxeBuilder({ page }).include('.signal-run').analyze()).violations).toEqual([]);
});

test('testnet keys are entered in a panel and a testnet run shows its orders on the exchange', async ({
  page,
}) => {
  const bots = await server(page);
  bots[0].status = 'running';
  bots[0].execution = 'testnet';
  bots[0].deposit = 5010.5;
  let configured = false;
  let saved: { mode: string; apiKey: string; apiSecret: string } | null = null;
  await page.route('**/api/v1/exchange/keys', (route) => {
    if (route.request().method() === 'POST') {
      saved = route.request().postDataJSON();
      configured = true;
      return route.fulfill({ json: { balance: 5000 } });
    }
    return route.fulfill({
      json: {
        testnet: {
          mode: 'testnet',
          configured,
          keyEnd: configured ? 'WXYZ' : null,
          savedUtc: null,
        },
        liveAllowed: false,
      },
    });
  });
  await page.route('**/api/v1/exchange/balance/testnet', (route) =>
    route.fulfill({
      json: { wallet: 5010.5, available: 5000.5, unrealized: -1.25, atMs: Date.now() },
    }),
  );
  await page.route('**/api/v1/bots/*/exchange', (route) =>
    route.fulfill({
      json: [
        {
          runId: 'r',
          atMs: Date.now(),
          symbol: 'KAVAUSDT',
          action: 'защитный стоп',
          side: 'SELL',
          quantity: 0,
          price: 0.0675,
          clientId: 'tb-s',
          status: 'NEW',
          message: 'стоп за уровнем отмены',
        },
      ],
    }),
  );
  await page.route('**/api/v1/bots/*/signal', (route) =>
    route.fulfill({
      json: [
        {
          run: {
            id: 'r',
            botId: bots[0].id,
            fromMs: Date.now() - 3600000,
            toMs: null,
            startedUtc: new Date().toISOString(),
            budget: 20,
            lossLimit: 2,
            parameters: bots[0].signal,
            live: true,
            execution: 'testnet',
          },
          state: 'running',
          result: {
            trades: [],
            skipped: {},
            signals: 0,
            incomplete: 0,
            deposit: 20,
            recordings: 1,
            lastDataMs: Date.now(),
            entriesBlocked: null,
            pending: 0,
            unrealized: 0,
          },
          live: {
            watching: [],
            open: [],
            segment: '20261018T100000Z',
            exchange: [
              {
                symbol: 'KAVAUSDT',
                side: 'long',
                quantity: 1470,
                entry: 0.0681,
                stopPrice: 0.0675,
                openedMs: Date.now(),
              },
            ],
            exchangeProblem: null,
          },
        },
      ],
    }),
  );
  await page.goto('/');
  await page.getByRole('button', { name: /^Боты/ }).first().click();
  await page
    .getByRole('button', { name: /Вынос цены · 5 мин/ })
    .first()
    .click();
  await expect(page.getByText('ТЕСТОВАЯ СЕТЬ', { exact: true })).toBeVisible();
  await expect(page.locator('.mode-pill').first()).toHaveText('ДЕМО-СЧЁТ');
  // Stopping a testnet run closes its positions on the exchange: the button says so and asks first.
  let stopped = false;
  await page.route('**/api/v1/bots/*/signal/stop', (route) => {
    stopped = true;
    return route.fulfill({ status: 202 });
  });
  page.once('dialog', (d) => void d.dismiss());
  await page.getByRole('button', { name: 'Остановить и закрыть всё' }).click();
  expect(stopped).toBe(false);
  await expect(page.getByText('KAVAUSDT лонг 1470 от 0.0681, защитный стоп 0.0675')).toBeVisible();
  await expect(page.getByRole('region', { name: /Заявки на бирже/ })).toContainText(
    'стоп за уровнем отмены',
  );
  const keys = page.locator('.exchange-keys');
  await expect(keys).toContainText('ключей нет');
  await keys.locator('summary').click();
  await keys.getByLabel('API-ключ').fill('test-key-WXYZ');
  await keys.getByLabel('Секрет').fill('test-secret');
  await keys.getByRole('button', { name: 'Сохранить и проверить' }).click();
  await expect(keys.getByRole('status')).toContainText('Баланс тестового счёта: 5');
  await expect(keys).toContainText('ключи сохранены (…WXYZ)');
  // With the keys saved the test account's balance is on the page.
  const balance = page.getByRole('region', { name: 'Тестовая сеть Binance' });
  await expect(balance).toContainText('5 010,50 USDT');
  await expect(balance).toContainText('-1,25 USDT');
  expect(saved).toEqual({ mode: 'testnet', apiKey: 'test-key-WXYZ', apiSecret: 'test-secret' });
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

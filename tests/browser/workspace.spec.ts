import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { defaultLeaderLagSettings, exportConfig, type Bot, type Trade } from '../../src/model';

const hour = 3600000;
// Closed simulated trades as the server returns them inside a bot: two within 24 hours, five within 7 days.
const trades: Trade[] = [1, 5, 30, 60, 100].map((hours, i) => ({
  id: `run-1-${i}`,
  at: new Date(Date.now() - hours * hour).toISOString(),
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
    id: 'impulse',
    version: '0.1.0',
    name: 'Импульс',
    description: 'Шаблон.',
    implementationStatus: 'planned',
    canCreate: true,
    canRun: false,
    exchanges: ['Binance', 'Bybit', 'OKX'],
  },
  {
    id: 'leader-lag',
    version: '0.3.2',
    name: 'Поводырь → отстающая биржа',
    description: 'Живая симуляция.',
    implementationStatus: 'paper',
    canCreate: true,
    canRun: true,
    exchanges: ['Binance', 'Bybit', 'OKX', 'Hyperliquid'],
  },
];

// An in-memory stand-in for the local API, enough for the workspace screens.
async function server(page: Page) {
  const bots: (Bot & { revision: number })[] = [
    {
      id: '11111111-1111-1111-1111-111111111111',
      name: 'Поводырь · MAGIC',
      exchange: 'OKX',
      symbol: 'MAGICUSDT',
      budget: 20,
      lossLimit: 2,
      strategy: 'leader-lag',
      strategyVersion: '0.3.2',
      parameters: {
        signalExchange: 'Binance',
        settings: defaultLeaderLagSettings,
        delayMs: 50,
        counter: false,
      },
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
  await page.route('**/api/v1/paper', (route) =>
    route.fulfill({ json: { id: null, state: 'idle', botId: null } }),
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
    log(bot, 'Создан экземпляр стратегии.');
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
    log(bots[index], 'Настройки экземпляра обновлены.');
    return route.fulfill({ json: bots[index] });
  });
  await page.route('**/api/v1/bots/*/runs', (route) => route.fulfill({ json: [] }));
  return bots;
}

test('overview, trade periods and bot navigation work without console errors', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await server(page);
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Всё под контролем' })).toBeVisible();
  await expect(page.getByText('Живая симуляция.', { exact: false }).first()).toBeVisible();
  await page.screenshot({ path: 'artifacts/overview-desktop.png', fullPage: true });
  await page.getByRole('button', { name: 'Статистика' }).click();
  await expect(page.getByRole('heading', { name: 'Поводырь · MAGIC' })).toBeVisible();
  await page.locator('.detail-tabs').getByRole('button', { name: 'Сделки', exact: true }).click();
  await expect(page.locator('.trades-panel tbody tr')).toHaveCount(5);
  await page.getByRole('button', { name: '24 часа', exact: true }).click();
  await expect(page.locator('.trades-panel tbody tr')).toHaveCount(2);
  await expect(page.locator('.trades-panel')).toContainText('SIM');
  await page.getByRole('button', { name: 'Настройки', exact: true }).click();
  await page.getByLabel('Название бота').fill('Мой поводырь');
  await page.getByRole('button', { name: 'Сохранить изменения' }).click();
  await expect(page.getByRole('heading', { name: 'Мой поводырь' })).toBeVisible();
  await page.getByRole('button', { name: 'Журнал', exact: true }).click();
  await expect(page.getByText('Настройки экземпляра обновлены.')).toBeVisible();
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
  await page
    .getByRole('dialog')
    .getByRole('radio', { name: /Импульс/ })
    .check();
  await page.getByLabel('Название бота').fill('Тестовый бот');
  await page.getByRole('dialog').getByRole('button', { name: 'Создать бота', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Тестовый бот' })).toBeVisible();
  await expect(page.getByText('Черновик', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Здесь появится кривая результата')).toBeVisible();
  expect(bots.some((b) => b.name === 'Тестовый бот' && b.strategy === 'impulse')).toBe(true);
  await page.getByRole('button', { name: 'Настройки', exact: true }).click();
  const downloadPromise = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Экспорт', exact: true }).click();
  expect((await downloadPromise).suggestedFilename()).toBe('arbusdt-bot.json');
  await page.getByRole('button', { name: 'Все боты', exact: true }).click();
  await page.getByRole('button', { name: 'Импорт бота', exact: true }).click();
  await page
    .getByLabel('Или вставьте содержимое JSON')
    .fill(exportConfig({ ...bots[0], name: 'Импортированный бот' }));
  await page.getByRole('button', { name: 'Импортировать', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Импортированный бот' })).toBeVisible();
  const imported = bots.find((b) => b.name === 'Импортированный бот')!;
  expect(imported.parameters?.signalExchange).toBe('Binance');
  await page.getByRole('button', { name: 'Настройки', exact: true }).click();
  await page.getByRole('button', { name: 'Удалить', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Удалить', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Импортированный бот Поводырь v0.3.2' }),
  ).toHaveCount(0);
  expect(bots.some((b) => b.name === 'Импортированный бот')).toBe(false);
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
  await page.getByRole('button', { name: 'Мои боты', exact: false }).first().click();
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
  await expect(page.getByRole('heading', { name: 'Поводырь · MAGIC' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Поводырь · MAGIC/ }).first()).toBeVisible();
});

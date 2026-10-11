import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { defaultLeaderLagSettings } from '../../src/model';

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
const bot = {
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
  createdAt: '2026-10-11T10:00:00Z',
  revision: 1,
  status: 'draft',
  trades: [],
};
const trade = {
  entrySeconds: 130.2,
  exitSeconds: 131.1,
  side: 'long',
  entryPrice: 0.4012,
  exitPrice: 0.4019,
  netBps: 4.5,
  exitReason: 'Отставание сократилось или сменило знак',
  quantity: 24,
  netQuote: 0.0045,
};

async function server(page: Page) {
  const state = { status: 'draft', started: 0, paused: 0, stopped: 0, created: null as unknown };
  await page.route('**/api/v1/strategies', (route) => route.fulfill({ json: catalog }));
  await page.route('**/api/v1/workspace', (route) =>
    route.fulfill({
      json: { schemaVersion: 1, bots: [{ ...bot, status: state.status }], events: [] },
    }),
  );
  await page.route('**/api/v1/bots', (route) => {
    state.created = route.request().postDataJSON();
    return route.fulfill({
      status: 201,
      json: { ...bot, ...(state.created as object), id: bot.id },
    });
  });
  await page.route(`**/api/v1/bots/${bot.id}/*`, (route) => {
    const action = route.request().url().split('/').pop()!.split('?')[0];
    if (action === 'runs')
      return route.fulfill({
        json:
          state.stopped === 0
            ? []
            : [
                {
                  id: 'run-1',
                  version: '0.3.2',
                  startedUtc: '2026-10-11T10:05:00Z',
                  endedUtc: '2026-10-11T10:35:00Z',
                  state: 'finished',
                  explanation: 'Симуляция завершена.',
                  coin: 'MAGIC',
                  signalExchange: 'binance',
                  exchange: 'okx',
                  delayMs: 50,
                  counter: false,
                  leader: 'binance',
                  executionExchange: 'okx',
                  trades: 1,
                  sumNetBps: 4.5,
                  closedNetQuote: 0.0045,
                  unresolved: 0,
                  lateEvents: 0,
                  tradeLog: [trade],
                  journal: [],
                },
              ],
      });
    if (action === 'start') {
      expect(route.request().postDataJSON()).toEqual({ seconds: 600 });
      state.started++;
      state.status = 'running';
    }
    if (action === 'pause') {
      state.paused++;
      state.status = 'paused';
    }
    if (action === 'stop') {
      state.stopped++;
      state.status = 'stopped';
    }
    return route.fulfill({ status: 202, json: { id: 'session' } });
  });
  await page.route('**/api/v1/paper', (route) =>
    route.fulfill({
      json: {
        id: 'session',
        state: state.status === 'stopped' ? 'finished' : 'running',
        explanation:
          'Поводырь: binance, виртуальное исполнение на okx. Реальные заявки не отправляются.',
        coin: 'MAGIC',
        sourceExchange: 'binance',
        exchange: 'okx',
        settings: { calibrationSeconds: 120, executionModel: 'depth', entryOrder: 'taker' },
        delayMs: 50,
        counter: false,
        sessionSeconds: 140,
        leader: 'binance',
        executionExchange: 'okx',
        normalBasisBps: 1.2,
        decisions: [
          {
            seconds: 130.15,
            action: 'вход отправлен',
            reason: 'Импульс и отставание',
            side: 'long',
            price: 0.4013,
            netBps: null,
          },
        ],
        trades: [trade],
        decided: 3,
        sumNetBps: 4.5,
        closedNetQuote: 0.0045,
        lateEvents: 0,
        result: null,
        version: '0.3.2',
        executionEnabled: false,
        botId: bot.id,
        paused: state.status === 'paused',
        stopping: false,
        entriesBlocked: state.status === 'paused' ? 'Пауза: новые входы не отправляются.' : null,
        positionOpen: false,
      },
    }),
  );
  return state;
}

test('leader-lag bot is created with its strategy parameters', async ({ page }) => {
  const state = await server(page);
  await page.goto('/');
  await page.getByRole('button', { name: 'Стратегии', exact: true }).click();
  await page
    .locator('.strategy-card', {
      has: page.getByRole('heading', { name: 'Поводырь → отстающая биржа' }),
    })
    .getByRole('button', { name: 'Создать экземпляр' })
    .click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('radio', { name: /Поводырь/ })).toBeChecked();
  await dialog.getByLabel('Название бота').fill('Поводырь · MAGIC');
  await dialog.getByLabel('USDT-фьючерс').fill('MAGICUSDT');
  await dialog.getByLabel('Биржа бота (исполнение)').selectOption('OKX');
  await dialog.getByLabel('Биржа сигнала').selectOption('Binance');
  await dialog.getByLabel('Комиссия taker за сторону, б.п.').fill('2.75');
  await dialog.getByLabel('Вход в сделку').selectOption('maker');
  expect((await new AxeBuilder({ page }).include('dialog.modal').analyze()).violations).toEqual([]);
  await dialog.getByRole('button', { name: 'Создать бота', exact: true }).click();
  await expect.poll(() => state.created).not.toBeNull();
  const created = state.created as {
    strategy: string;
    exchange: string;
    parameters: { signalExchange: string; settings: { feeBps: number; entryOrder: string } };
  };
  expect(created.strategy).toBe('leader-lag');
  expect(created.exchange).toBe('OKX');
  expect(created.parameters.signalExchange).toBe('Binance');
  expect(created.parameters.settings.feeBps).toBe(2.75);
  expect(created.parameters.settings.entryOrder).toBe('maker');
});

test('bot page starts, shows, pauses and stops a live paper run', async ({ page }) => {
  const state = await server(page);
  await page.goto('/');
  await page.getByRole('button', { name: 'Поводырь · MAGIC Поводырь v0.3.2' }).first().click();
  await expect(page.getByRole('heading', { name: 'Живая симуляция' })).toBeVisible();
  await expect(page.getByText('Реальные заявки не отправляются', { exact: true })).toBeVisible();
  await page.getByLabel('Длительность').selectOption('600');
  await page.getByRole('button', { name: 'Запустить симуляцию' }).click();
  await expect(page.getByText('Симуляция идёт', { exact: true }).first()).toBeVisible();
  await expect(page.locator('.paper-facts')).toContainText('binance → okx');
  await expect(page.getByRole('table', { name: 'Последние решения' })).toContainText(
    'вход отправлен',
  );
  await expect(page.getByRole('table', { name: 'Закрытые сделки этого запуска' })).toContainText(
    'Отставание сократилось',
  );
  await page.getByRole('button', { name: 'Пауза', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Продолжить' })).toBeVisible();
  expect(state.paused).toBe(1);
  await page.getByRole('button', { name: 'Остановить', exact: true }).click();
  await expect(page.getByRole('table', { name: 'История запусков' })).toContainText('Завершён');
  await expect(page.getByRole('table', { name: 'Сделки последнего запуска' })).toBeVisible();
  expect(state.stopped).toBe(1);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect((await new AxeBuilder({ page }).include('.paper-run').analyze()).violations).toEqual([]);
});

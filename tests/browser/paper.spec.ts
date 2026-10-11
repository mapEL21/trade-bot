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

// A closed paper trade as the server returns it inside the bot: net +1.00 USDT after fees.
const simulatedTrade = () => ({
  id: 'run-1-0',
  at: new Date(Date.now() - 3600000).toISOString(),
  side: 'long',
  gross: 1.2,
  fee: 0.2,
  entry: 0.4012,
  exit: 0.4019,
  quantity: 24,
  runId: 'run-1',
  simulated: true,
});

async function server(page: Page) {
  const state = { status: 'draft', started: 0, paused: 0, stopped: 0, created: null as unknown };
  await page.route('**/api/v1/strategies', (route) => route.fulfill({ json: catalog }));
  await page.route('**/api/v1/research/markets*', (route) => {
    const url = new URL(route.request().url());
    const exchange = url.searchParams.get('exchange')!;
    const symbol = (coin: string) => (exchange === 'okx' ? `${coin}-USDT-SWAP` : `${coin}USDT`);
    return route.fulfill({
      json: {
        exchange,
        sourceExchange: url.searchParams.get('sourceExchange'),
        updatedUtc: '2026-10-11T10:00:00Z',
        instruments: ['ARB', 'MAGIC', 'SOL'].map((coin) => ({ coin, symbol: symbol(coin) })),
      },
    });
  });
  await page.route('**/api/v1/research/instrument*', (route) =>
    route.fulfill({
      json: {
        exchange: 'okx',
        coin: new URL(route.request().url()).searchParams.get('coin'),
        quantityStep: 1,
        minimumQuantity: 1,
        minimumNotional: 5,
        maximumQuantity: null,
        lastPrice: 0.4,
        minimumOrderNotional: 5.2,
        quoteCurrency: 'USDT',
        ruleSource: 'Текущие правила биржи',
      },
    }),
  );
  await page.route('**/api/v1/workspace', (route) =>
    route.fulfill({
      json: {
        schemaVersion: 1,
        bots: [{ ...bot, status: state.status, trades: state.stopped ? [simulatedTrade()] : [] }],
        events: [],
      },
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
  await dialog.getByLabel('Биржа бота (исполнение)').selectOption('OKX');
  await dialog.getByLabel('Биржа сигнала').selectOption('Binance');
  // Fees default to the bot exchange's table and are shown in percent.
  await expect(dialog.getByLabel('Комиссия taker, %')).toHaveValue('0.05');
  const pair = dialog.getByRole('combobox', { name: 'Торговая пара' });
  await pair.click();
  await pair.fill('mag');
  await pair.press('Enter');
  await expect(dialog.getByRole('status').filter({ hasText: 'шаг 1' })).toContainText(
    'минимальная заявка ≈ 5,2 USDT',
  );
  await dialog.getByLabel('Размер заявки задаётся').selectOption('coins');
  await dialog.getByLabel('Размер заявки, MAGIC').fill('30');
  await expect(dialog.getByText(/больше выделенного баланса/)).toBeVisible();
  await dialog.getByLabel('Размер заявки, MAGIC').fill('10');
  await expect(dialog.getByText(/меньше минимальной/)).toBeVisible();
  await dialog.getByLabel('Размер заявки, MAGIC').fill('20');
  await dialog.getByLabel('Комиссия taker, %').fill('0.0275');
  await dialog.getByLabel('Вход в сделку').selectOption('maker');
  await dialog.getByText('Остальные параметры стратегии').click();
  await expect(dialog.getByLabel('Порог импульса, %')).toHaveValue('0.03');
  await dialog.getByRole('button', { name: 'б.п.', exact: true }).click();
  await expect(dialog.getByLabel('Порог импульса, б.п.')).toHaveValue('3');
  expect((await new AxeBuilder({ page }).include('dialog.modal').analyze()).violations).toEqual([]);
  await dialog.getByRole('button', { name: 'Создать бота', exact: true }).click();
  await expect.poll(() => state.created).not.toBeNull();
  const created = state.created as {
    strategy: string;
    exchange: string;
    symbol: string;
    parameters: {
      signalExchange: string;
      settings: { feeBps: number; entryOrder: string; orderQuantity: number | null };
    };
  };
  expect(created.symbol).toBe('MAGICUSDT');
  expect(created.parameters.settings.orderQuantity).toBe(20);
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
  // The finished run's trade now counts in the bot's simulated balance, chart and history.
  await expect(page.getByText('СТАТИСТИКА СИМУЛЯЦИИ · НЕ РЕАЛЬНАЯ ТОРГОВЛЯ')).toBeVisible();
  await expect(page.locator('.stat', { hasText: 'Баланс · симуляция' })).toContainText('21,00');
  await expect(page.locator('.stat', { hasText: 'Чистый результат' })).toContainText('+1,00');
  await expect(page.getByRole('img', { name: /накопленного результата/ })).toBeVisible();
  await expect(page.locator('.trades-panel')).toContainText('SIM');
  expect(state.stopped).toBe(1);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect((await new AxeBuilder({ page }).include('.paper-run').analyze()).violations).toEqual([]);
});

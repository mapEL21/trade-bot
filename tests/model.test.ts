import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  csvCell,
  defaultLeaderLagSettings,
  exportConfig,
  importConfig,
  metrics,
  net,
  parseWorkspace,
  periodTrades,
  validateConfig,
  type Bot,
  type Trade,
  type Workspace,
} from '../src/model.ts';

const impulseConfig = {
  name: 'Импульс · ARB',
  exchange: 'Binance' as const,
  symbol: 'ARBUSDT',
  budget: 10,
  lossLimit: 1,
  strategy: 'impulse' as const,
  strategyVersion: '0.1.0',
};
const trade = (at: string, gross: number, fee = 0.01): Trade => ({
  id: at,
  at,
  side: 'long',
  gross,
  fee,
  entry: 0.4,
  exit: 0.4 + gross / 10,
  quantity: 10,
  simulated: true,
});
const bot = (trades: Trade[] = []): Bot => ({
  ...impulseConfig,
  id: 'bot-1',
  status: 'paused',
  trades,
  createdAt: '2026-10-01T00:00:00Z',
});

test('CSV escapes names and prevents formula execution without changing numeric losses', () => {
  assert.equal(csvCell('=1+1'), '"\'=1+1"');
  assert.equal(csvCell('  @SUM(A1)'), '"\'  @SUM(A1)"');
  assert.equal(csvCell('Мой "бот"'), '"Мой ""бот"""');
  assert.equal(csvCell(-0.25), '"-0.25"');
});

test('configuration roundtrip excludes history, IDs, and runtime status', () => {
  const source = bot([trade('2026-10-02T00:00:00Z', 0.05)]);
  const text = exportConfig(source);
  assert.equal(text.includes('trades'), false);
  assert.equal(text.includes('paused'), false);
  assert.equal(text.includes(source.id), false);
  assert.deepEqual(importConfig(text), impulseConfig);
});
test('rejects secrets, unknown versions, nonfinite values and invalid risk limits', () => {
  const config = importConfig(exportConfig(bot()));
  assert.throws(() => validateConfig({ ...config, apiKey: 'should-not-be-imported' }));
  assert.throws(() => validateConfig({ ...config, budget: Infinity }));
  assert.throws(() => validateConfig({ ...config, lossLimit: 11 }));
  assert.throws(() => validateConfig({ ...config, symbol: 'ARBUSDT\n' }));
  assert.throws(() => importConfig('{"schemaVersion":2}'));
  assert.throws(() => importConfig('invalid json'));
  assert.throws(() => importConfig(' '.repeat(32769)));
});
test('statistics account for fees and drawdown in chronological order', () => {
  const trades = [
    trade('2026-10-03T00:00:00Z', 2, 0.1),
    trade('2026-10-01T00:00:00Z', 1, 0.1),
    trade('2026-10-02T00:00:00Z', -0.5, 0.1),
  ];
  const stats = metrics(trades);
  assert.ok(Math.abs(stats.pnl - 2.2) < 1e-9);
  assert.ok(Math.abs(stats.drawdown - 0.6) < 1e-9);
  assert.equal(stats.count, 3);
  assert.ok(Math.abs(stats.winRate! - 200 / 3) < 1e-9);
  assert.equal(metrics([]).winRate, null);
  // A maker rebate is a negative fee and adds to the result.
  assert.equal(net(trade('2026-10-01T00:00:00Z', 1, -0.02)), 1.02);
});
test('periods count back from the given moment, by default from now', () => {
  const now = Date.parse('2026-10-11T12:00:00Z');
  const trades = [
    trade('2026-10-11T06:00:00Z', 0.1),
    trade('2026-10-08T06:00:00Z', 0.1),
    trade('2026-09-20T06:00:00Z', 0.1),
    trade('2026-10-12T06:00:00Z', 0.1),
  ];
  assert.equal(periodTrades(trades, 1, now).length, 1);
  assert.equal(periodTrades(trades, 7, now).length, 2);
  assert.equal(periodTrades(trades, 30, now).length, 3);
  assert.equal(periodTrades([trade(new Date().toISOString(), 0.1)], 1).length, 1);
});
test('server workspace validation handles corrupted records', () => {
  const ws: Workspace = {
    schemaVersion: 1,
    bots: [bot([trade('2026-10-02T00:00:00Z', 0.05)])],
    events: [],
  };
  assert.deepEqual(parseWorkspace(JSON.stringify(ws)), ws);
  assert.throws(() => parseWorkspace('{"schemaVersion":1,"bots":[null],"events":[]}'));
  assert.throws(() => parseWorkspace(JSON.stringify({ ...ws, bots: [ws.bots[0], ws.bots[0]] })));
  assert.throws(() =>
    parseWorkspace(JSON.stringify({ ...ws, bots: [{ ...ws.bots[0], status: 'unknown' }] })),
  );
});
test('leader-lag configuration keeps its parameters through export and import, and stays strict', () => {
  const config = {
    name: 'Поводырь · MAGIC',
    exchange: 'Binance' as const,
    symbol: 'MAGICUSDT',
    budget: 20,
    lossLimit: 2,
    strategy: 'leader-lag' as const,
    strategyVersion: '0.3.2',
    parameters: {
      signalExchange: 'OKX' as const,
      settings: { ...defaultLeaderLagSettings },
      delayMs: 50,
      counter: false,
    },
  };
  const saved: Bot = {
    ...validateConfig(config),
    id: 'bot-2',
    status: 'draft',
    trades: [],
    createdAt: '2026-10-01T00:00:00Z',
  };
  assert.deepEqual(importConfig(exportConfig(saved)), validateConfig(config));
  assert.throws(() => validateConfig({ ...config, parameters: undefined }));
  assert.throws(() =>
    validateConfig({ ...config, parameters: { ...config.parameters, signalExchange: 'Binance' } }),
  );
  assert.throws(() =>
    validateConfig({ ...config, parameters: { ...config.parameters, delayMs: 0 } }),
  );
  assert.throws(() =>
    validateConfig({
      ...config,
      parameters: {
        ...config.parameters,
        settings: { ...defaultLeaderLagSettings, orderNotional: 50 },
      },
    }),
  );
  assert.throws(() =>
    validateConfig({
      ...config,
      parameters: { ...config.parameters, settings: { ...defaultLeaderLagSettings, apiKey: 'x' } },
    }),
  );
  // A size in coins is not limited by the USDT nominal check, which needs a price.
  assert.equal(
    validateConfig({
      ...config,
      parameters: {
        ...config.parameters,
        settings: { ...defaultLeaderLagSettings, orderNotional: 50, orderQuantity: 30 },
      },
    }).parameters!.settings.orderQuantity,
    30,
  );
  assert.equal(
    validateConfig({
      ...config,
      exchange: 'Hyperliquid',
      parameters: { ...config.parameters, signalExchange: 'Binance' },
    }).exchange,
    'Hyperliquid',
  );
  // The impulse template keeps its three exchanges and accepts no strategy parameters.
  assert.throws(() => validateConfig({ ...impulseConfig, exchange: 'Hyperliquid' }));
  assert.throws(() => validateConfig({ ...impulseConfig, parameters: config.parameters }));
  assert.doesNotThrow(() => validateConfig({ ...impulseConfig, parameters: null }));
});

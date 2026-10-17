import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  csvCell,
  defaultSignalParameters,
  exportConfig,
  importConfig,
  metrics,
  net,
  parseWorkspace,
  periodTrades,
  tradeQuality,
  validateConfig,
  type Bot,
  type Trade,
  type Workspace,
} from '../src/model.ts';

const signalConfig = {
  name: 'Вынос цены · 5 мин',
  exchange: 'Binance' as const,
  symbol: '*',
  budget: 10,
  lossLimit: 1,
  strategy: 'signal' as const,
  strategyVersion: '0.1.0',
  signal: defaultSignalParameters,
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
  ...signalConfig,
  id: 'bot-1',
  status: 'stopped',
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
  assert.equal(text.includes('stopped'), false);
  assert.equal(text.includes(source.id), false);
  assert.deepEqual(importConfig(text), signalConfig);
});
test('rejects secrets, unknown versions, nonfinite values and invalid risk limits', () => {
  const config = importConfig(exportConfig(bot()));
  assert.throws(() => validateConfig({ ...config, apiKey: 'should-not-be-imported' }));
  assert.throws(() => validateConfig({ ...config, budget: Infinity }));
  assert.throws(() => validateConfig({ ...config, lossLimit: 11 }));
  // The bot trades every coin of the recording; old strategies are no longer accepted.
  assert.throws(() => validateConfig({ ...config, symbol: 'ARBUSDT' }));
  assert.throws(() => validateConfig({ ...config, strategy: 'leader-lag' }), /старые стратегии/);
  assert.throws(() => validateConfig({ ...config, strategy: 'impulse' }));
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
test('trade quality separates entry and exit problems and groups exit reasons', () => {
  // 10 USDT long trades with 0.01 USDT fees (10 b.p. round trip).
  const at = (
    exit: number,
    reason: string,
    favorable: number | undefined,
    adverse: number | undefined,
  ): Trade => ({
    ...trade('2026-10-02T00:00:00Z', (exit - 1) * 10, 0.01),
    entry: 1,
    exit,
    quantity: 10,
    notional: 10,
    holdSeconds: 0.5,
    exitReason: reason,
    maxFavorableBps: favorable,
    maxAdverseBps: adverse,
  });
  const q = tradeQuality([
    at(1.002, 'Отставание сократилось или сменило знак', 25, -2),
    at(0.9997, 'Поводырь развернулся', 4, -3),
    at(0.9997, 'Повтор выхода: Поводырь развернулся', 12, -3),
    at(0.999, 'Ограничение убытка', undefined, undefined),
  ]);
  assert.equal(q.count, 4);
  assert.equal(q.measured, 3);
  // 2 of 3 measured trades moved at least the 10 b.p. of fees at some point.
  assert.ok(Math.abs(q.reachedFeesShare! - 200 / 3) < 1e-9);
  // Given back: 25 − 20, 4 − (−3), 12 − (−3).
  assert.ok(Math.abs(q.givenBackBps! - 27 / 3) < 1e-6);
  assert.ok(Math.abs(q.avgFeeBps! - 10) < 1e-9);
  // A retried exit counts under its original reason.
  assert.deepEqual(
    q.reasons.map((r) => [r.reason, r.count]),
    [
      ['Поводырь развернулся', 2],
      ['Отставание сократилось или сменило знак', 1],
      ['Ограничение убытка', 1],
    ],
  );
  assert.equal(q.reasons[0].winRate, 0);
  assert.ok(Math.abs(q.reasons[0].avgFavorableBps! - 8) < 1e-9);
  assert.ok(Math.abs(q.profitFactor! - 0.01 / 0.046) < 1e-9);
  assert.equal(tradeQuality([]).avgNet, null);
});

test('signal bot settings: every coin of the recording, risk and limits checked, round trip through export', () => {
  const config = {
    name: 'Вынос цены · 5 мин',
    exchange: 'Binance' as const,
    symbol: '*',
    budget: 100,
    lossLimit: 30,
    strategy: 'signal' as const,
    strategyVersion: '0.1.0',
    signal: defaultSignalParameters,
  };
  assert.deepEqual(validateConfig(config), config);
  assert.equal(defaultSignalParameters.riskPct, 1.5);
  assert.throws(() => validateConfig({ ...config, symbol: 'ARBUSDT' }), /все монеты/);
  assert.throws(
    () => validateConfig({ ...config, signal: { ...defaultSignalParameters, riskPct: 9 } }),
    /Риск на сделку/,
  );
  assert.throws(
    () => validateConfig({ ...config, signal: { ...defaultSignalParameters, signal: 'Луна' } }),
    /сигнал/,
  );
  const bot = {
    ...config,
    id: 'b',
    status: 'draft',
    trades: [],
    createdAt: '2026-10-01T00:00:00Z',
    revision: 1,
  } as unknown as Bot;
  assert.deepEqual(importConfig(exportConfig(bot)), config);
});

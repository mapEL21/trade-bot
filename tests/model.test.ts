import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  createDemo,
  csvCell,
  exportConfig,
  importConfig,
  makeBot,
  metrics,
  net,
  parseWorkspace,
  periodTrades,
  validateConfig,
} from '../src/model.ts';

test('CSV escapes names and prevents formula execution without changing numeric losses', () => {
  assert.equal(csvCell('=1+1'), '"\'=1+1"');
  assert.equal(csvCell('  @SUM(A1)'), '"\'  @SUM(A1)"');
  assert.equal(csvCell('Мой "бот"'), '"Мой ""бот"""');
  assert.equal(csvCell(-0.25), '"-0.25"');
});

test('configuration roundtrip excludes history, IDs, and runtime status', () => {
  const demo = createDemo().bots[0];
  const text = exportConfig(demo);
  assert.equal(text.includes('trades'), false);
  assert.equal(text.includes('paused'), false);
  const bot = makeBot(importConfig(text));
  assert.equal(bot.name, demo.name);
  assert.notEqual(bot.id, demo.id);
  assert.equal(bot.status, 'draft');
  assert.deepEqual(bot.trades, []);
});
test('rejects secrets, unknown versions, nonfinite values and invalid risk limits', () => {
  const config = importConfig(exportConfig(createDemo().bots[0]));
  assert.throws(() => validateConfig({ ...config, apiKey: 'should-not-be-imported' }));
  assert.throws(() => validateConfig({ ...config, budget: Infinity }));
  assert.throws(() => validateConfig({ ...config, lossLimit: 11 }));
  assert.throws(() => validateConfig({ ...config, symbol: 'ARBUSDT\n' }));
  assert.throws(() => importConfig('{"schemaVersion":2}'));
  assert.throws(() => importConfig('invalid json'));
  assert.throws(() => importConfig(' '.repeat(32769)));
});
test('statistics account for fees and drawdown in chronological order', () => {
  const base = createDemo().bots[0].trades[0];
  const trades = [
    { ...base, at: '2026-10-03T00:00:00Z', gross: 2, fee: 0.1 },
    { ...base, at: '2026-10-01T00:00:00Z', gross: 1, fee: 0.1 },
    { ...base, at: '2026-10-02T00:00:00Z', gross: -0.5, fee: 0.1 },
  ];
  const stats = metrics(trades);
  assert.ok(Math.abs(stats.pnl - 2.2) < 1e-9);
  assert.ok(Math.abs(stats.drawdown - 0.6) < 1e-9);
  assert.equal(stats.count, 3);
  assert.ok(Math.abs(stats.winRate! - 200 / 3) < 1e-9);
  assert.equal(metrics([]).winRate, null);
});
test('demo accounting is internally consistent and time filtering changes selection', () => {
  const trades = createDemo().bots[0].trades;
  for (const t of trades)
    assert.ok(
      Math.abs((t.exit - t.entry) * t.quantity * (t.side === 'long' ? 1 : -1) - t.gross) < 1e-9,
    );
  assert.ok(periodTrades(trades, 1).length < periodTrades(trades, 7).length);
  assert.ok(Math.abs(metrics(trades).pnl - trades.reduce((s, t) => s + net(t), 0)) < 1e-9);
});
test('stored workspace validation handles corrupted records', () => {
  const ws = createDemo();
  assert.deepEqual(parseWorkspace(JSON.stringify(ws)), ws);
  assert.throws(() => parseWorkspace('{"schemaVersion":1,"bots":[null],"events":[]}'));
  ws.bots.push(ws.bots[0]);
  assert.throws(() => parseWorkspace(JSON.stringify(ws)));
});

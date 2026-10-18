import type { ReactNode } from 'react';
import { tradingSessions } from './api';
import { SignalRunPanel } from './SignalBot';
import { formatMoney, onExchange, type Bot } from './model';

// What each strategy adds to the common bot page. Everything else — statistics, balance, the result curve, trade
// quality and trade history — is built from closed trades by TradeAnalysis.tsx and is the same for every strategy.
// A new algorithm registers here: its name, its own live panel (if it can run) and the parameters worth showing.
export interface StrategyView {
  name: string;
  // Live controls and state of the strategy's run, with strategy-specific analysis (e.g. the leader for leader-lag).
  live?: (bot: Bot, onChanged: () => void) => ReactNode;
  facts: (bot: Bot) => { label: string; value: string }[];
  note: string;
}
const executionLabels = {
  simulation: 'симуляция',
  testnet: 'демо-счёт Binance',
  live: 'реальный счёт Binance',
} as const;
const signal: StrategyView = {
  name: 'Сигнал лаборатории',
  live: (bot, onChanged) => <SignalRunPanel bot={bot} onChanged={onChanged} />,
  facts: (bot) => {
    const p = bot.signal!;
    const pct = (n: number) => `${n.toLocaleString('ru-RU', { maximumFractionDigits: 2 })}%`;
    return [
      { label: 'Стратегия', value: `${p.signal} · v${bot.strategyVersion}` },
      { label: 'Вход и выход', value: 'по логике стратегии' },
      { label: 'Где торгует', value: executionLabels[bot.execution ?? 'simulation'] },
      onExchange(bot)
        ? {
            label: 'Депозит · лимит убытка',
            value: `баланс счёта${bot.deposit != null ? ` (${formatMoney(bot.deposit)} USDT)` : ''} · ${pct(bot.lossLimit)}`,
          }
        : { label: 'Депозит', value: `${formatMoney(bot.budget)} USDT` },
      { label: 'Риск на сделку', value: `${pct(p.riskPct)} депозита с комиссиями` },
      { label: 'Комиссии круга', value: pct(p.feePct) },
      { label: 'Плечо · сделок одновременно', value: `до ${p.maxLeverage}x · ${p.maxPositions}` },
      {
        label: 'Дневной лимит · позиция',
        value: `${pct(p.dailyLossPct ?? 10)} депозита · до ${formatMoney(p.maxNotional ?? 100)} USDT`,
      },
      {
        label: 'Сессии',
        value: p.sessions?.length
          ? tradingSessions
              .filter((s) => p.sessions!.includes(s.id))
              .map((s) => s.name.split(' · ')[0])
              .join(', ')
          : 'все часы',
      },
      { label: 'Лимит убытка', value: `${formatMoney(bot.lossLimit)} USDT` },
    ];
  },
  note: 'Сделки виртуальные: исполнение по лучшим ценам стакана из записи через 0,25 с после решения; проскальзывание глубже и funding не учтены.',
};

export const strategyViews: Record<Bot['strategy'], StrategyView> = { signal };
export const strategyView = (bot: Bot) => strategyViews[bot.strategy];

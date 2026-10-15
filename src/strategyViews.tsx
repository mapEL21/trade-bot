import type { ReactNode } from 'react';
import { PaperRun } from './PaperRun';
import { SignalRunPanel } from './SignalBot';
import { formatMoney, type Bot } from './model';

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
// Fees and thresholds are stored in basis points and shown in percent.
const percent = (bps: number) =>
  `${(bps / 100).toLocaleString('ru-RU', { maximumFractionDigits: 6 })}%`;

const leaderLag: StrategyView = {
  name: 'Поводырь → отстающая биржа',
  live: (bot, onChanged) => <PaperRun bot={bot} onChanged={onChanged} />,
  facts: (bot) => {
    const p = bot.parameters!;
    const s = p.settings;
    // Bots saved before leverage existed have none: they trade at 1x.
    const leverage = p.leverage ?? 1;
    return [
      { label: 'Стратегия', value: 'Поводырь → отстающая биржа' },
      { label: 'Версия', value: bot.strategyVersion },
      { label: 'Биржа бота / сигнала', value: `${bot.exchange} / ${p.signalExchange}` },
      {
        label: 'Размер заявки',
        value:
          s.orderQuantity !== null
            ? `${s.orderQuantity} ${bot.symbol.replace(/USDT$/, '')}`
            : `${formatMoney(s.orderNotional)} USDT`,
      },
      {
        label: 'Плечо',
        value:
          s.orderQuantity === null
            ? `${leverage}x · маржа ${formatMoney(s.orderNotional / leverage)} USDT`
            : `${leverage}x`,
      },
      { label: 'Лимит убытка', value: `${formatMoney(bot.lossLimit)} USDT` },
      {
        label: 'Вход · исполнение',
        value: `${s.entryOrder === 'maker' ? 'maker' : 'taker'} · ${s.executionModel === 'depth' ? 'по стакану' : 'BBO'} · ${p.delayMs} мс`,
      },
      {
        label: 'Комиссии taker / maker',
        value: `${percent(s.feeBps)} / ${percent(s.makerFeeBps)}`,
      },
    ];
  },
  note: 'Лимит убытка и размер заявки применяются в симуляции. Реальных позиций и ордеров нет.',
};

const impulse: StrategyView = {
  name: 'Импульс',
  facts: (bot) => [
    { label: 'Стратегия', value: 'Импульс' },
    { label: 'Версия', value: `${bot.strategyVersion} · шаблон` },
    { label: 'Выделено', value: `${formatMoney(bot.budget)} USDT` },
    { label: 'Лимит убытка', value: `${formatMoney(bot.lossLimit)} USDT` },
    { label: 'Открытая позиция', value: 'Нет позиции' },
  ],
  note: 'Настройки риска пока не исполняются: торгового движка нет.',
};

const signal: StrategyView = {
  name: 'Сигнал лаборатории',
  live: (bot, onChanged) => <SignalRunPanel bot={bot} onChanged={onChanged} />,
  facts: (bot) => {
    const p = bot.signal!;
    const pct = (n: number) => `${n.toLocaleString('ru-RU', { maximumFractionDigits: 2 })}%`;
    return [
      { label: 'Стратегия', value: `Сигнал лаборатории · v${bot.strategyVersion}` },
      {
        label: 'Сигнал',
        value: `${p.signal} · ${p.mode === 'follow' ? 'по движению' : 'против движения'}`,
      },
      {
        label: 'Удержание',
        value: p.holdSeconds >= 60 ? `${p.holdSeconds / 60} мин` : `${p.holdSeconds} с`,
      },
      { label: 'Депозит', value: `${formatMoney(bot.budget)} USDT` },
      { label: 'Риск на сделку', value: `${pct(p.riskPct)} депозита с комиссиями` },
      { label: 'Стоп', value: `${p.stopRanges} обычных минутных свечи` },
      {
        label: 'Комиссии круга',
        value: `${pct(p.feePct)} · не больше ${pct(p.maxFeeShare * 100)} риска`,
      },
      { label: 'Плечо · сделок одновременно', value: `до ${p.maxLeverage}x · ${p.maxPositions}` },
      { label: 'Лимит убытка', value: `${formatMoney(bot.lossLimit)} USDT` },
    ];
  },
  note: 'Сделки виртуальные: вход и выход по лучшим ценам стакана из записи, проскальзывание глубже и funding не учтены.',
};

export const strategyViews: Record<Bot['strategy'], StrategyView> = {
  'leader-lag': leaderLag,
  impulse,
  signal,
};
// A leader-lag bot without parameters cannot run; it falls back to the plain view.
export const strategyView = (bot: Bot) =>
  bot.strategy === 'leader-lag' && !bot.parameters ? impulse : strategyViews[bot.strategy];

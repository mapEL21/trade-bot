import type { BotTemplate } from './api';
import { defaultLeaderLagSettings, type Exchange, type LeaderLagSettings } from './model';

// Settings are stored in basis points (1 b.p. = 0.01%); the bot form can show them in percent.
export type NumericSetting = Exclude<
  keyof LeaderLagSettings,
  'executionModel' | 'entryOrder' | 'orderQuantity' | 'leader'
>;
export interface SettingMeta {
  key: NumericSetting;
  label: string;
  unit: 'bps' | 'ms' | 's' | 'usdt';
  min: number;
  max: number;
  step: number;
  hint: string;
}

export const settingMeta: SettingMeta[] = [
  {
    key: 'feeBps',
    label: 'Комиссия taker',
    unit: 'bps',
    min: 0,
    max: 100,
    step: 0.01,
    hint: 'Комиссия за исполнение по встречной цене, за одну сторону: вход или выход.',
  },
  {
    key: 'makerFeeBps',
    label: 'Комиссия maker',
    unit: 'bps',
    min: -10,
    max: 100,
    step: 0.01,
    hint: 'Комиссия за заявку, которая встала в стакан и дождалась исполнения. Используется только при входе maker.',
  },
  {
    key: 'calibrationSeconds',
    label: 'Начальный участок',
    unit: 's',
    min: 30,
    max: 1800,
    step: 1,
    hint: 'Сколько секунд в начале запуска бот только наблюдает и измеряет обычную разницу цен между биржами: от неё считается отставание. Сделок в это время нет. Поводырь не выбирается — это биржа сигнала.',
  },
  {
    key: 'impulseBps',
    label: 'Порог импульса',
    unit: 'bps',
    min: 0.1,
    max: 1000,
    step: 0.01,
    hint: 'Насколько цена на бирже-поводыре должна сдвинуться за окно движения, чтобы это считалось сигналом.',
  },
  {
    key: 'windowMs',
    label: 'Окно движения',
    unit: 'ms',
    min: 10,
    max: 5000,
    step: 1,
    hint: 'За какое время измеряется движение поводыря.',
  },
  {
    key: 'minEdgeBps',
    label: 'Запас сверх расходов',
    unit: 'bps',
    min: 0,
    max: 100,
    step: 0.01,
    hint: 'Сколько отставания должно оставаться сверх комиссий, спреда и проскальзывания, чтобы вход имел смысл. Когда отставание сокращается до этого запаса, бот выходит.',
  },
  {
    key: 'slippageBps',
    label: 'Дополнительное проскальзывание',
    unit: 'bps',
    min: 0,
    max: 100,
    step: 0.01,
    hint: 'Ухудшение цены сверх записанного стакана за каждую сторону — запас на неточность модели.',
  },
  {
    key: 'priceLimitBps',
    label: 'Предел цены заявки',
    unit: 'bps',
    min: 0,
    max: 100,
    step: 0.01,
    hint: 'Насколько хуже текущей лучшей цены бот согласен исполниться. Всё, что дальше, не исполняется.',
  },
  {
    key: 'stopLossBps',
    label: 'Порог убытка сделки',
    unit: 'bps',
    min: 1,
    max: 1000,
    step: 0.01,
    hint: 'Убыток по текущей цене выхода с комиссиями, при котором бот отправляет заявку на выход. Цену выхода не гарантирует: действует задержка.',
  },
  {
    key: 'maxHoldMs',
    label: 'Максимальное удержание',
    unit: 'ms',
    min: 100,
    max: 10000,
    step: 1,
    hint: 'Сколько бот держит позицию, если другие причины выхода не наступили.',
  },
  {
    key: 'maxAgeMs',
    label: 'Свежесть котировки',
    unit: 'ms',
    min: 10,
    max: 5000,
    step: 1,
    hint: 'Котировка старше этого считается устаревшей: бот не входит, а открытая позиция получает неопределённый результат.',
  },
  {
    key: 'reversalBps',
    label: 'Разворот поводыря для выхода',
    unit: 'bps',
    min: 0.1,
    max: 1000,
    step: 0.1,
    hint: 'Если цена на бирже-поводыре за окно движения ушла против позиции на столько, бот выходит. Больше порога импульса — меньше выходов на шуме.',
  },
  {
    key: 'leaderAdvantageBps',
    label: 'Преимущество поводыря',
    unit: 'bps',
    min: 0.1,
    max: 100,
    step: 0.01,
    hint: 'Только для автоматического выбора поводыря при проверке записи: на сколько реакция ведомой биржи на поводыря должна быть сильнее обратной. Бот это не использует.',
  },
  {
    key: 'makerWaitMs',
    label: 'Ожидание исполнения maker',
    unit: 'ms',
    min: 10,
    max: 60000,
    step: 1,
    hint: 'Сколько ждать исполнения maker-заявки, прежде чем отменить её.',
  },
];

// Fee tables from the user's broker screenshots, in b.p.: X Binance and X OKX perpetual maker 0.02% / taker 0.05%,
// Bybit via the broker maker 0.014% / taker 0.035%. Whether the 45% TigerX reduction is already applied is unconfirmed.
// Hyperliquid: the public base tier (maker 0.015% / taker 0.045%), not a broker rate.
export const defaultFees: Record<Exchange, { taker: number; maker: number; note: string }> = {
  Binance: {
    taker: 5,
    maker: 2,
    note: 'таблица X Binance через TigerX; учтена ли скидка 45%, не подтверждено',
  },
  OKX: {
    taker: 5,
    maker: 2,
    note: 'таблица X OKX через TigerX; учтена ли скидка 45%, не подтверждено',
  },
  Bybit: { taker: 3.5, maker: 1.4, note: 'условия Bybit через брокера со скриншота' },
  Hyperliquid: {
    taker: 4.5,
    maker: 1.5,
    note: 'публичный базовый тариф Hyperliquid, не брокерский',
  },
};

const round = (value: number) => Math.round(value * 1e6) / 1e6;
export const toDisplay = (bps: number, percent: boolean) => (percent ? round(bps / 100) : bps);
export const fromDisplay = (value: number, percent: boolean) =>
  percent ? round(value * 100) : value;
export const unitLabel = (unit: SettingMeta['unit'], percent: boolean) =>
  unit === 'bps' ? (percent ? '%' : 'б.п.') : unit === 'ms' ? 'мс' : unit === 's' ? 'с' : 'USDT';

// Built-in starting points. Fees are not part of them: applying one keeps the fees of the bot's exchange.
const statsSettings: LeaderLagSettings = {
  ...defaultLeaderLagSettings,
  calibrationSeconds: 60,
  impulseBps: 2,
  slippageBps: 0,
  minEdgeBps: 0.5,
  stopLossBps: 25,
  maxHoldMs: 5000,
  reversalBps: 4,
  orderNotional: 200,
};
export const builtInTemplates: BotTemplate[] = [
  {
    id: 'builtin-stats',
    name: 'Поводырь · сбор статистики',
    description:
      'Больше входов при той же логике: сигнал от 2 б.п., вход только если отставание покрывает комиссии, спред и запас 0,5 б.п.; выход при сближении цен, развороте поводыря от 4 б.п., убытке 25 б.п. или через 5 с. Начальный участок 60 с. Проскальзывание 0: в модели стакана цена считается по уровням.',
    strategy: 'leader-lag',
    budget: 100,
    lossLimit: 10,
    leverage: 2,
    delayMs: 50,
    counter: false,
    settings: statsSettings,
    builtIn: true,
  },
  {
    id: 'builtin-careful',
    name: 'Поводырь · осторожный',
    description:
      'Прежние пороги: импульс 3 б.п., запас 2 б.п. и проскальзывание 1 б.п. сверх комиссий, выход при развороте от 3 б.п., убытке 20 б.п. или через 2 с. Входов меньше.',
    strategy: 'leader-lag',
    budget: 100,
    lossLimit: 10,
    leverage: 2,
    delayMs: 50,
    counter: false,
    settings: { ...defaultLeaderLagSettings, orderNotional: 200 },
    builtIn: true,
  },
];

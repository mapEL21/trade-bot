export type Exchange = 'Binance' | 'Bybit' | 'OKX' | 'Hyperliquid';
export type Status = 'running' | 'draft' | 'stopped';
export const maxLeverage = 125;
// A bot's strategy and money management (SignalParameters on the server). Entries and exits are the strategy's own logic
// (docs/BOT_LOGIC.md), never a stop or a target set here. riskPct: the share of the deposit lost, fees included, when the
// strategy's cancel level is reached; it sets the size.
export interface SignalParameters {
  signal: string;
  riskPct: number;
  feePct: number;
  maxLeverage: number;
  maxPositions: number;
  // Trading sessions the bot opens trades in (asia, europe, us, late); missing or empty means all hours.
  sessions?: string[];
  // Realized loss of a Moscow day, % of the starting deposit, after which new trades wait for the next day.
  dailyLossPct: number;
}
export const sessionIds = ['asia', 'europe', 'us', 'late'];
export const strategies: { id: string; description: string }[] = [
  {
    id: 'Плотность',
    description:
      'Отскок от крупной заявки в стакане, пока её не проедают; выход до проедания с переворотом в пробой.',
  },
  {
    id: 'Вынос цены',
    description: 'Резкий принудительный ход цены: держим, пока движение не выдохлось.',
  },
];
export const signalVersion = '0.2.0';
export const defaultSignalParameters: SignalParameters = {
  signal: 'Плотность',
  riskPct: 1.5,
  feePct: 0.1,
  maxLeverage: 20,
  maxPositions: 3,
  dailyLossPct: 10,
};
export interface BotConfig {
  name: string;
  exchange: Exchange;
  symbol: string;
  budget: number;
  lossLimit: number;
  strategy: 'signal';
  strategyVersion: string;
  signal?: SignalParameters;
  // Where the bot trades: virtual trades, Binance's demo account or the real account. A bot on an exchange takes the account
  // balance as its deposit; its budget is then 100 and lossLimit is a percentage.
  execution?: Execution;
}
export type Execution = 'simulation' | 'testnet' | 'live';
export const onExchange = (c: { execution?: Execution }) =>
  c.execution === 'testnet' || c.execution === 'live';
export interface Trade {
  id: string;
  at: string;
  side: 'long' | 'short';
  gross: number;
  fee: number;
  entry: number;
  exit: number;
  quantity: number;
  // Set by the server: simulated trades of a bot run, never a real execution.
  runId?: string;
  simulated?: boolean;
  // Common to every strategy, taken from the run that made the trade rather than the bot's current settings.
  symbol?: string;
  exchange?: string;
  openedAt?: string;
  holdSeconds?: number;
  // Position size in USDT at the entry price.
  notional?: number;
  exitReason?: string;
  // Best and worst price move in the trade's direction while it was open, before fees, b.p.
  maxFavorableBps?: number;
  maxAdverseBps?: number;
  // Why the strategy entered, in words; exitReason says why it left.
  entryReason?: string;
}
export interface Bot extends BotConfig {
  revision?: number;
  id: string;
  status: Status;
  trades: Trade[];
  createdAt: string;
  // Result of an open position if closed now, after fees; set by the server while a run holds one.
  unrealized?: number | null;
  // Deposit of the latest run: for a bot on an exchange, the account balance when it started.
  deposit?: number | null;
}
export interface Event {
  botId?: string;
  id: string;
  at: string;
  botName: string;
  text: string;
}
export interface Workspace {
  schemaVersion: 1;
  bots: Bot[];
  events: Event[];
}
export const statusLabels: Record<Status, string> = {
  running: 'Работает',
  draft: 'Не запускался',
  stopped: 'Остановлен',
};
export const formatMoney = (n: number, signed = false) =>
  `${signed && n > 0 ? '+' : ''}${n.toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
export const net = (t: Trade) => t.gross - t.fee;
// Keep user-provided names as text when opened in spreadsheet software.
export function csvCell(value: string | number): string {
  const text =
    typeof value === 'string' && /^[\s]*[=+@-]/.test(value) ? `'${value}` : String(value);
  return `"${text.replace(/"/g, '""')}"`;
}
export function metrics(trades: Trade[]) {
  let equity = 0,
    peak = 0,
    drawdown = 0;
  const ordered = [...trades].sort((a, b) => Date.parse(a.at) - Date.parse(b.at));
  for (const t of ordered) {
    equity += net(t);
    peak = Math.max(peak, equity);
    drawdown = Math.max(drawdown, peak - equity);
  }
  return {
    pnl: equity,
    fees: trades.reduce((sum, t) => sum + t.fee, 0),
    count: trades.length,
    winRate: trades.length ? (trades.filter((t) => net(t) > 0).length / trades.length) * 100 : null,
    drawdown,
  };
}
// Price move in the trade's direction before fees, b.p.
export const tradeMoveBps = (t: Trade) =>
  (t.exit / t.entry - 1) * (t.side === 'long' ? 10000 : -10000);
export const tradeNotional = (t: Trade) => t.notional ?? t.quantity * t.entry;
// Exit reasons are grouped by their kind: a retried exit counts under its original reason, details after a colon
// (leverage, user action) are dropped.
export const exitKind = (reason: string) => reason.replace(/^Повтор выхода: /, '').split(':')[0];
const mean = (values: number[]) =>
  values.length ? values.reduce((a, b) => a + b, 0) / values.length : null;
export interface ExitReasonStats {
  reason: string;
  count: number;
  pnl: number;
  winRate: number;
  avgMoveBps: number;
  avgHoldSeconds: number | null;
  avgFavorableBps: number | null;
}
// Trade quality shared by every strategy: whether losses come from entries (price never moved enough to pay the
// fees) or from exits (it did, and the gain was given back). Excursions exist only for trades that recorded them.
export function tradeQuality(trades: Trade[]) {
  const nets = trades.map(net);
  const wins = nets.filter((n) => n > 0);
  const losses = nets.filter((n) => n <= 0);
  const lost = -losses.reduce((a, b) => a + b, 0);
  const won = wins.reduce((a, b) => a + b, 0);
  const measured = trades.filter((t) => t.maxFavorableBps != null && t.maxAdverseBps != null);
  const feeBps = (t: Trade) => (t.fee / tradeNotional(t)) * 10000;
  const groups = new Map<string, Trade[]>();
  for (const t of trades) {
    const key = exitKind(t.exitReason ?? 'Причина не записана');
    groups.set(key, [...(groups.get(key) ?? []), t]);
  }
  return {
    count: trades.length,
    avgNet: mean(nets),
    avgWin: mean(wins),
    avgLoss: mean(losses),
    profitFactor: lost > 0 ? won / lost : null,
    grossPnl: trades.reduce((s, t) => s + t.gross, 0),
    fees: trades.reduce((s, t) => s + t.fee, 0),
    avgMoveBps: mean(trades.map(tradeMoveBps)),
    avgFeeBps: mean(trades.map(feeBps)),
    avgHoldSeconds: mean(trades.flatMap((t) => (t.holdSeconds != null ? [t.holdSeconds] : []))),
    measured: measured.length,
    avgFavorableBps: mean(measured.map((t) => t.maxFavorableBps!)),
    avgAdverseBps: mean(measured.map((t) => t.maxAdverseBps!)),
    // Share of trades where price at some point moved far enough to pay both fees.
    reachedFeesShare: measured.length
      ? (measured.filter((t) => t.maxFavorableBps! >= feeBps(t)).length / measured.length) * 100
      : null,
    // How much of the best move was given back by the exit, on average.
    givenBackBps: mean(measured.map((t) => t.maxFavorableBps! - tradeMoveBps(t))),
    reasons: [...groups]
      .map(([reason, list]): ExitReasonStats => ({
        reason,
        count: list.length,
        pnl: list.reduce((s, t) => s + net(t), 0),
        winRate: (list.filter((t) => net(t) > 0).length / list.length) * 100,
        avgMoveBps: mean(list.map(tradeMoveBps))!,
        avgHoldSeconds: mean(list.flatMap((t) => (t.holdSeconds != null ? [t.holdSeconds] : []))),
        avgFavorableBps: mean(
          list.flatMap((t) => (t.maxFavorableBps != null ? [t.maxFavorableBps] : [])),
        ),
      }))
      .sort((a, b) => b.count - a.count),
  };
}
export function periodTrades(trades: Trade[], days: number, now = Date.now()) {
  return trades.filter((t) => Date.parse(t.at) >= now - days * 86400000 && Date.parse(t.at) <= now);
}
export function validateConfig(value: unknown): BotConfig {
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new Error('Настройки бота должны быть объектом.');
  const v = value as Record<string, unknown>;
  const allowed = [
    'name',
    'exchange',
    'symbol',
    'budget',
    'lossLimit',
    'strategy',
    'strategyVersion',
    'signal',
    'execution',
  ];
  if (Object.keys(v).some((k) => !allowed.includes(k)))
    throw new Error(
      'В файле есть неизвестные поля. Импортируйте только настройки, без ключей и секретов.',
    );
  if (typeof v.name !== 'string' || !v.name.trim() || v.name.trim().length > 40)
    throw new Error('Название должно содержать от 1 до 40 символов.');
  if (v.strategy !== 'signal')
    throw new Error('Поддерживается только бот «Сигнал лаборатории»: старые стратегии удалены.');
  return validateSignalBot(v);
}
const inRange = (n: unknown, min: number, max: number) =>
  typeof n === 'number' && Number.isFinite(n) && n >= min && n <= max;
function validateSignalBot(v: Record<string, unknown>): BotConfig {
  if (v.exchange !== 'Binance' || v.symbol !== '*')
    throw new Error('Бот сигнала торгует все монеты записи данных Binance.');
  if (!inRange(v.budget, 1, 1000000))
    throw new Error('Депозит бота должен быть от 1 до 1 000 000 USDT.');
  if (!inRange(v.lossLimit, 0.01, v.budget as number))
    throw new Error('Лимит убытка должен быть больше нуля и не больше депозита.');
  if (
    typeof v.strategyVersion !== 'string' ||
    !/^\d{1,4}\.\d{1,5}\.\d{1,6}$/.test(v.strategyVersion)
  )
    throw new Error('Неизвестная версия бота сигнала.');
  const s = v.signal as Record<string, unknown> | undefined;
  if (!s || typeof s !== 'object') throw new Error('Укажите настройки сигнала.');
  const p = { ...defaultSignalParameters, ...s } as SignalParameters;
  if (!strategies.some((x) => x.id === p.signal))
    throw new Error('Выберите стратегию: плотность или вынос цены.');
  if (!inRange(p.riskPct, 0.1, 5)) throw new Error('Риск на сделку — от 0,1 до 5% депозита.');
  if (!inRange(p.feePct, 0, 1)) throw new Error('Комиссии круга — от 0 до 1%.');
  if (!inRange(p.maxLeverage, 1, maxLeverage) || !Number.isInteger(p.maxLeverage))
    throw new Error(`Плечо — целое число от 1 до ${maxLeverage}.`);
  if (!inRange(p.maxPositions, 1, 20) || !Number.isInteger(p.maxPositions))
    throw new Error('Одновременных сделок — от 1 до 20.');
  if (!inRange(p.dailyLossPct, 1, 100))
    throw new Error('Дневной лимит убытка — от 1 до 100% начального депозита.');
  if (
    p.sessions != null &&
    (!Array.isArray(p.sessions) ||
      p.sessions.some((x) => !sessionIds.includes(x)) ||
      new Set(p.sessions).size !== p.sessions.length)
  )
    throw new Error('Неизвестная торговая сессия.');
  if (
    v.execution !== undefined &&
    !['simulation', 'testnet', 'live'].includes(v.execution as string)
  )
    throw new Error('Выберите, где торгует бот.');
  if (onExchange(v as { execution?: Execution })) {
    if (p.signal !== 'Плотность')
      throw new Error(
        'На бирже пока торгует только «Плотность»: у «Выноса цены» нет живого режима.',
      );
    if (v.budget !== 100)
      throw new Error('У бота на бирже депозит — баланс счёта; лимит убытка задаётся в процентах.');
  }
  return {
    name: (v.name as string).trim(),
    exchange: 'Binance',
    symbol: '*',
    budget: v.budget as number,
    lossLimit: v.lossLimit as number,
    strategy: 'signal',
    strategyVersion: v.strategyVersion,
    signal: {
      signal: p.signal,
      riskPct: p.riskPct,
      feePct: p.feePct,
      maxLeverage: p.maxLeverage,
      maxPositions: p.maxPositions,
      dailyLossPct: p.dailyLossPct,
      ...(p.sessions?.length ? { sessions: p.sessions } : {}),
    },
    ...(v.execution === undefined || v.execution === 'simulation'
      ? {}
      : { execution: v.execution as Execution }),
  };
}
export function exportConfig(bot: Bot): string {
  const {
    name,
    exchange,
    symbol,
    budget,
    lossLimit,
    strategy,
    strategyVersion,
    signal,
    execution,
  } = bot;
  return JSON.stringify(
    {
      schemaVersion: 1,
      config: {
        name,
        exchange,
        symbol,
        budget,
        lossLimit,
        strategy,
        strategyVersion,
        ...(signal ? { signal } : {}),
        ...(execution && execution !== 'simulation' ? { execution } : {}),
      },
    },
    null,
    2,
  );
}
export function importConfig(text: string): BotConfig {
  if (text.length > 32768) throw new Error('Файл настроек не должен превышать 32 КБ.');
  let data: unknown;
  try {
    data = JSON.parse(text);
  } catch {
    throw new Error('Не удалось прочитать JSON. Проверьте формат файла.');
  }
  if (!data || typeof data !== 'object' || Array.isArray(data))
    throw new Error('Неверный формат пакета.');
  const v = data as Record<string, unknown>;
  if (v.schemaVersion !== 1 || Object.keys(v).some((k) => !['schemaVersion', 'config'].includes(k)))
    throw new Error('Нужен пакет настроек версии 1, экспортированный из Trade Bot.');
  return validateConfig(v.config);
}
// Server data is checked too: a corrupt record must not crash the UI.
export function parseWorkspace(text: string): Workspace {
  const v = JSON.parse(text) as Workspace;
  if (
    v.schemaVersion !== 1 ||
    !Array.isArray(v.bots) ||
    !Array.isArray(v.events) ||
    v.bots.length > 100 ||
    v.events.length > 200
  )
    throw new Error('Invalid workspace');
  const ids = new Set<string>();
  for (const bot of v.bots) {
    if (!bot || typeof bot !== 'object') throw new Error('Invalid bot');
    const { name, exchange, symbol, budget, lossLimit, strategy, strategyVersion, signal } = bot;
    const checked = validateConfig({
      name,
      exchange,
      symbol,
      budget,
      lossLimit,
      strategy,
      strategyVersion,
      ...(signal ? { signal } : {}),
    });
    if (checked.signal) bot.signal = checked.signal;
    if (
      typeof bot.id !== 'string' ||
      (bot.unrealized != null &&
        (typeof bot.unrealized !== 'number' || !Number.isFinite(bot.unrealized))) ||
      ids.has(bot.id) ||
      !Object.hasOwn(statusLabels, bot.status) ||
      !Number.isFinite(Date.parse(bot.createdAt)) ||
      !Array.isArray(bot.trades) ||
      bot.trades.length > 10000
    )
      throw new Error('Invalid bot');
    ids.add(bot.id);
    for (const t of bot.trades)
      if (
        !t ||
        typeof t.id !== 'string' ||
        !['long', 'short'].includes(t.side) ||
        !Number.isFinite(Date.parse(t.at)) ||
        ![t.gross, t.fee, t.entry, t.exit, t.quantity].every(
          (n) => typeof n === 'number' && Number.isFinite(n),
        ) ||
        t.entry <= 0 ||
        t.exit <= 0 ||
        t.quantity <= 0 ||
        [t.symbol, t.exchange, t.exitReason, t.entryReason].some(
          (s) => s != null && typeof s !== 'string',
        ) ||
        (t.openedAt != null && !Number.isFinite(Date.parse(t.openedAt))) ||
        [t.holdSeconds, t.notional].some(
          (n) => n != null && (typeof n !== 'number' || !Number.isFinite(n) || n < 0),
        ) ||
        [t.maxFavorableBps, t.maxAdverseBps].some(
          (n) => n != null && (typeof n !== 'number' || !Number.isFinite(n)),
        )
      )
        throw new Error('Invalid trade');
  }
  for (const e of v.events)
    if (
      !e ||
      typeof e.id !== 'string' ||
      typeof e.botName !== 'string' ||
      (e.botId !== undefined && typeof e.botId !== 'string') ||
      typeof e.text !== 'string' ||
      !Number.isFinite(Date.parse(e.at))
    )
      throw new Error('Invalid event');
  return v;
}

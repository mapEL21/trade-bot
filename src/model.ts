export type Exchange = 'Binance' | 'Bybit' | 'OKX' | 'Hyperliquid';
export type Status =
  'running' | 'paused' | 'draft' | 'stopped' | 'calibrating' | 'stopping' | 'interrupted';
// Settings of the leader-lag strategy, the same fields as the recording check (ReplaySettings on the server).
export interface LeaderLagSettings {
  calibrationSeconds: number;
  impulseBps: number;
  windowMs: number;
  maxAgeMs: number;
  feeBps: number;
  slippageBps: number;
  minEdgeBps: number;
  stopLossBps: number;
  maxHoldMs: number;
  leaderAdvantageBps: number;
  // How far the leader must move back against the position to exit; the server treats a missing value as impulseBps.
  reversalBps: number;
  orderNotional: number;
  priceLimitBps: number;
  makerFeeBps: number;
  makerWaitMs: number;
  executionModel: 'depth' | 'bbo';
  entryOrder: 'taker' | 'maker';
  // A fixed size in coins; null means the size is orderNotional in USDT.
  orderQuantity: number | null;
  // source: the signal exchange leads, as set by the user; auto: the recording check picks the leader from the start
  // of the data. Bots always use source.
  leader: 'auto' | 'source';
}
// The bot trades (virtually) on its own exchange and follows the signal exchange, which the user sets as the leader.
export interface LeaderLagParameters {
  signalExchange: Exchange;
  settings: LeaderLagSettings;
  delayMs: number;
  counter: boolean;
  // Margin is nominal / leverage; the order nominal may be up to balance x leverage.
  leverage: number;
}
export const maxLeverage = 125;
// A bot that trades one signal of the lab on every coin of the data recording (SignalParameters on the server).
// riskPct: the share of the deposit lost at the stop, fees included; the size follows from it and the stop distance.
export interface SignalParameters {
  signal: string;
  mode: 'follow' | 'fade';
  holdSeconds: number;
  riskPct: number;
  // The stop is this many typical 1-minute ranges of the coin away from the entry.
  stopRanges: number;
  // A trade is skipped when the round-trip fees would be more than this share of its risk.
  maxFeeShare: number;
  feePct: number;
  maxLeverage: number;
  maxPositions: number;
  // Trading sessions the bot opens trades in (asia, europe, us, late); missing or empty means all hours.
  sessions?: string[];
}
export const sessionIds = ['asia', 'europe', 'us', 'late'];
export const signalTypes = [
  'Вынос цены',
  'Каскад ликвидаций',
  'Всплеск объёма',
  'Перекос агрессора',
  'Перекос стакана',
  'Пробой уровня',
  'Подход к плотности',
  'Проедание плотности',
  'Снятие плотности',
  'Переставление плотности',
];
export const signalVersion = '0.1.0';
// The hypothesis under check: follow a forced price jerk for 5 minutes, risking 1.5% of the deposit with fees.
export const defaultSignalParameters: SignalParameters = {
  signal: 'Вынос цены',
  mode: 'follow',
  holdSeconds: 300,
  riskPct: 1.5,
  stopRanges: 2,
  maxFeeShare: 0.3,
  feePct: 0.1,
  maxLeverage: 20,
  maxPositions: 3,
};
export interface BotConfig {
  name: string;
  exchange: Exchange;
  symbol: string;
  budget: number;
  lossLimit: number;
  strategy: 'impulse' | 'leader-lag' | 'signal';
  strategyVersion: string;
  parameters?: LeaderLagParameters;
  signal?: SignalParameters;
}
export interface Trade {
  id: string;
  at: string;
  side: 'long' | 'short';
  gross: number;
  fee: number;
  entry: number;
  exit: number;
  quantity: number;
  // Set by the server for trades of a live paper run: simulated, never a real execution.
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
  // Strategy-specific: the exchange that gave the signal in leader-lag.
  leader?: string;
}
export interface Bot extends BotConfig {
  revision?: number;
  id: string;
  status: Status;
  trades: Trade[];
  createdAt: string;
  // Result of the open simulated position if closed now, after fees; set by the server while a run holds one.
  unrealized?: number | null;
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
export const exchanges: Exchange[] = ['Binance', 'Bybit', 'OKX'];
// Exchanges the leader-lag research and simulation support; Hyperliquid is one of them, with no fixed role.
export const paperExchanges: Exchange[] = ['Binance', 'Bybit', 'OKX', 'Hyperliquid'];
export const statusLabels: Record<Status, string> = {
  running: 'Симуляция идёт',
  paused: 'На паузе',
  draft: 'Черновик',
  stopped: 'Остановлен',
  calibrating: 'Калибровка',
  stopping: 'Останавливается',
  interrupted: 'Прерван',
};
export const defaultLeaderLagSettings: LeaderLagSettings = {
  calibrationSeconds: 120,
  impulseBps: 3,
  windowMs: 250,
  maxAgeMs: 500,
  feeBps: 5,
  slippageBps: 1,
  minEdgeBps: 2,
  stopLossBps: 20,
  maxHoldMs: 2000,
  leaderAdvantageBps: 0.5,
  reversalBps: 3,
  orderNotional: 10,
  priceLimitBps: 5,
  makerFeeBps: 2,
  makerWaitMs: 1000,
  executionModel: 'depth',
  entryOrder: 'taker',
  orderQuantity: null,
  leader: 'source',
};
const settingKeys = Object.keys(defaultLeaderLagSettings) as (keyof LeaderLagSettings)[];
// Shape and obvious limits only; the server applies the full strategy validation.
function validateParameters(
  value: unknown,
  exchange: Exchange,
  budget: number,
): LeaderLagParameters {
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new Error('Укажите параметры стратегии.');
  const p = value as Record<string, unknown>;
  if (
    Object.keys(p).some(
      (k) => !['signalExchange', 'settings', 'delayMs', 'counter', 'leverage'].includes(k),
    )
  )
    throw new Error('В параметрах стратегии есть неизвестные поля.');
  if (!paperExchanges.includes(p.signalExchange as Exchange) || p.signalExchange === exchange)
    throw new Error('Биржа сигнала должна отличаться от биржи бота.');
  if (
    typeof p.delayMs !== 'number' ||
    !Number.isInteger(p.delayMs) ||
    p.delayMs < 1 ||
    p.delayMs > 1000
  )
    throw new Error('Задержка заявки — целое число от 1 до 1000 мс.');
  if (typeof p.counter !== 'boolean') throw new Error('Укажите направление входа.');
  // Bots saved before leverage existed trade at 1x.
  const leverage = p.leverage ?? 1;
  if (
    typeof leverage !== 'number' ||
    !Number.isInteger(leverage) ||
    leverage < 1 ||
    leverage > maxLeverage
  )
    throw new Error(`Плечо — целое число от 1 до ${maxLeverage}.`);
  const s = p.settings as Record<string, unknown> | null;
  if (
    !s ||
    typeof s !== 'object' ||
    Array.isArray(s) ||
    Object.keys(s).some((k) => !settingKeys.includes(k as keyof LeaderLagSettings))
  )
    throw new Error('Проверьте настройки стратегии.');
  const settings = { ...defaultLeaderLagSettings };
  for (const key of settingKeys) {
    const v = s[key];
    if (key === 'executionModel') {
      if (v !== 'depth' && v !== 'bbo') throw new Error('Модель исполнения: стакан или BBO.');
      settings.executionModel = v;
    } else if (key === 'entryOrder') {
      if (v !== 'taker' && v !== 'maker') throw new Error('Вход: taker или maker.');
      settings.entryOrder = v;
    } else if (key === 'leader') {
      // Saved before the leader was a setting, or picked automatically: a bot always follows its signal exchange.
      if (v !== undefined && v !== 'auto' && v !== 'source')
        throw new Error('Поводырь: auto или source.');
      settings.leader = 'source';
    } else if (key === 'reversalBps' && (v === null || v === undefined)) {
      // Saved before the reversal had its own threshold: it was the entry impulse, filled in after the loop.
      settings.reversalBps = NaN;
    } else if (key === 'orderQuantity') {
      if (v !== null && v !== undefined && (typeof v !== 'number' || !Number.isFinite(v) || v <= 0))
        throw new Error('Размер заявки в монетах должен быть больше нуля.');
      settings.orderQuantity = (v as number | null | undefined) ?? null;
    } else {
      if (typeof v !== 'number' || !Number.isFinite(v))
        throw new Error('Все настройки стратегии должны быть числами.');
      settings[key] = v;
    }
  }
  if (Number.isNaN(settings.reversalBps)) settings.reversalBps = settings.impulseBps;
  if (settings.orderQuantity === null && settings.orderNotional > budget * leverage)
    throw new Error('Номинал заявки не может превышать выделенный баланс, умноженный на плечо.');
  return {
    signalExchange: p.signalExchange as Exchange,
    settings,
    delayMs: p.delayMs,
    counter: p.counter,
    leverage,
  };
}
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
    'parameters',
    'signal',
  ];
  if (Object.keys(v).some((k) => !allowed.includes(k)))
    throw new Error(
      'В файле есть неизвестные поля. Импортируйте только настройки, без ключей и секретов.',
    );
  if (typeof v.name !== 'string' || !v.name.trim() || v.name.trim().length > 40)
    throw new Error('Название должно содержать от 1 до 40 символов.');
  if (v.strategy === 'signal') return validateSignalBot(v);
  const leaderLag = v.strategy === 'leader-lag';
  if (!(leaderLag ? paperExchanges : exchanges).includes(v.exchange as Exchange))
    throw new Error(
      leaderLag
        ? 'Выберите Binance, Bybit, OKX или Hyperliquid.'
        : 'Выберите Binance, Bybit или OKX.',
    );
  if (
    typeof v.symbol !== 'string' ||
    v.symbol !== v.symbol.trim() ||
    // The same tickers the common pair list offers (1-20 characters, e.g. W or T), so every listed pair can be saved.
    !/^[A-Z0-9]{1,20}USDT$/.test(v.symbol)
  )
    throw new Error('Укажите символ USDT-фьючерса, например ARBUSDT.');
  if (
    typeof v.budget !== 'number' ||
    !Number.isFinite(v.budget) ||
    v.budget < 1 ||
    v.budget > 1000000
  )
    throw new Error('Выделенный баланс должен быть от 1 до 1 000 000 USDT.');
  if (
    typeof v.lossLimit !== 'number' ||
    !Number.isFinite(v.lossLimit) ||
    v.lossLimit <= 0 ||
    v.lossLimit > v.budget
  )
    throw new Error('Лимит убытка должен быть больше нуля и не больше баланса.');
  if (leaderLag) {
    if (
      typeof v.strategyVersion !== 'string' ||
      !/^\d{1,4}\.\d{1,5}\.\d{1,6}$/.test(v.strategyVersion)
    )
      throw new Error('Неизвестная версия стратегии «Поводырь».');
    return {
      name: v.name.trim(),
      exchange: v.exchange as Exchange,
      symbol: v.symbol,
      budget: v.budget,
      lossLimit: v.lossLimit,
      strategy: 'leader-lag',
      strategyVersion: v.strategyVersion,
      parameters: validateParameters(v.parameters, v.exchange as Exchange, v.budget),
    };
  }
  if (
    v.strategy !== 'impulse' ||
    v.strategyVersion !== '0.1.0' ||
    (v.parameters !== undefined && v.parameters !== null)
  )
    throw new Error('Эта версия интерфейса поддерживает шаблоны «Импульс» 0.1.0 и «Поводырь».');
  return {
    name: v.name.trim(),
    exchange: v.exchange as Exchange,
    symbol: v.symbol,
    budget: v.budget,
    lossLimit: v.lossLimit,
    strategy: 'impulse',
    strategyVersion: '0.1.0',
  };
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
  if (!signalTypes.includes(p.signal)) throw new Error('Выберите сигнал из лаборатории.');
  if (p.mode !== 'follow' && p.mode !== 'fade')
    throw new Error('Направление — по движению или против.');
  if (!inRange(p.holdSeconds, 5, 300) || !Number.isInteger(p.holdSeconds))
    throw new Error('Удержание — целое число секунд от 5 до 300.');
  if (!inRange(p.riskPct, 0.1, 5)) throw new Error('Риск на сделку — от 0,1 до 5% депозита.');
  if (!inRange(p.stopRanges, 0.5, 20))
    throw new Error('Стоп — от 0,5 до 20 обычных минутных свечей.');
  if (!inRange(p.maxFeeShare, 0.05, 0.9)) throw new Error('Доля комиссии в риске — от 5 до 90%.');
  if (!inRange(p.feePct, 0, 1)) throw new Error('Комиссии круга — от 0 до 1%.');
  if (!inRange(p.maxLeverage, 1, maxLeverage) || !Number.isInteger(p.maxLeverage))
    throw new Error(`Плечо — целое число от 1 до ${maxLeverage}.`);
  if (!inRange(p.maxPositions, 1, 20) || !Number.isInteger(p.maxPositions))
    throw new Error('Одновременных сделок — от 1 до 20.');
  if (
    p.sessions != null &&
    (!Array.isArray(p.sessions) ||
      p.sessions.some((x) => !sessionIds.includes(x)) ||
      new Set(p.sessions).size !== p.sessions.length)
  )
    throw new Error('Неизвестная торговая сессия.');
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
      mode: p.mode,
      holdSeconds: p.holdSeconds,
      riskPct: p.riskPct,
      stopRanges: p.stopRanges,
      maxFeeShare: p.maxFeeShare,
      feePct: p.feePct,
      maxLeverage: p.maxLeverage,
      maxPositions: p.maxPositions,
      ...(p.sessions?.length ? { sessions: p.sessions } : {}),
    },
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
    parameters,
    signal,
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
        ...(parameters ? { parameters } : {}),
        ...(signal ? { signal } : {}),
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
    const {
      name,
      exchange,
      symbol,
      budget,
      lossLimit,
      strategy,
      strategyVersion,
      parameters,
      signal,
    } = bot;
    const checked = validateConfig({
      name,
      exchange,
      symbol,
      budget,
      lossLimit,
      strategy,
      strategyVersion,
      ...(parameters ? { parameters } : {}),
      ...(signal ? { signal } : {}),
    });
    if (checked.signal) bot.signal = checked.signal;
    // Older records lack later settings (leverage, reversal threshold): use the filled-in values.
    if (checked.parameters) bot.parameters = checked.parameters;
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
        [t.symbol, t.exchange, t.leader, t.exitReason].some(
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

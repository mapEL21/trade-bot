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
  orderNotional: number;
  priceLimitBps: number;
  makerFeeBps: number;
  makerWaitMs: number;
  executionModel: 'depth' | 'bbo';
  entryOrder: 'taker' | 'maker';
  // A fixed size in coins; null means the size is orderNotional in USDT.
  orderQuantity: number | null;
}
// The bot trades (virtually) on its own exchange; the signal exchange must turn out to be the leader.
export interface LeaderLagParameters {
  signalExchange: Exchange;
  settings: LeaderLagSettings;
  delayMs: number;
  counter: boolean;
  // Margin is nominal / leverage; the order nominal may be up to balance x leverage.
  leverage: number;
}
export const maxLeverage = 125;
export interface BotConfig {
  name: string;
  exchange: Exchange;
  symbol: string;
  budget: number;
  lossLimit: number;
  strategy: 'impulse' | 'leader-lag';
  strategyVersion: string;
  parameters?: LeaderLagParameters;
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
}
export interface Bot extends BotConfig {
  revision?: number;
  id: string;
  status: Status;
  trades: Trade[];
  createdAt: string;
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
  orderNotional: 10,
  priceLimitBps: 5,
  makerFeeBps: 2,
  makerWaitMs: 1000,
  executionModel: 'depth',
  entryOrder: 'taker',
  orderQuantity: null,
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
  ];
  if (Object.keys(v).some((k) => !allowed.includes(k)))
    throw new Error(
      'В файле есть неизвестные поля. Импортируйте только настройки, без ключей и секретов.',
    );
  if (typeof v.name !== 'string' || !v.name.trim() || v.name.trim().length > 40)
    throw new Error('Название должно содержать от 1 до 40 символов.');
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
    !/^[A-Z0-9]{2,16}USDT$/.test(v.symbol)
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
export function exportConfig(bot: Bot): string {
  const { name, exchange, symbol, budget, lossLimit, strategy, strategyVersion, parameters } = bot;
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
    const { name, exchange, symbol, budget, lossLimit, strategy, strategyVersion, parameters } =
      bot;
    validateConfig({
      name,
      exchange,
      symbol,
      budget,
      lossLimit,
      strategy,
      strategyVersion,
      ...(parameters ? { parameters } : {}),
    });
    if (
      typeof bot.id !== 'string' ||
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
        t.quantity <= 0
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

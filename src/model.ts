export type Exchange = 'Binance' | 'Bybit' | 'OKX';
export type Status = 'running' | 'paused' | 'draft' | 'stopped';
export interface BotConfig {
  name: string;
  exchange: Exchange;
  symbol: string;
  budget: number;
  lossLimit: number;
  strategy: 'impulse';
  strategyVersion: '0.1.0';
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
}
export interface Bot extends BotConfig {
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
export const STORAGE_KEY = 'trade-bot.workspace.v1';
export const DEMO_NOW = Date.parse('2026-10-07T18:00:00Z');
export const exchanges: Exchange[] = ['Binance', 'Bybit', 'OKX'];
export const statusLabels: Record<Status, string> = {
  running: 'Демо запущено',
  paused: 'На паузе',
  draft: 'Черновик',
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
export function periodTrades(trades: Trade[], days: number) {
  return trades.filter(
    (t) => Date.parse(t.at) >= DEMO_NOW - days * 86400000 && Date.parse(t.at) <= DEMO_NOW,
  );
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
  ];
  if (Object.keys(v).some((k) => !allowed.includes(k)))
    throw new Error(
      'В файле есть неизвестные поля. Импортируйте только настройки, без ключей и секретов.',
    );
  if (typeof v.name !== 'string' || !v.name.trim() || v.name.trim().length > 40)
    throw new Error('Название должно содержать от 1 до 40 символов.');
  if (!exchanges.includes(v.exchange as Exchange))
    throw new Error('Выберите Binance, Bybit или OKX.');
  if (typeof v.symbol !== 'string' || !/^[A-Z0-9]{2,16}USDT$/.test(v.symbol))
    throw new Error('Укажите символ USDT-фьючерса, например ARBUSDT.');
  if (
    typeof v.budget !== 'number' ||
    !Number.isFinite(v.budget) ||
    v.budget < 1 ||
    v.budget > 1000000
  )
    throw new Error('Демо-баланс должен быть от 1 до 1 000 000 USDT.');
  if (
    typeof v.lossLimit !== 'number' ||
    !Number.isFinite(v.lossLimit) ||
    v.lossLimit <= 0 ||
    v.lossLimit > v.budget
  )
    throw new Error('Лимит убытка должен быть больше нуля и не больше баланса.');
  if (v.strategy !== 'impulse' || v.strategyVersion !== '0.1.0')
    throw new Error('Эта версия интерфейса поддерживает шаблон «Импульс» 0.1.0.');
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
  const { name, exchange, symbol, budget, lossLimit, strategy, strategyVersion } = bot;
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
export function makeBot(config: BotConfig): Bot {
  return {
    ...validateConfig(config),
    id: crypto.randomUUID(),
    createdAt: new Date().toISOString(),
    status: 'draft',
    trades: [],
  };
}
export function createDemo(): Workspace {
  const values = [
    0.05, 0.09, -0.03, 0.07, 0.1, -0.04, 0.06, 0.08, 0.04, -0.02, 0.11, 0.07, -0.03, 0.06, 0.09,
    0.04,
  ];
  const trades: Trade[] = values.map((gross, i) => {
    const entry = 0.4,
      quantity = 10,
      side = i % 3 === 0 ? 'short' : 'long';
    return {
      id: `demo-trade-${i}`,
      at: new Date(DEMO_NOW - (values.length - 1 - i) * 12 * 3600000).toISOString(),
      side,
      gross,
      fee: 0.004,
      entry,
      exit: entry + (gross / quantity) * (side === 'long' ? 1 : -1),
      quantity,
    };
  });
  return {
    schemaVersion: 1,
    bots: [
      {
        id: 'demo-impulse',
        name: 'Импульс · ARB',
        exchange: 'Binance',
        symbol: 'ARBUSDT',
        budget: 10,
        lossLimit: 1,
        strategy: 'impulse',
        strategyVersion: '0.1.0',
        status: 'paused',
        trades,
        createdAt: '2026-09-30T00:00:00Z',
      },
    ],
    events: [
      {
        id: 'welcome',
        at: new Date(DEMO_NOW).toISOString(),
        botName: 'Рабочее пространство',
        text: 'Загружен демонстрационный пример. Все сделки и цены вымышлены.',
      },
    ],
  };
}
// Stored data is untrusted too: a corrupt record must not crash the UI.
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
    const { name, exchange, symbol, budget, lossLimit, strategy, strategyVersion } = bot;
    validateConfig({
      name,
      exchange,
      symbol,
      budget,
      lossLimit,
      strategy,
      strategyVersion,
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
        t.fee < 0 ||
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

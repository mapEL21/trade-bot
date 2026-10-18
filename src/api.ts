import {
  parseWorkspace,
  type Bot,
  type BotConfig,
  type SignalParameters,
  type Trade,
  type Workspace,
} from './model';

export interface StrategyDefinition {
  id: string;
  version: string;
  name: string;
  description: string;
  implementationStatus: 'planned' | 'implemented' | 'paper';
  canCreate: boolean;
  canRun: boolean;
  exchanges: string[];
}

export class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
  ) {
    super(message);
  }
}

export async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`/api/v1${path}`, {
      ...init,
      signal: init.signal ?? AbortSignal.timeout(10000),
      headers: { 'Content-Type': 'application/json', 'X-TradeBot-Client': 'web', ...init.headers },
    });
  } catch {
    throw new ApiError(
      'Сервер недоступен или ответ не получен. Обновите данные перед повтором: изменение могло сохраниться.',
      0,
    );
  }
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    const messages = Object.values(problem.errors ?? {})
      .flat()
      .join(' ');
    throw new ApiError(
      messages || problem.title || 'Сервер не смог выполнить запрос. Обновите данные и повторите.',
      response.status,
    );
  }
  if (response.status === 204) return undefined as T;
  // A command answered without a body (202 Accepted, for example) has nothing to parse.
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

// Data recorder for the signal lab: public Binance data of many coins; it never trades.
export interface RecordingSymbol {
  symbol: string;
  quoteVolume: number;
  trades: number;
  liquidations: number;
  // 24-hour range (high - low) / price, % at the start of the recording.
  rangePct: number;
}
export interface ScanSnapshot {
  id: string | null;
  state: 'idle' | 'starting' | 'scanning' | 'finished' | 'stopped' | 'failed' | 'no-data';
  explanation: string;
  startedUtc: string | null;
  seconds: number;
  events: number;
  bytes: number;
  symbols: RecordingSymbol[];
  continuous?: boolean;
  segment?: number;
}
// Signal lab: events found in the scanner recordings and what trading on them would have made after fees.
export interface LabStatus {
  state: 'idle' | 'running' | 'finished' | 'stopped';
  explanation: string;
  done: number;
  total: number;
  lines: number;
  current: string | null;
}
export interface LabRow {
  type: string;
  mode: 'follow' | 'fade';
  horizon: number;
  count: number;
  meanPct: number;
  winRate: number;
  firstHalf: number | null;
  secondHalf: number | null;
  stable: boolean;
  // Independent market moments: events of one type and direction within 10 s on any coins count once.
  episodes: number;
}
// The pre-registered hypothesis and where its check on new data stands.
export type Verdict = 'collecting' | 'confirmed' | 'rejected';
export interface HypothesisSignal {
  type: string;
  mode: 'follow' | 'fade';
  horizon: number;
  episodes: number;
  needed: number;
  meanPct: number | null;
  winRate: number | null;
  firstHalf: number | null;
  secondHalf: number | null;
  verdict: Verdict;
}
export interface HypothesisCheck {
  statement: string;
  fromMs: number;
  feePct: number;
  hours: number;
  signals: HypothesisSignal[];
  verdict: Verdict;
  explanation: string;
  number?: number;
  // Earlier hypotheses and how they ended.
  previous?: HypothesisCheck[] | null;
}
// Trading sessions by the UTC hour (TradingSessions on the server).
export const tradingSessions: { id: string; name: string }[] = [
  { id: 'asia', name: 'Азия · 03:00–11:00 МСК' },
  { id: 'europe', name: 'Европа · 11:00–16:00 МСК' },
  { id: 'us', name: 'США и Европа · 16:00–00:00 МСК' },
  { id: 'late', name: 'Тихие часы · 00:00–03:00 МСК' },
];
export interface LabSessionRow {
  session: string;
  name: string;
  episodes: number;
  meanPct: number | null;
  winRate: number | null;
  hours: number;
}
// How often the spread alone would pay two maker fees (4 bp), per coin.
export interface LabSpreadRow {
  symbol: string;
  snapshots: number;
  meanBps: number;
  above4Pct: number;
  above10Pct: number;
  recordings: number;
}
export interface LabReport {
  recordings: number;
  hours: number;
  events: number;
  feePct: number;
  counts: { type: string; count: number; perHour: number; episodes: number }[];
  fromMs: number | null;
  session: string | null;
  rows: LabRow[];
  notes: string[];
}
// Disk upkeep on a server: compression, retention and database copies; off on a laptop.
export interface StorageStatus {
  enabled: boolean;
  checkedUtc: string | null;
  recordings: number;
  recordingBytes: number;
  retentionDays: number;
  freeBytes: number | null;
  totalBytes: number | null;
  lastBackup: string | null;
  error: string | null;
}

// Density screener: walls in the order books of every Binance USDT perpetual and what happened to them.
export interface ScreenerWall {
  symbol: string;
  side: number;
  price: number;
  quote: number;
  peak: number;
  ratio: number;
  distancePct: number;
  ticks: number;
  tickSize: number;
  firstMs: number;
  seenMs: number;
  status: 'stands' | 'near' | 'eating' | 'eaten' | 'pulled';
  // Minutes of the coin's average trading it would take to eat the wall.
  minutes: number;
}
export interface ScreenerSnapshot {
  state: string;
  explanation: string;
  markets: number;
  rounds: number;
  roundMs: number | null;
  roundSeconds: number | null;
  walls: ScreenerWall[];
  finished: ScreenerWall[];
  day: { found: number; eaten: number; pulled: number; medianMinutes: number | null };
}
export const api = {
  screener: () => request<ScreenerSnapshot>('/screener'),
  startScreener: () => request<void>('/screener/start', { method: 'POST' }),
  stopScreener: () => request<void>('/screener/stop', { method: 'POST' }),
  scanner: (signal?: AbortSignal) => request<ScanSnapshot>('/scanner', { signal }),
  labStatus: (signal?: AbortSignal) => request<LabStatus>('/lab/status', { signal }),
  runLab: () => request<void>('/lab/run', { method: 'POST' }),
  labHypothesis: () => request<HypothesisCheck>('/lab/hypothesis'),
  labReport: (feePct: number, fromMs: number | null, session: string | null = null) =>
    request<LabReport>(
      `/lab/report?feePct=${encodeURIComponent(feePct)}${fromMs !== null ? `&fromMs=${fromMs}` : ''}${session ? `&session=${session}` : ''}`,
    ),
  labSessions: (row: LabRow, feePct: number, fromMs: number | null) =>
    request<LabSessionRow[]>(
      `/lab/sessions?type=${encodeURIComponent(row.type)}&mode=${row.mode}&horizon=${row.horizon}&feePct=${encodeURIComponent(feePct)}${fromMs !== null ? `&fromMs=${fromMs}` : ''}`,
    ),
  labSpreads: (fromMs: number | null) =>
    request<LabSpreadRow[]>(`/lab/spreads${fromMs !== null ? `?fromMs=${fromMs}` : ''}`),
  labTrades: (row: LabRow, feePct: number, fromMs: number | null) =>
    request<Trade[]>(
      `/lab/trades?type=${encodeURIComponent(row.type)}&mode=${row.mode}&horizon=${row.horizon}&feePct=${encodeURIComponent(feePct)}${fromMs !== null ? `&fromMs=${fromMs}` : ''}`,
    ),
  storage: (signal?: AbortSignal) => request<StorageStatus>('/storage', { signal }),
  startScanner: (options: {
    seconds: number;
    minQuoteVolume: number;
    maxSymbols: number;
    exclude: string[];
    continuous: boolean;
    minRangePct: number;
  }) => request<{ id: string }>('/scanner', { method: 'POST', body: JSON.stringify(options) }),
  stopScanner: (id: string) => request<void>(`/scanner/${id}/stop`, { method: 'POST' }),
  setScannerContinuous: (id: string, continuous: boolean) =>
    request<ScanSnapshot>(`/scanner/${id}/continuous`, {
      method: 'POST',
      body: JSON.stringify({ continuous }),
    }),
  workspace: async (): Promise<Workspace> =>
    parseWorkspace(JSON.stringify(await request('/workspace'))),
  strategies: () => request<StrategyDefinition[]>('/strategies'),
  create: (config: BotConfig) =>
    request<Bot>('/bots', { method: 'POST', body: JSON.stringify(config) }),
  update: (bot: Bot, config: BotConfig) =>
    request<Bot>(`/bots/${bot.id}`, {
      method: 'PUT',
      headers: { 'If-Match': `"${bot.revision}"` },
      body: JSON.stringify(config),
    }),
  remove: (bot: Bot) =>
    request<void>(`/bots/${bot.id}`, {
      method: 'DELETE',
      headers: { 'If-Match': `"${bot.revision}"` },
    }),
  signalRuns: (bot: Bot, signal?: AbortSignal) =>
    request<SignalRunView[]>(`/bots/${bot.id}/signal`, { signal }),
  // fromMs: also replay the recordings since that moment; without it the bot trades from now on.
  // Where the bot trades is its own setting (execution); a run only chooses when it starts.
  startSignal: (bot: Bot, fromMs: number | null) =>
    request<SignalRun>(`/bots/${bot.id}/signal/start`, {
      method: 'POST',
      body: JSON.stringify(fromMs === null ? {} : { fromMs }),
    }),
  exchangeKeys: () => request<ExchangeKeys>('/exchange/keys'),
  saveExchangeKeys: (mode: string, apiKey: string, apiSecret: string) =>
    request<{ balance: number }>('/exchange/keys', {
      method: 'POST',
      body: JSON.stringify({ mode, apiKey, apiSecret }),
    }),
  deleteExchangeKeys: (mode: string) =>
    request<void>(`/exchange/keys/${mode}`, { method: 'DELETE' }),
  exchangeBalance: (mode = 'testnet') => request<ExchangeBalance>(`/exchange/balance/${mode}`),
  exchangeLog: (bot: Bot) => request<ExchangeLogEntry[]>(`/bots/${bot.id}/exchange`),
  stopSignal: (bot: Bot) => request<void>(`/bots/${bot.id}/signal/stop`, { method: 'POST' }),
};

// A signal bot run follows the processed recordings from fromMs; trades are computed from them.
export interface SignalRun {
  id: string;
  botId: string;
  fromMs: number;
  toMs: number | null;
  startedUtc: string;
  budget: number;
  lossLimit: number;
  parameters: SignalParameters;
  // A density run started "from now" trades the live stream of the coins the screener points to.
  live?: boolean;
  execution?: 'simulation' | 'testnet' | 'live';
}
export interface ExchangeKeys {
  testnet: { mode: string; configured: boolean; keyEnd: string | null; savedUtc: string | null };
  liveAllowed: boolean;
}
export interface ExchangeBalance {
  wallet: number;
  available: number;
  unrealized: number;
  atMs: number;
}
export interface ExchangeLogEntry {
  runId: string;
  atMs: number;
  symbol: string;
  action: string;
  side: string;
  quantity: number;
  price: number;
  clientId: string;
  status: string;
  message: string;
}
// What a live run watches now and the positions it holds (mark: the exit-side best price).
export interface LiveView {
  watching: { symbol: string; side: number; wall: number; distancePct: number }[];
  open: {
    symbol: string;
    side: 'long' | 'short';
    openedMs: number;
    entry: number;
    mark: number;
    fraction: number;
    entryReason: string;
  }[];
  segment: string | null;
  // Testnet runs: positions on the exchange, and why orders cannot be sent when they cannot.
  exchange?: {
    symbol: string;
    side: 'long' | 'short';
    quantity: number;
    entry: number;
    stopPrice: number;
    openedMs: number;
  }[];
  exchangeProblem?: string | null;
}
export interface SignalRunView {
  run: SignalRun;
  state: 'running' | 'stopped';
  result: {
    trades: Trade[];
    skipped: Record<string, number>;
    signals: number;
    incomplete: number;
    deposit: number;
    recordings: number;
    lastDataMs: number | null;
    entriesBlocked: string | null;
    // Recordings still being computed in the background.
    pending: number;
    unrealized: number | null;
  };
  live: LiveView | null;
}

import { parseWorkspace, type Bot, type BotConfig, type Workspace } from './model';

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

export interface ResearchMarketList {
  exchange: string;
  sourceExchange?: string;
  updatedUtc: string;
  instruments: { coin: string; symbol: string }[];
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
  if (response.status === 204 || response.headers.get('content-length') === '0')
    return undefined as T;
  return response.json();
}

export const api = {
  researchMarkets: (exchange: string, sourceExchange: string, signal: AbortSignal) =>
    request<ResearchMarketList>(
      `/research/markets?exchange=${encodeURIComponent(exchange)}&sourceExchange=${encodeURIComponent(sourceExchange)}`,
      {
        signal: AbortSignal.any([signal, AbortSignal.timeout(10000)]),
      },
    ),
  startResearch: (config: {
    coin: string;
    seconds: number;
    maxMb: number;
    exchange: string;
    sourceExchange: string;
  }) => request<{ id: string }>('/research', { method: 'POST', body: JSON.stringify(config) }),
  stopResearch: (id: string) => request<void>(`/research/${id}/stop`, { method: 'POST' }),
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
  // Live paper simulation of a leader-lag bot: nothing is sent to an exchange.
  startPaper: (bot: Bot, seconds: number) =>
    request<{ id: string }>(`/bots/${bot.id}/start`, {
      method: 'POST',
      body: JSON.stringify({ seconds }),
    }),
  pausePaper: (bot: Bot, paused: boolean) =>
    request<void>(`/bots/${bot.id}/${paused ? 'pause' : 'resume'}`, { method: 'POST' }),
  stopPaper: (bot: Bot, force: boolean) =>
    request<void>(`/bots/${bot.id}/stop${force ? '?force=true' : ''}`, { method: 'POST' }),
  paper: (signal?: AbortSignal) => request<PaperSnapshot>('/paper', { signal }),
  paperRuns: (bot: Bot, signal?: AbortSignal) =>
    request<PaperRunRecord[]>(`/bots/${bot.id}/runs`, { signal }),
};

export interface PaperDecision {
  seconds: number;
  action: string;
  reason: string;
  side: string | null;
  price: number | null;
  netBps: number | null;
  quantity?: number | null;
}
export interface PaperTrade {
  entrySeconds: number;
  exitSeconds: number;
  side: string;
  entryPrice: number;
  exitPrice: number;
  netBps: number;
  exitReason: string;
  quantity?: number | null;
  netQuote?: number | null;
  feesQuote?: number | null;
}
export interface PaperSnapshot {
  id: string | null;
  state: string;
  explanation: string;
  coin: string | null;
  sourceExchange: string | null;
  exchange: string | null;
  settings: { calibrationSeconds: number; executionModel: string; entryOrder?: string } | null;
  delayMs: number;
  counter: boolean;
  sessionSeconds: number;
  leader: string | null;
  executionExchange: string | null;
  normalBasisBps: number | null;
  decisions: PaperDecision[];
  trades: PaperTrade[];
  decided: number;
  sumNetBps: number;
  closedNetQuote: number | null;
  lateEvents: number;
  result: { unresolvedPositions: number; trades: number } | null;
  version: string;
  executionEnabled: boolean;
  botId: string | null;
  paused: boolean;
  stopping: boolean;
  entriesBlocked: string | null;
  positionOpen: boolean;
}
export interface PaperRunRecord {
  id: string;
  version: string;
  startedUtc: string;
  endedUtc: string | null;
  state: string;
  explanation: string;
  coin: string;
  signalExchange: string;
  exchange: string;
  delayMs: number;
  counter: boolean;
  leader: string | null;
  executionExchange: string | null;
  trades: number;
  sumNetBps: number;
  closedNetQuote: number | null;
  unresolved: number;
  lateEvents: number;
  tradeLog: PaperTrade[];
  journal: PaperDecision[];
}

import { useEffect, useId, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { ExchangeBalanceCard } from './ExchangePanel';
import {
  Activity,
  ArrowLeft,
  ArrowRight,
  ArrowUpRight,
  Bot as BotIcon,
  Clock3,
  Copy,
  FlaskConical,
  FolderInput,
  LayoutDashboard,
  ListFilter,
  Menu,
  Pause,
  Play,
  Plus,
  Radio,
  ScanSearch,
  Search,
  ShieldCheck,
  Trash2,
  X,
  Zap,
} from 'lucide-react';
import { api, type StrategyDefinition } from './api';
import { Scanner } from './Scanner';
import { Laboratory } from './SignalLab';
import { strategyView } from './strategyViews';
import { SignalFields } from './SignalBot';
import { DownloadButton, Empty, Modal, Toast, download } from './components';
import { Performance, Stats, TradeQuality, TradesTable } from './TradeAnalysis';
import {
  exportConfig,
  formatMoney,
  importConfig,
  metrics,
  periodTrades,
  defaultSignalParameters,
  signalVersion,
  statusLabels,
  onExchange,
  validateConfig,
  type Bot,
  type BotConfig,
  type Execution,
  type Status,
  type Workspace,
} from './model';

const emptyWorkspace: Workspace = { schemaVersion: 1, bots: [], events: [] };

type Page = 'overview' | 'bots' | 'scanner';
type Tab = 'overview' | 'trades' | 'settings' | 'events';
const nav: { id: Page; label: string; icon: typeof BotIcon }[] = [
  { id: 'overview', label: 'Обзор', icon: LayoutDashboard },
  { id: 'bots', label: 'Боты', icon: BotIcon },
  { id: 'scanner', label: 'Лаборатория', icon: ScanSearch },
];
// A new bot trades a lab signal: 100 USDT deposit, 1.5% risk per trade with fees, a 30 USDT loss limit.
const defaultConfig: BotConfig = {
  name: '',
  exchange: 'Binance',
  symbol: '*',
  budget: 100,
  lossLimit: 30,
  strategy: 'signal',
  strategyVersion: signalVersion,
  signal: defaultSignalParameters,
};
// A running bot is always a live paper simulation, never real trading.
const statusLabel = (status: Status) => statusLabels[status];
const strategyName = (bot: BotConfig) => ({ signal: 'Сигнал лаборатории' })[bot.strategy];
// The pair line of a bot: a signal bot trades every coin of the data recording.
const marketLabel = (bot: BotConfig) =>
  bot.symbol === '*' ? 'Все монеты записи' : bot.symbol.replace('USDT', ' / USDT');
export default function App() {
  const [strategies, setStrategies] = useState<StrategyDefinition[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const mutation = useRef(false);
  const [workspace, setWorkspace] = useState<Workspace>(emptyWorkspace);
  const [serverError, setServerError] = useState('');
  const [page, setPage] = useState<Page>('overview');
  const [selected, setSelected] = useState<string | null>(null);
  const [tab, setTab] = useState<Tab>('overview');
  const [days, setDays] = useState(7);
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState('all');
  const [mobile, setMobile] = useState(false);
  const [editor, setEditor] = useState<{
    config: BotConfig;
    id?: string;
  } | null>(null);
  const [importing, setImporting] = useState(false);
  const [confirm, setConfirm] = useState<{ type: 'delete'; id: string } | null>(null);
  const [toast, setToast] = useState('');
  const toastTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const bot = workspace.bots.find((b) => b.id === selected);
  const allTrades = workspace.bots.flatMap((b) => b.trades.map((t) => ({ ...t, botName: b.name })));
  const now = Date.now();
  const trades = periodTrades(bot?.trades ?? allTrades, days, now);
  const stat = metrics(trades);
  // A bot's deposit is that of its latest run (for a bot on an exchange, the account balance at the start).
  const depositOf = (b: Bot) => b.deposit ?? b.budget;
  const budget = bot ? depositOf(bot) : workspace.bots.reduce((s, b) => s + depositOf(b), 0);
  // Allocated balance plus every closed simulated trade so far, whatever the period shown.
  const balance = budget + metrics(bot?.trades ?? allTrades).pnl;
  // Open simulated positions right now: the bot's, or the sum over all bots on the overview.
  const open = (bot ? [bot] : workspace.bots).flatMap((b) =>
    b.unrealized != null ? [b.unrealized] : [],
  );
  const unrealized = open.length ? open.reduce((a, b) => a + b, 0) : null;
  const visibleBots = workspace.bots.filter(
    (b) =>
      `${b.name} ${b.symbol} ${b.exchange}`.toLowerCase().includes(query.toLowerCase()) &&
      (filter === 'all' || b.status === filter),
  );
  useEffect(
    () => () => {
      if (toastTimer.current) clearTimeout(toastTimer.current);
    },
    [],
  );
  useEffect(() => {
    let active = true;
    Promise.all([api.workspace(), api.strategies()])
      .then(([data, catalog]) => {
        if (active) {
          setWorkspace(data);
          setStrategies(catalog);
          setServerError('');
        }
      })
      .catch((error) => {
        if (active) setServerError(error.message);
      })
      .finally(() => {
        if (active) setLoading(false);
      });
    return () => {
      active = false;
    };
  }, []);
  // While a simulation runs, keep bot statuses current without the loading indicator.
  const anyActive = workspace.bots.some((b) => b.status === 'running');
  const demoRunning = workspace.bots.some((b) => b.status === 'running' && onExchange(b));
  // What of the Laboratory works right now (data recording, processing of recordings, the density screener), for the
  // indicator next to it in the menu.
  const [labWork, setLabWork] = useState<string[]>([]);
  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    async function poll() {
      const [scanner, lab, screener] = await Promise.allSettled([
        api.scanner(),
        api.labStatus(),
        api.screener(),
      ]);
      const work: string[] = [];
      if (scanner.status === 'fulfilled' && ['starting', 'scanning'].includes(scanner.value.state))
        work.push('запись данных');
      if (lab.status === 'fulfilled' && lab.value.state === 'running')
        work.push('обработка записей');
      if (screener.status === 'fulfilled' && ['starting', 'running'].includes(screener.value.state))
        work.push('скринер плотностей');
      if (!stopped) setLabWork(work);
      if (!stopped) timer = setTimeout(poll, 10000);
    }
    void poll();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, []);
  const live: Partial<Record<Page, { on: boolean; text: string }>> = {
    bots: anyActive
      ? { on: true, text: 'Есть работающие боты' }
      : { on: false, text: 'Ни один бот не запущен' },
    scanner: labWork.length
      ? { on: true, text: `Работает: ${labWork.join(', ')}` }
      : { on: false, text: 'Ничего не запущено' },
  };
  useEffect(() => {
    if (!anyActive) return;
    const timer = setInterval(() => {
      if (!mutation.current)
        void api
          .workspace()
          .then(setWorkspace)
          .catch(() => {});
    }, 5000);
    return () => clearInterval(timer);
  }, [anyActive]);
  function quietRefresh() {
    void api
      .workspace()
      .then(setWorkspace)
      .catch((error) => setServerError((error as Error).message));
  }
  async function refreshServer() {
    setLoading(true);
    try {
      const [data, catalog] = await Promise.all([api.workspace(), api.strategies()]);
      setWorkspace(data);
      setStrategies(catalog);
      setServerError('');
    } catch (error) {
      setServerError((error as Error).message);
      throw error;
    } finally {
      setLoading(false);
    }
  }
  function notify(message: string) {
    setToast(message);
    if (toastTimer.current) clearTimeout(toastTimer.current);
    toastTimer.current = setTimeout(() => setToast(''), 4500);
  }
  function go(p: Page) {
    setPage(p);
    setSelected(null);
    setMobile(false);
    setQuery('');
    setFilter('all');
  }
  function openBot(id: string) {
    setSelected(id);
    setTab('overview');
    setPage('bots');
  }
  async function save(config: BotConfig, id?: string) {
    if (mutation.current) throw new Error('Дождитесь завершения предыдущего запроса.');
    mutation.current = true;
    setBusy(true);
    let saved = false;
    try {
      const existing = workspace.bots.find((b) => b.id === id);
      if (id && !existing) throw new Error('Бот больше не существует. Обновите данные.');
      const result = existing ? await api.update(existing, config) : await api.create(config);
      saved = true;
      setEditor(null);
      setImporting(false);
      openBot(result.id);
      await refreshServer();
      notify('Настройки экземпляра сохранены на сервере');
    } catch (error) {
      const message = saved
        ? 'Настройки сохранены, но обновить список не удалось. Нажмите «Обновить данные».'
        : (error as Error).message;
      setServerError(message);
      throw new Error(message);
    } finally {
      mutation.current = false;
      setBusy(false);
    }
  }
  async function runConfirm() {
    if (mutation.current || !confirm) return;
    const target = workspace.bots.find((b) => b.id === confirm.id);
    if (!target) return;
    mutation.current = true;
    setBusy(true);
    try {
      await api.remove(target);
      setConfirm(null);
      go('bots');
      await refreshServer();
      notify('Экземпляр удалён с сервера');
    } catch (error) {
      setServerError((error as Error).message);
      setConfirm(null);
    } finally {
      mutation.current = false;
      setBusy(false);
    }
  }
  const actions = (
    <>
      <button
        className="button secondary"
        disabled={loading || !strategies.length}
        onClick={() => setImporting(true)}
      >
        <FolderInput size={17} />
        Импорт бота
      </button>
      <button
        className="button primary"
        disabled={loading || !strategies.length}
        onClick={() => setEditor({ config: defaultConfig })}
      >
        <Plus size={18} />
        Создать бота
      </button>
    </>
  );
  const period = (
    <div className="segmented" aria-label="Период статистики">
      {[
        [1, '24 часа'],
        [7, '7 дней'],
        [30, '30 дней'],
      ].map(([value, label]) => (
        <button
          key={value}
          aria-pressed={days === value}
          className={days === value ? 'active' : ''}
          onClick={() => setDays(Number(value))}
        >
          {label}
        </button>
      ))}
    </div>
  );
  return (
    <div className="app-shell">
      {mobile && (
        <button className="nav-scrim" aria-label="Закрыть меню" onClick={() => setMobile(false)} />
      )}
      <aside className={`sidebar ${mobile ? 'open' : ''}`}>
        <button className="brand" onClick={() => go('overview')} aria-label="Trade Bot — обзор">
          <span className="brand-mark">
            <Zap size={23} fill="currentColor" />
          </span>
          <span>
            trade<span className="brand-light">bot</span>
          </span>
        </button>
        <nav>
          {nav.map(({ id, label, icon: Icon }) => (
            <button
              key={id}
              className={page === id ? 'nav-item active' : 'nav-item'}
              aria-current={page === id ? 'page' : undefined}
              onClick={() => go(id)}
            >
              <Icon size={19} />
              <span>{label}</span>
              {live[id] && (
                <span
                  className={`live-dot ${live[id]!.on ? 'on' : ''}`}
                  role="img"
                  aria-label={live[id]!.text}
                  title={live[id]!.text}
                />
              )}
              {id === 'bots' && <span className="nav-count">{workspace.bots.length}</span>}
            </button>
          ))}
        </nav>
      </aside>
      <div className="main-shell">
        <header className="topbar">
          <div className="breadcrumb">
            <button
              className="icon-button mobile-menu"
              aria-label="Открыть меню"
              onClick={() => setMobile(true)}
            >
              <Menu size={20} />
            </button>
            {/* The page name is the heading below; on a phone it is shown here next to the menu button. */}
            <strong className="topbar-title">
              {bot ? bot.name : nav.find((n) => n.id === page)?.label}
            </strong>
          </div>
          <div className="topbar-right">
            <span
              className="mode-pill"
              title={
                demoRunning
                  ? 'Работает бот на демо-счёте Binance: заявки с ненастоящими деньгами'
                  : 'Реальные заявки не отправляются: боты торгуют виртуально по публичным данным'
              }
            >
              <FlaskConical size={13} />
              {demoRunning ? 'ДЕМО-СЧЁТ' : 'СИМУЛЯЦИЯ'}
            </span>
          </div>
        </header>
        <main>
          {serverError && (
            <div className="error-banner" role="alert">
              {serverError}
              <button
                className="button secondary"
                disabled={loading || busy}
                onClick={() => void refreshServer().catch(() => {})}
              >
                Обновить данные
              </button>
            </div>
          )}
          {/* For screen readers and checks only: the page itself shows data or an error. */}
          <span className="sr-only" role="status">
            {loading
              ? 'Загрузка с сервера…'
              : busy
                ? 'Сохранение…'
                : serverError
                  ? 'Не удалось обновить данные'
                  : 'Данные загружены с сервера'}
          </span>
          {bot ? (
            <>
              <button className="back-link" onClick={() => go('bots')}>
                <ArrowLeft size={15} />
                Все боты
              </button>
              <div className="page-heading">
                <div className="bot-title">
                  <span className="bot-icon large">
                    <Zap size={27} />
                  </span>
                  <div>
                    <h1>{bot.name}</h1>
                    <p>
                      {marketLabel(bot)} <span className="dot-separator">·</span> USDT Perpetual{' '}
                      <span className="dot-separator">·</span> {bot.exchange}
                    </p>
                  </div>
                </div>
                <div className="heading-actions">
                  <StatusBadge status={bot.status} />
                </div>
              </div>
              <div className="detail-tabs">
                {(['overview', 'trades', 'settings', 'events'] as Tab[]).map((t, i) => (
                  <button
                    key={t}
                    className={tab === t ? 'active' : ''}
                    aria-pressed={tab === t}
                    onClick={() => setTab(t)}
                  >
                    {['Обзор', 'Сделки', 'Настройки', 'Журнал'][i]}
                  </button>
                ))}
              </div>
              {tab === 'overview' && (
                <>
                  {strategyView(bot).live?.(bot, quietRefresh)}
                  <div className="section-line">
                    <span className="section-kicker">
                      <span className="tiny-dot" />
                      СТАТИСТИКА
                    </span>
                    {period}
                  </div>
                  <Stats stat={stat} budget={budget} balance={balance} unrealized={unrealized} />
                  <div className="overview-grid">
                    <Performance trades={trades} pnl={stat.pnl} />
                    <div className="panel bot-facts">
                      <h3>Параметры бота</h3>
                      {strategyView(bot)
                        .facts(bot)
                        .map((f) => (
                          <Fact key={f.label} label={f.label} value={f.value} />
                        ))}
                      <div className="inline-note">
                        <ShieldCheck size={17} />
                        <span>{strategyView(bot).note}</span>
                      </div>
                    </div>
                  </div>
                  <TradeQuality trades={trades} />
                  <TradesTable trades={trades} botName={bot.name} />
                </>
              )}
              {tab === 'trades' && (
                <>
                  <div className="section-line">
                    <p className="muted">Сделки симуляции. Реальных исполнений нет.</p>
                    {period}
                  </div>
                  <TradesTable trades={trades} botName={bot.name} />
                </>
              )}
              {tab === 'events' && (
                <Events events={workspace.events.filter((e) => e.botId === bot.id)} />
              )}
              {tab === 'settings' && (
                <div className="panel settings-panel">
                  <div className="section-line">
                    <h3>Настройки бота</h3>
                    <DownloadButton
                      onClick={() =>
                        download(
                          `${bot.symbol === '*' ? 'signal' : bot.symbol.toLowerCase()}-bot.json`,
                          exportConfig(bot),
                        )
                      }
                    />
                  </div>
                  <ConfigForm
                    config={bot}
                    onSave={(c) => save(c, bot.id)}
                    submitLabel="Сохранить изменения"
                  />
                  {bot.status === 'running' && (
                    <p className="field-hint">
                      Пока идёт симуляция, сервер не примет изменения: сначала остановите её.
                    </p>
                  )}
                  <div className="danger-zone">
                    <div>
                      <strong>Удалить бота</strong>
                      <p>Будут удалены его настройки и локальная история сделок.</p>
                    </div>
                    <button
                      className="button danger"
                      onClick={() => setConfirm({ type: 'delete', id: bot.id })}
                    >
                      <Trash2 size={15} />
                      Удалить
                    </button>
                  </div>
                </div>
              )}
            </>
          ) : (
            <>
              <div className="page-heading">
                <h1>{nav.find((n) => n.id === page)?.label}</h1>
                <div className="heading-actions">
                  {(page === 'overview' || page === 'bots') && actions}
                </div>
              </div>
              {page === 'scanner' && <Laboratory scanner={<Scanner />} />}
              {page === 'overview' && (
                <>
                  <div className="section-line">
                    <span className="section-kicker">
                      <span className="tiny-dot" />
                      СТАТИСТИКА ВСЕХ БОТОВ
                    </span>
                    {period}
                  </div>
                  <Stats stat={stat} budget={budget} balance={balance} unrealized={unrealized} />
                  <ExchangeBalanceCard />
                  <div className="overview-grid">
                    <Performance trades={trades} pnl={stat.pnl} />
                    <div className="panel workspace-health">
                      <div className="section-line">
                        <h3>Боты</h3>
                        <Radio size={18} className="muted" />
                      </div>
                      <div className="health-total">
                        <strong>{workspace.bots.length.toString().padStart(2, '0')}</strong>
                        <span>ботов</span>
                        <div className="orbit">
                          <BotIcon size={29} />
                        </div>
                      </div>
                      <Fact
                        label={'Запущено'}
                        value={
                          <span className="positive">
                            {workspace.bots.filter((b) => b.status === 'running').length}
                          </span>
                        }
                      />
                      <Fact
                        label="Не запущены"
                        value={
                          workspace.bots.filter(
                            (b) => b.status === 'draft' || b.status === 'stopped',
                          ).length
                        }
                      />
                    </div>
                  </div>
                  <div className="section-line bots-section-heading">
                    <h2>
                      Мои боты <span className="count-badge">{workspace.bots.length}</span>
                    </h2>
                    <button className="text-button" onClick={() => go('bots')}>
                      Все боты
                      <ArrowRight size={15} />
                    </button>
                  </div>
                  <div className="bots-grid">
                    {workspace.bots.slice(0, 2).map((b) => (
                      <BotCard
                        key={b.id}
                        bot={b}
                        onOpen={() => openBot(b.id)}
                        onDuplicate={() =>
                          setEditor({
                            config: {
                              ...defaultConfig,
                              ...b,
                              name: `${b.name.slice(0, 31)} · копия`,
                            },
                          })
                        }
                      />
                    ))}
                    <button
                      className="add-bot-card"
                      disabled={loading || !strategies.length}
                      onClick={() => setEditor({ config: defaultConfig })}
                    >
                      <span>
                        <Plus size={22} />
                      </span>
                      <span className="add-link">
                        Создать бота
                        <ArrowRight size={15} />
                      </span>
                    </button>
                  </div>
                  <TradeQuality trades={trades} />
                  <TradesTable trades={trades} showBot />
                </>
              )}
              {page === 'bots' && (
                <>
                  <div className="filter-bar">
                    <label className="search-field">
                      <Search size={17} />
                      <input
                        aria-label="Найти бота"
                        placeholder="Поиск по имени, паре или бирже"
                        value={query}
                        onChange={(e) => setQuery(e.target.value)}
                      />
                      {query && (
                        <button
                          className="icon-button"
                          aria-label="Очистить поиск"
                          onClick={() => setQuery('')}
                        >
                          <X size={14} />
                        </button>
                      )}
                    </label>
                    <div className="filter-select">
                      <ListFilter size={16} />
                      <select
                        aria-label="Фильтр статуса"
                        value={filter}
                        onChange={(e) => setFilter(e.target.value)}
                      >
                        <option value="all">Все статусы</option>
                        {(Object.keys(statusLabels) as Status[]).map((key) => (
                          <option key={key} value={key}>
                            {statusLabel(key)}
                          </option>
                        ))}
                      </select>
                    </div>
                    <span className="muted">Найдено: {visibleBots.length}</span>
                  </div>
                  {visibleBots.length ? (
                    <div className="bots-grid list">
                      {visibleBots.map((b) => (
                        <BotCard
                          key={b.id}
                          bot={b}
                          onOpen={() => openBot(b.id)}
                          onDuplicate={() =>
                            setEditor({
                              config: {
                                ...defaultConfig,
                                ...b,
                                name: `${b.name.slice(0, 31)} · копия`,
                              },
                            })
                          }
                        />
                      ))}
                    </div>
                  ) : (
                    <div className="panel">
                      <Empty
                        icon={<BotIcon size={28} />}
                        title={
                          query || filter !== 'all' ? 'Боты не найдены' : 'Ваш первый бот — впереди'
                        }
                        text={
                          query || filter !== 'all'
                            ? 'Попробуйте изменить поиск или фильтр.'
                            : 'Создайте бота из шаблона или импортируйте сохранённые настройки.'
                        }
                        action={
                          <button
                            className="button primary"
                            disabled={loading || !strategies.length}
                            onClick={() => setEditor({ config: defaultConfig })}
                          >
                            <Plus size={16} />
                            Создать бота
                          </button>
                        }
                      />
                    </div>
                  )}
                </>
              )}
            </>
          )}
        </main>
      </div>
      {editor && (
        <Modal
          title={editor.id ? 'Настроить бота' : 'Создать нового бота'}
          onClose={() => setEditor(null)}
        >
          <ConfigForm
            config={editor.config}
            onSave={(c) => save(c, editor.id)}
            submitLabel="Создать бота"
            onCancel={() => setEditor(null)}
          />
        </Modal>
      )}
      {importing && <ImportDialog onClose={() => setImporting(false)} onImport={(c) => save(c)} />}
      {confirm && (
        <Modal title="Удалить бота?" onClose={() => setConfirm(null)}>
          <p className="confirm-text">
            Настройки этого экземпляра будут удалены с сервера. При необходимости сначала
            экспортируйте настройки.
          </p>
          <div className="modal-actions">
            <button className="button secondary" onClick={() => setConfirm(null)}>
              Отмена
            </button>
            <button className="button danger" disabled={busy} onClick={() => void runConfirm()}>
              Удалить
            </button>
          </div>
        </Modal>
      )}{' '}
      {toast && <Toast message={toast} />}
    </div>
  );
}
function StatusBadge({ status }: { status: Status }) {
  return (
    <span className={`status-badge ${status}`}>
      <span />
      {statusLabel(status)}
    </span>
  );
}
function Fact({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="fact">
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}
function BotCard({
  bot,
  onOpen,
  onDuplicate,
}: {
  bot: Bot;
  onOpen: () => void;
  onDuplicate: () => void;
}) {
  const stat = metrics(bot.trades);
  // The deposit of the latest run (for a bot on an exchange, the account balance at its start) plus the closed trades.
  const balance = (bot.deposit ?? bot.budget) + stat.pnl;
  return (
    <article className="panel bot-card">
      <div className="bot-card-header">
        <span className="bot-icon">
          <Zap size={21} />
        </span>
        <button className="bot-name" onClick={onOpen}>
          {bot.name}
          <small>
            {strategyName(bot)} <span>v{bot.strategyVersion}</span>
          </small>
        </button>
        <button
          className="icon-button"
          aria-label={`Дублировать ${bot.name}`}
          title="Создать копию настроек"
          onClick={onDuplicate}
        >
          <Copy size={16} />
        </button>
      </div>
      <div className="bot-market">
        <span className="exchange-label">{bot.exchange}</span>
        <span>{bot.symbol === '*' ? 'Все монеты записи' : bot.symbol}</span>
        <span className="market-type">PERP</span>
      </div>
      <div className="bot-card-stats">
        <div>
          <span>Чистый PnL · вся история</span>
          <strong className={stat.pnl >= 0 ? 'positive' : 'negative'}>
            {formatMoney(stat.pnl, true)} <small>USDT</small>
          </strong>
        </div>
        <div>
          <span>Сделки</span>
          <strong>{stat.count}</strong>
        </div>
        <div>
          <span>{onExchange(bot) ? 'Баланс · демо-счёт' : 'Баланс · симуляция'}</span>
          <strong>
            {formatMoney(balance)} <small>USDT</small>
          </strong>
        </div>
      </div>
      <div className="bot-card-footer">
        <StatusBadge status={bot.status} />
        <div>
          {/* Run controls live on the bot page, next to its state. */}
          <button
            className="icon-button"
            aria-label={`Управление симуляцией ${bot.name}`}
            title="Управление симуляцией"
            onClick={onOpen}
          >
            {bot.status === 'running' ? <Pause size={16} /> : <Play size={16} />}
          </button>
          <button className="text-button" onClick={onOpen}>
            Статистика
            <ArrowUpRight size={15} />
          </button>
        </div>
      </div>
    </article>
  );
}
function ConfigForm({
  config,
  onSave,
  submitLabel,
  onCancel,
}: {
  config: BotConfig;
  onSave: (c: BotConfig) => Promise<void>;
  submitLabel: string;
  onCancel?: () => void;
}) {
  const [form, setForm] = useState(config);
  const [submitting, setSubmitting] = useState(false);
  const pending = useRef(false);
  const [error, setError] = useState('');
  const errorId = useId();
  useEffect(() => {
    setForm(config);
    setError('');
  }, [config]);
  const density = form.signal?.signal === 'Плотность';
  const exchangeBot = onExchange(form);
  // On an exchange the deposit is the account balance: the budget becomes 100 and the loss limit a percentage of it.
  function setExecution(execution: Execution) {
    if (onExchange({ execution }) === exchangeBot) return setForm({ ...form, execution });
    const lossPct = (form.lossLimit / form.budget) * 100;
    setForm(
      onExchange({ execution })
        ? {
            ...form,
            execution,
            budget: 100,
            lossLimit: Number.isFinite(lossPct) ? Math.round(lossPct * 10) / 10 : 30,
          }
        : { ...form, execution, budget: 100, lossLimit: form.lossLimit },
    );
  }
  // Only the density strategy trades on an exchange so far.
  useEffect(() => {
    if (!density && exchangeBot) setForm((f) => ({ ...f, execution: 'simulation' }));
  }, [density, exchangeBot]);
  async function submit(e: FormEvent) {
    e.preventDefault();
    if (pending.current) return;
    pending.current = true;
    setSubmitting(true);
    try {
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
      } = form;
      await onSave(
        validateConfig({
          name,
          exchange,
          symbol,
          budget,
          lossLimit,
          strategy,
          strategyVersion,
          signal,
          execution: execution ?? 'simulation',
        }),
      );
      setError('');
    } catch (e) {
      setError((e as Error).message);
    } finally {
      pending.current = false;
      setSubmitting(false);
    }
  }
  return (
    <form onSubmit={submit} className="config-form" aria-describedby={error ? errorId : undefined}>
      <label className="field">
        <span>Название бота</span>
        <input
          data-autofocus
          value={form.name}
          maxLength={40}
          placeholder="Например, Вынос цены · 5 мин"
          onChange={(e) => setForm({ ...form, name: e.target.value })}
          required
        />
      </label>
      <label className="field">
        <span>Где торгует</span>
        <select
          value={form.execution ?? 'simulation'}
          onChange={(e) => setExecution(e.target.value as Execution)}
        >
          <option value="simulation">Симуляция: виртуальные сделки, без биржи</option>
          <option value="testnet" disabled={!density}>
            Демо-счёт Binance: заявки с ненастоящими деньгами
            {density ? '' : ' (только «Плотность»)'}
          </option>
          <option value="live" disabled>
            Реальный счёт — после проверки на демо-счёте
          </option>
        </select>
      </label>
      {exchangeBot ? (
        <div className="form-grid">
          <p className="field-hint">
            Депозит — баланс счёта в момент запуска; размер сделок считается от него.
          </p>
          <label className="field">
            <span>Лимит убытка, % депозита</span>
            <input
              type="number"
              min="0.1"
              max="100"
              step="0.1"
              value={Number.isNaN(form.lossLimit) ? '' : form.lossLimit}
              onChange={(e) => setForm({ ...form, lossLimit: e.target.valueAsNumber })}
              required
            />
          </label>
        </div>
      ) : (
        <div className="form-grid">
          <label className="field">
            <span>Депозит бота, USDT</span>
            <input
              type="number"
              min="1"
              max="1000000"
              step="0.01"
              value={Number.isNaN(form.budget) ? '' : form.budget}
              onChange={(e) => setForm({ ...form, budget: e.target.valueAsNumber })}
              required
            />
          </label>
          <label className="field">
            <span>Лимит убытка, USDT</span>
            <input
              type="number"
              min="0.01"
              max={form.budget}
              step="0.01"
              value={Number.isNaN(form.lossLimit) ? '' : form.lossLimit}
              onChange={(e) => setForm({ ...form, lossLimit: e.target.valueAsNumber })}
              required
            />
          </label>
        </div>
      )}
      {form.signal && <SignalFields form={form} setForm={setForm} />}
      {error && (
        <p id={errorId} className="form-error" role="alert">
          {error}
        </p>
      )}
      <div className="modal-actions">
        {onCancel && (
          <button type="button" className="button secondary" onClick={onCancel}>
            Отмена
          </button>
        )}
        <button className="button primary" type="submit" disabled={submitting}>
          {submitLabel}
          <ArrowRight size={16} />
        </button>
      </div>
    </form>
  );
}
function ImportDialog({
  onClose,
  onImport,
}: {
  onClose: () => void;
  onImport: (c: BotConfig) => Promise<void>;
}) {
  const [text, setText] = useState('');
  const [error, setError] = useState('');
  const errorId = useId();
  const [filename, setFilename] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const pending = useRef(false);
  async function file(f?: File) {
    if (!f) return;
    setError('');
    setText('');
    setFilename('');
    if (f.size > 32768) {
      setError('Файл должен быть не больше 32 КБ.');
      return;
    }
    try {
      const contents = await f.text();
      importConfig(contents);
      setText(contents);
      setFilename(f.name);
    } catch (e) {
      setError((e as Error).message);
    }
  }
  return (
    <Modal
      title="Импорт бота"
      subtitle="Добавьте настройки, экспортированные из Trade Bot."
      onClose={onClose}
    >
      <label
        className="upload-zone"
        onDragOver={(e) => e.preventDefault()}
        onDrop={(e) => {
          e.preventDefault();
          void file(e.dataTransfer.files[0]);
        }}
      >
        <FolderInput size={30} />
        <strong>{filename || 'Выберите JSON-файл или перетащите сюда'}</strong>
        <span>Наш формат · версия 1 · до 32 КБ</span>
        <input
          type="file"
          accept=".json,application/json"
          aria-label="Файл настроек бота"
          onChange={(e) => void file(e.target.files?.[0])}
        />
      </label>
      <label className="field">
        <span>Или вставьте содержимое JSON</span>
        <textarea
          rows={6}
          aria-invalid={!!error}
          aria-describedby={error ? errorId : undefined}
          value={text}
          onChange={(e) => {
            setText(e.target.value);
            setError('');
          }}
          placeholder={'{ "schemaVersion": 1, "config": { … } }'}
        />
      </label>
      <p className="field-hint">
        Импортируется только конфигурация. История, статус запуска, API-ключи и программный код не
        переносятся.
      </p>
      {error && (
        <p id={errorId} className="form-error" role="alert">
          {error}
        </p>
      )}
      <div className="modal-actions">
        <button className="button secondary" onClick={onClose}>
          Отмена
        </button>
        <button
          className="button primary"
          disabled={!text.trim() || submitting}
          onClick={async () => {
            if (pending.current) return;
            pending.current = true;
            setSubmitting(true);
            try {
              await onImport(importConfig(text));
            } catch (e) {
              setError((e as Error).message);
            } finally {
              pending.current = false;
              setSubmitting(false);
            }
          }}
        >
          <FolderInput size={16} />
          Импортировать
        </button>
      </div>
    </Modal>
  );
}
function Events({ events }: { events: Workspace['events'] }) {
  return (
    <section className="panel events-panel">
      <div className="section-line">
        <h3>Журнал событий</h3>
        <span className="muted">Последние {events.length} событий</span>
      </div>
      {events.length ? (
        events.map((e) => (
          <div className="event" key={e.id}>
            <span className="event-icon">
              <Activity size={16} />
            </span>
            <div>
              <strong>{e.botName}</strong>
              <p>{e.text}</p>
            </div>
            <time>
              {new Date(e.at).toLocaleString('ru-RU', {
                day: '2-digit',
                month: '2-digit',
                hour: '2-digit',
                minute: '2-digit',
                timeZone: 'Europe/Moscow',
              })}
            </time>
          </div>
        ))
      ) : (
        <Empty
          icon={<Clock3 size={26} />}
          title="Пока без событий"
          text="Здесь появятся изменения настроек и состояния бота."
        />
      )}
    </section>
  );
}

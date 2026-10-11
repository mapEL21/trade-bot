import { useEffect, useId, useRef, useState, type FormEvent, type ReactNode } from 'react';
import {
  Activity,
  ArrowDownToLine,
  ArrowLeft,
  ArrowRight,
  ArrowUpRight,
  Bot as BotIcon,
  Check,
  ChevronRight,
  Clock3,
  Copy,
  FlaskConical,
  FolderInput,
  Gauge,
  LayoutDashboard,
  ListFilter,
  Menu,
  Pause,
  Play,
  Plus,
  Radio,
  Search,
  ShieldCheck,
  Trash2,
  TrendingUp,
  Wallet,
  Waypoints,
  X,
  Zap,
} from 'lucide-react';
import { api, type InstrumentInfo, type StrategyDefinition } from './api';
import { Strategies } from './Strategies';
import { Research } from './Research';
import { PaperRun, activeStatuses } from './PaperRun';
import { LeaderLagFields, sizeProblem } from './LeaderLagFields';
import { defaultFees } from './strategySettings';
import { Chart, DownloadButton, Empty, Modal, SelectField, Toast, Tooltip } from './components';
import {
  csvCell,
  defaultLeaderLagSettings,
  exchanges,
  exportConfig,
  formatMoney,
  importConfig,
  metrics,
  net,
  paperExchanges,
  periodTrades,
  statusLabels,
  validateConfig,
  type Bot,
  type BotConfig,
  type Exchange,
  type Status,
  type Trade,
  type Workspace,
} from './model';

const emptyWorkspace: Workspace = { schemaVersion: 1, bots: [], events: [] };

type Page = 'strategies' | 'overview' | 'bots' | 'trades' | 'events' | 'connections' | 'research';
type Tab = 'overview' | 'trades' | 'settings' | 'events';
const nav: { id: Page; label: string; icon: typeof BotIcon }[] = [
  { id: 'overview', label: 'Обзор', icon: LayoutDashboard },
  { id: 'bots', label: 'Мои боты', icon: BotIcon },
  { id: 'strategies', label: 'Стратегии', icon: Zap },
  { id: 'research', label: 'Исследование рынка', icon: Radio },
  { id: 'trades', label: 'Сделки', icon: Activity },
  { id: 'events', label: 'Журнал событий', icon: Clock3 },
  { id: 'connections', label: 'Подключения', icon: Waypoints },
];
const defaultConfig: BotConfig = {
  name: '',
  exchange: 'Binance',
  symbol: 'ARBUSDT',
  budget: 10,
  lossLimit: 1,
  strategy: 'impulse',
  strategyVersion: '0.1.0',
};
function leaderLagConfig(version: string): BotConfig {
  return {
    name: '',
    exchange: 'Binance',
    symbol: 'ARBUSDT',
    budget: 10,
    lossLimit: 1,
    strategy: 'leader-lag',
    strategyVersion: version,
    parameters: {
      signalExchange: 'OKX',
      settings: { ...defaultLeaderLagSettings },
      delayMs: 50,
      counter: false,
    },
  };
}
// A running bot is always a live paper simulation, never real trading.
const statusLabel = (status: Status) => statusLabels[status];
// Fees and thresholds are stored in basis points and shown in percent.
const percent = (bps: number) =>
  `${(bps / 100).toLocaleString('ru-RU', { maximumFractionDigits: 6 })}%`;
const strategyName = (bot: BotConfig) => (bot.strategy === 'leader-lag' ? 'Поводырь' : 'Импульс');
function download(name: string, text: string, type = 'application/json') {
  const url = URL.createObjectURL(new Blob([text], { type }));
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
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
  const allTrades = workspace.bots.flatMap((b) =>
    b.trades.map((t) => ({ ...t, botName: b.name, symbol: b.symbol })),
  );
  const now = Date.now();
  const trades = periodTrades(bot?.trades ?? allTrades, days, now);
  const stat = metrics(trades);
  const budget = bot?.budget ?? workspace.bots.reduce((s, b) => s + b.budget, 0);
  // Allocated balance plus every closed simulated trade so far, whatever the period shown.
  const balance = budget + metrics(bot?.trades ?? allTrades).pnl;
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
  const anyActive = workspace.bots.some((b) => activeStatuses.includes(b.status));
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
            <small>TRADING WORKSPACE</small>
          </span>
        </button>
        <div className="workspace-switch">
          <span className="avatar">M</span>
          <span>
            Моё пространство<small>{'Локальный сервер'}</small>
          </span>
          <span className="workspace-dot" />
        </div>
        <p className="nav-caption">РАБОЧЕЕ ПРОСТРАНСТВО</p>
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
              {id === 'bots' && <span className="nav-count">{workspace.bots.length}</span>}
            </button>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <div className="sandbox-card">
            <FlaskConical size={21} />
            <strong>Только симуляция</strong>
            <p>Боты работают на публичных данных бирж. Реальных заявок и ключей нет.</p>
            <button onClick={() => go('connections')}>
              О рабочем пространстве <ArrowUpRight size={15} />
            </button>
          </div>
          <div className="sidebar-footer">
            <span className="tiny-dot" />
            Серверное пространство
            <span>v0.1</span>
          </div>
        </div>
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
            <span>Рабочее пространство</span>
            <ChevronRight size={14} />
            <strong>{bot ? bot.name : nav.find((n) => n.id === page)?.label}</strong>
          </div>
          <div className="topbar-right">
            <span className="mode-pill">
              <FlaskConical size={13} />
              СИМУЛЯЦИЯ
            </span>
            <span className="topbar-divider" />
            <span className="profile" title="Локальный сервер">
              M
            </span>
          </div>
        </header>
        <main>
          <div className="mode-banner">
            <FlaskConical size={16} />
            <span>
              <strong>Живая симуляция.</strong> Настройки и результаты хранятся в базе локального
              сервера. Боты работают по публичным данным бирж; реальные заявки не отправляются,
              торговые подключения не созданы.
            </span>
            <button onClick={() => go('connections')}>
              Подробнее
              <ArrowRight size={14} />
            </button>
          </div>
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
          <div className="server-toolbar">
            <span role="status">
              {loading
                ? 'Загрузка с сервера…'
                : busy
                  ? 'Сохранение…'
                  : serverError
                    ? 'Не удалось обновить данные'
                    : 'Данные загружены с сервера'}
            </span>
            <button
              className="text-button"
              disabled={loading || busy}
              onClick={() => void refreshServer().catch(() => {})}
            >
              Обновить данные
            </button>
          </div>
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
                      {bot.symbol.replace('USDT', ' / USDT')}{' '}
                      <span className="dot-separator">·</span> {bot.exchange}{' '}
                      <span className="dot-separator">·</span> USDT Perpetual
                      {bot.parameters && (
                        <>
                          {' '}
                          <span className="dot-separator">·</span> сигнал{' '}
                          {bot.parameters.signalExchange}
                        </>
                      )}
                    </p>
                  </div>
                </div>
                <div className="heading-actions">
                  <StatusBadge status={bot.status} />
                  {bot.strategy !== 'leader-lag' && (
                    <button className="button secondary" disabled>
                      Алгоритм в разработке
                    </button>
                  )}
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
              {tab === 'overview' && bot.strategy === 'leader-lag' && bot.parameters && (
                <>
                  <PaperRun bot={bot} onChanged={quietRefresh} />
                  <div className="section-line">
                    <span className="section-kicker">
                      <span className="tiny-dot" />
                      СТАТИСТИКА СИМУЛЯЦИИ · НЕ РЕАЛЬНАЯ ТОРГОВЛЯ
                    </span>
                    {period}
                  </div>
                  <Stats stat={stat} budget={budget} balance={balance} />
                  <div className="overview-grid">
                    <Performance trades={trades} pnl={stat.pnl} />
                    <div className="panel bot-facts">
                      <h3>Параметры бота</h3>
                      <Fact label="Стратегия" value="Поводырь → отстающая биржа" />
                      <Fact label="Версия" value={bot.strategyVersion} />
                      <Fact
                        label="Биржа бота / сигнала"
                        value={`${bot.exchange} / ${bot.parameters.signalExchange}`}
                      />
                      <Fact
                        label="Размер заявки"
                        value={
                          bot.parameters.settings.orderQuantity !== null
                            ? `${bot.parameters.settings.orderQuantity} ${bot.symbol.replace(/USDT$/, '')}`
                            : `${formatMoney(bot.parameters.settings.orderNotional)} USDT`
                        }
                      />
                      <Fact label="Лимит убытка" value={`${formatMoney(bot.lossLimit)} USDT`} />
                      <Fact
                        label="Вход · исполнение"
                        value={`${bot.parameters.settings.entryOrder === 'maker' ? 'maker' : 'taker'} · ${bot.parameters.settings.executionModel === 'depth' ? 'по стакану' : 'BBO'} · ${bot.parameters.delayMs} мс`}
                      />
                      <Fact
                        label="Комиссии taker / maker"
                        value={`${percent(bot.parameters.settings.feeBps)} / ${percent(bot.parameters.settings.makerFeeBps)}`}
                      />
                      <div className="inline-note">
                        <ShieldCheck size={17} />
                        <span>
                          Лимит убытка и размер заявки применяются в симуляции. Реальных позиций и
                          ордеров нет.
                        </span>
                      </div>
                    </div>
                  </div>
                  <TradesTable trades={trades} botName={bot.name} symbol={bot.symbol} />
                </>
              )}
              {tab === 'overview' && bot.strategy !== 'leader-lag' && (
                <>
                  <div className="section-line">
                    <span className="muted">{'Статистика экземпляра'}</span>
                    {period}
                  </div>
                  <Stats stat={stat} budget={budget} balance={balance} />
                  <div className="overview-grid">
                    <Performance trades={trades} pnl={stat.pnl} />
                    <div className="panel bot-facts">
                      <h3>Параметры бота</h3>
                      <Fact label="Стратегия" value={strategyName(bot)} />
                      <Fact label="Версия" value={`${bot.strategyVersion} · шаблон`} />
                      <Fact label="Выделено" value={`${formatMoney(bot.budget)} USDT`} />
                      <Fact label="Лимит убытка" value={`${formatMoney(bot.lossLimit)} USDT`} />
                      <Fact label="Открытая позиция" value="Нет позиции" />
                      <div className="inline-note">
                        <ShieldCheck size={17} />
                        <span>Настройки риска пока не исполняются: торгового движка нет.</span>
                      </div>
                    </div>
                  </div>
                  <TradesTable trades={trades} botName={bot.name} symbol={bot.symbol} />
                </>
              )}
              {tab === 'trades' && (
                <>
                  <div className="section-line">
                    <p className="muted">
                      {bot.strategy === 'leader-lag'
                        ? 'Сделки симуляции — на вкладке «Обзор», в истории запусков. Реальных исполнений нет.'
                        : 'Исполнений пока нет'}
                    </p>
                    {period}
                  </div>
                  <TradesTable trades={trades} botName={bot.name} symbol={bot.symbol} />
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
                        download(`${bot.symbol.toLowerCase()}-bot.json`, exportConfig(bot))
                      }
                    />
                  </div>
                  <ConfigForm
                    config={bot}
                    historyLocked={!!bot.trades.length}
                    onSave={(c) => save(c, bot.id)}
                    submitLabel="Сохранить изменения"
                    strategies={strategies}
                    strategyLocked
                  />
                  {activeStatuses.includes(bot.status) && (
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
                <div>
                  <div className="eyebrow">ВАШ ТРЕЙДИНГ. ВАШИ ПРАВИЛА.</div>
                  <h1>
                    {page === 'overview'
                      ? 'Всё под контролем'
                      : nav.find((n) => n.id === page)?.label}
                  </h1>
                  <p>
                    {
                      {
                        overview: 'Результаты, стратегии и боты — в одном пространстве.',
                        bots: 'Экземпляры стратегий: настройки, состояние и статистика.',
                        strategies: 'Наши алгоритмы, их версии и готовность к запуску.',
                        research: 'Сравниваем выбранные биржи на реальных данных.',
                        trades: 'История исполнений всех ботов в одном месте.',
                        events: 'Все изменения вашего рабочего пространства.',
                        connections: 'Биржевые подключения и данные приложения.',
                      }[page]
                    }
                  </p>
                </div>
                <div className="heading-actions">
                  {(page === 'overview' || page === 'bots') && actions}
                </div>
              </div>
              {page === 'research' && <Research />}
              {page === 'strategies' && (
                <Strategies
                  items={strategies}
                  onCreate={(item) =>
                    setEditor({
                      config:
                        item.id === 'leader-lag' ? leaderLagConfig(item.version) : defaultConfig,
                    })
                  }
                />
              )}
              {page === 'overview' && (
                <>
                  <div className="section-line">
                    <span className="section-kicker">
                      <span className="tiny-dot" />
                      {'СТАТИСТИКА ЭКЗЕМПЛЯРОВ'}
                    </span>
                    {period}
                  </div>
                  <Stats stat={stat} budget={budget} balance={balance} />
                  <div className="overview-grid">
                    <Performance trades={trades} pnl={stat.pnl} />
                    <div className="panel workspace-health">
                      <div className="section-line">
                        <h3>Рабочая среда</h3>
                        <Radio size={18} className="muted" />
                      </div>
                      <div className="health-total">
                        <strong>{workspace.bots.length.toString().padStart(2, '0')}</strong>
                        <span>
                          ботов
                          <br />в пространстве
                        </span>
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
                        label="На паузе"
                        value={workspace.bots.filter((b) => b.status === 'paused').length}
                      />
                      <Fact
                        label="Черновики / остановлены"
                        value={
                          workspace.bots.filter(
                            (b) => b.status === 'draft' || b.status === 'stopped',
                          ).length
                        }
                      />
                      <div className="health-footer">
                        <span className="tiny-dot amber" />
                        Биржи не подключены
                        <button onClick={() => go('connections')} aria-label="Открыть подключения">
                          <ArrowUpRight size={17} />
                        </button>
                      </div>
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
                      <strong>Новая стратегия начинается здесь</strong>
                      <p>Добавьте бота и настройте его под себя</p>
                      <span className="add-link">
                        Создать бота
                        <ArrowRight size={15} />
                      </span>
                    </button>
                  </div>
                  <div className="footnote">
                    <ShieldCheck size={14} />{' '}
                    {'Настройки и журнал сохраняются в базе локального сервера.'}
                  </div>
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
              {page === 'trades' && (
                <>
                  <div className="section-line">
                    <span className="muted">
                      {'Сделки живой симуляции всех ботов · не реальная торговля'}
                    </span>
                    {period}
                  </div>
                  <TradesTable trades={periodTrades(allTrades, days, now)} showBot />
                </>
              )}
              {page === 'events' && <Events events={workspace.events} />}
              {page === 'connections' && (
                <>
                  <div className="connection-grid">
                    {exchanges.map((exchange, i) => (
                      <div className="panel exchange-card" key={exchange}>
                        <div className={`exchange-logo exchange-${i}`}>
                          {exchange === 'Binance' ? '◇' : exchange === 'Bybit' ? 'by' : '▦'}
                        </div>
                        <h3>{exchange}</h3>
                        <span className="neutral-badge">Не подключена</span>
                        <p>
                          Интеграция появится после создания торгового движка и проверки брокерского
                          API.
                        </p>
                        <button className="button secondary" disabled>
                          Скоро
                        </button>
                      </div>
                    ))}
                  </div>
                  <div className="panel local-data">
                    <div className="section-line">
                      <div>
                        <h3>Данные рабочего пространства</h3>
                        <p className="muted">
                          {'Настройки и журнал сохраняются сервером в SQLite.'} Секретные ключи
                          здесь не запрашиваются.
                        </p>
                      </div>
                      <ShieldCheck size={25} className="positive" />
                    </div>
                    <div className="inline-note">
                      <FlaskConical size={18} />
                      <span>
                        Сервер записывает публичные потоки бирж и ведёт живую симуляцию ботов
                        «Поводырь». Торговые подключения к аккаунтам ещё не созданы, реальные заявки
                        не отправляются. Выбранный тикер — пример, а не рекомендация.
                      </span>
                    </div>
                  </div>
                </>
              )}
            </>
          )}
        </main>
        <footer className="page-footer">
          <span>
            tradebot <span className="muted">/ рабочее пространство</span>
          </span>
          <span>
            Интерфейс v0.1 <span className="dot-separator">·</span> Реальная торговля не подключена
          </span>
        </footer>
      </div>
      {editor && (
        <Modal
          title={editor.id ? 'Настроить бота' : 'Создать нового бота'}
          subtitle="Создайте экземпляр стратегии: выберите биржу, инструмент и лимиты. Код стратегии подключается отдельно."
          onClose={() => setEditor(null)}
        >
          <ConfigForm
            config={editor.config}
            onSave={(c) => save(c, editor.id)}
            submitLabel="Создать бота"
            onCancel={() => setEditor(null)}
            strategies={strategies}
            strategyLocked={!!editor.id}
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
function Stats({
  stat,
  budget,
  balance,
}: {
  stat: ReturnType<typeof metrics>;
  budget: number;
  balance: number;
}) {
  return (
    <div className="stats-grid">
      <Stat
        label="Чистый результат"
        value={`${formatMoney(stat.pnl, true)}`}
        unit="USDT"
        icon={<TrendingUp size={18} />}
        accent={stat.pnl >= 0 ? 'positive' : 'negative'}
        note={`После комиссий · ${formatMoney(stat.fees)} USDT`}
      />
      <Stat
        label={'Баланс · симуляция'}
        value={formatMoney(balance)}
        unit="USDT"
        icon={<Wallet size={18} />}
        note={`Выделено ${formatMoney(budget)} USDT, изменение — по закрытым сделкам симуляции`}
      />
      <Stat
        label="Прибыльных сделок"
        value={stat.winRate === null ? '—' : stat.winRate.toFixed(1).replace('.', ',')}
        unit={stat.winRate === null ? '' : '%'}
        icon={<Gauge size={18} />}
        note={`${stat.count} закрытых ${'сделок'}`}
      />
      <Stat
        label="Макс. просадка"
        value={formatMoney(stat.drawdown)}
        unit="USDT"
        icon={<ShieldCheck size={18} />}
        note="По закрытым сделкам за период"
      />
    </div>
  );
}
function Stat({
  label,
  value,
  unit,
  icon,
  note,
  accent = '',
}: {
  label: string;
  value: string;
  unit: string;
  icon: ReactNode;
  note: string;
  accent?: string;
}) {
  return (
    <div className="panel stat">
      <div className="stat-label">
        {label}
        {icon}
      </div>
      <div className={`stat-value ${accent}`}>
        {value}
        <span>{unit}</span>
      </div>
      <div className="stat-note">{note}</div>
    </div>
  );
}
function Performance({ trades, pnl }: { trades: Trade[]; pnl: number }) {
  return (
    <section className="panel performance">
      <div className="section-line">
        <h3>
          Динамика результата{' '}
          <Tooltip
            text={
              'Накопленный результат закрытых сделок живой симуляции за выбранный период с учётом комиссий. Это не реальная торговля.'
            }
          />
        </h3>
        <span className="chart-legend">
          <span className="tiny-dot" />
          {'Чистый PnL · симуляция'}
        </span>
      </div>
      <div className="chart-summary">
        <strong className={pnl >= 0 ? 'positive' : 'negative'}>
          {formatMoney(pnl, true)} <span>USDT</span>
        </strong>
        <span>за выбранный период</span>
      </div>
      {trades.length ? (
        <Chart trades={trades} />
      ) : (
        <Empty
          icon={<TrendingUp size={26} />}
          title="Здесь появится кривая результата"
          text={'Закрытых сделок симуляции за выбранный период нет.'}
        />
      )}
    </section>
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
  // Every trade is simulated, so the card shows the simulated balance.
  const balance = bot.budget + stat.pnl;
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
        <span>{bot.symbol}</span>
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
          <span>{'Баланс · симуляция'}</span>
          <strong>
            {formatMoney(balance)} <small>USDT</small>
          </strong>
        </div>
      </div>
      <div className="bot-card-footer">
        <StatusBadge status={bot.status} />
        <div>
          {bot.strategy === 'leader-lag' ? (
            // Simulation controls live on the bot page, next to its live state.
            <button
              className="icon-button"
              aria-label={`Управление симуляцией ${bot.name}`}
              title="Управление симуляцией"
              onClick={onOpen}
            >
              {activeStatuses.includes(bot.status) ? <Pause size={16} /> : <Play size={16} />}
            </button>
          ) : (
            <button
              className="icon-button"
              aria-label={`Запуск недоступен: ${bot.name}`}
              disabled
              title="Алгоритм ещё не реализован"
            >
              <Play size={16} />
            </button>
          )}
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
  historyLocked = false,
  strategies = [],
  strategyLocked = false,
}: {
  config: BotConfig;
  onSave: (c: BotConfig) => Promise<void>;
  submitLabel: string;
  onCancel?: () => void;
  historyLocked?: boolean;
  strategies?: StrategyDefinition[];
  strategyLocked?: boolean;
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
  const leaderLag = form.strategy === 'leader-lag';
  const leaderLagDefinition = strategies.find((s) => s.id === 'leader-lag' && s.canCreate);
  const params = form.parameters;
  const [instrument, setInstrument] = useState<InstrumentInfo | null>(null);
  function chooseStrategy(id: BotConfig['strategy']) {
    if (id === form.strategy) return;
    if (id === 'leader-lag' && leaderLagDefinition)
      setForm({
        ...leaderLagConfig(leaderLagDefinition.version),
        name: form.name,
        budget: form.budget,
        lossLimit: form.lossLimit,
      });
    else setForm({ ...defaultConfig, name: form.name });
  }
  function chooseExchange(exchange: Exchange) {
    setForm((f) => ({
      ...f,
      exchange,
      // The signal must come from the other exchange; fees default to the new exchange's tariff.
      parameters: f.parameters && {
        ...f.parameters,
        signalExchange:
          f.parameters.signalExchange === exchange
            ? paperExchanges.find((x) => x !== exchange)!
            : f.parameters.signalExchange,
        settings: {
          ...f.parameters.settings,
          feeBps: defaultFees[exchange].taker,
          makerFeeBps: defaultFees[exchange].maker,
        },
      },
    }));
  }
  async function submit(e: FormEvent) {
    e.preventDefault();
    if (pending.current) return;
    pending.current = true;
    setSubmitting(true);
    try {
      const { name, exchange, symbol, budget, lossLimit, strategy, strategyVersion, parameters } =
        form;
      const tooSmall =
        parameters &&
        strategy === 'leader-lag' &&
        sizeProblem(parameters.settings, budget, instrument);
      if (tooSmall) throw new Error(tooSmall);
      await onSave(
        validateConfig({
          name,
          exchange,
          symbol,
          budget,
          lossLimit,
          strategy,
          strategyVersion,
          ...(strategy === 'leader-lag' ? { parameters } : {}),
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
          placeholder={leaderLag ? 'Например, Поводырь · MAGIC' : 'Например, Импульс · ARB'}
          onChange={(e) => setForm({ ...form, name: e.target.value })}
          required
        />
      </label>
      <fieldset className="strategy-choice" disabled={strategyLocked}>
        <legend>Стратегия</legend>
        <label className={`strategy-option ${!leaderLag ? 'chosen' : ''}`}>
          <input
            type="radio"
            name="strategy"
            checked={!leaderLag}
            onChange={() => chooseStrategy('impulse')}
          />
          <span className="bot-icon">
            <Zap size={22} />
          </span>
          <div>
            <strong>Импульс</strong>
            <p>Шаблон стратегии · алгоритм ещё не реализован</p>
          </div>
          {!leaderLag && <Check size={18} />}
        </label>
        {(leaderLagDefinition || leaderLag) && (
          <label className={`strategy-option ${leaderLag ? 'chosen' : ''}`}>
            <input
              type="radio"
              name="strategy"
              checked={leaderLag}
              onChange={() => chooseStrategy('leader-lag')}
            />
            <span className="bot-icon">
              <Radio size={22} />
            </span>
            <div>
              <strong>Поводырь → отстающая биржа</strong>
              <p>
                Живая симуляция · v
                {form.strategyVersion === '0.1.0'
                  ? leaderLagDefinition?.version
                  : form.strategyVersion}
              </p>
            </div>
            {leaderLag && <Check size={18} />}
          </label>
        )}
      </fieldset>
      <div className="form-grid">
        <SelectField
          label={leaderLag ? 'Биржа бота (исполнение)' : 'Биржа'}
          value={form.exchange}
          onChange={(exchange) => chooseExchange(exchange as Exchange)}
        >
          {(leaderLag ? paperExchanges : exchanges).map((x) => (
            <option key={x}>{x}</option>
          ))}
        </SelectField>
        {!leaderLag && (
          <label className="field">
            <span>USDT-фьючерс</span>
            <input
              value={form.symbol}
              onChange={(e) => setForm({ ...form, symbol: e.target.value.toUpperCase() })}
              placeholder="ARBUSDT"
              required
            />
          </label>
        )}
        <label className="field">
          <span>{'Выделенный баланс, USDT'}</span>
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
      {leaderLag && params && (
        <LeaderLagFields form={form} setForm={setForm} onInstrument={setInstrument} />
      )}
      {historyLocked && (
        <p className="field-hint">
          Биржу, инструмент и начальный баланс бота с историей можно изменить в новой копии бота.
        </p>
      )}
      <div className="inline-note">
        <FlaskConical size={17} />
        <span>
          {leaderLag
            ? 'Бот работает только в живой симуляции: реальные заявки не отправляются, ключи не нужны. Биржа сигнала должна оказаться поводырём, иначе запуск остановится с объяснением. Комиссии — пример, укажите свои.'
            : 'Создаётся черновик экземпляра. Алгоритм ещё не реализован, запуск недоступен.'}{' '}
          {!leaderLag && 'Доступность инструмента, минимальный ордер и маржа ещё не проверяются.'}
        </span>
      </div>
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
function TradesTable({
  trades,
  botName,
  symbol,
  showBot = false,
}: {
  trades: (Trade & { botName?: string; symbol?: string })[];
  botName?: string;
  symbol?: string;
  showBot?: boolean;
}) {
  const sorted = [...trades].sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
  function csv() {
    const rows = [
      [
        'Время UTC',
        'Бот',
        'Пара',
        'Сторона',
        'Вход',
        'Выход',
        'Количество',
        'Комиссия USDT',
        'Чистый PnL USDT',
      ],
      ...sorted.map((t) => [
        t.at,
        t.botName ?? botName ?? '',
        t.symbol ?? symbol ?? '',
        t.side,
        t.entry,
        t.exit,
        t.quantity,
        t.fee,
        net(t),
      ]),
    ];
    download(
      'simulated-trades.csv',
      '\uFEFF' + rows.map((r) => r.map(csvCell).join(';')).join('\r\n'),
      'text/csv;charset=utf-8',
    );
  }
  return (
    <section className="panel trades-panel">
      <div className="section-line">
        <h3>
          История сделок <span className="count-badge">{trades.length}</span>
        </h3>
        <button className="text-button" disabled={!trades.length} onClick={csv}>
          <ArrowDownToLine size={15} />
          Скачать CSV
        </button>
      </div>
      {!trades.length ? (
        <Empty
          icon={<Activity size={26} />}
          title="Сделок пока нет"
          text={'Здесь появятся закрытые сделки живой симуляции бота.'}
        />
      ) : (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th>Время · МСК</th>
                {showBot && <th>Бот</th>}
                <th>Инструмент</th>
                <th>Сторона</th>
                <th>Вход → выход</th>
                <th>Количество</th>
                <th>Комиссия</th>
                <th>Чистый PnL</th>
              </tr>
            </thead>
            <tbody>
              {sorted.map((t) => (
                <tr key={t.id}>
                  <td>
                    {new Date(t.at).toLocaleString('ru-RU', {
                      day: '2-digit',
                      month: '2-digit',
                      hour: '2-digit',
                      minute: '2-digit',
                      timeZone: 'Europe/Moscow',
                    })}
                  </td>
                  {showBot && <td>{t.botName ?? botName}</td>}
                  <td className="table-symbol">
                    {t.symbol ?? symbol}
                    {t.simulated && (
                      <span
                        className="sim-tag"
                        title="Сделка живой симуляции, не реальное исполнение"
                      >
                        SIM
                      </span>
                    )}
                  </td>
                  <td>
                    <span className={`side ${t.side}`}>{t.side === 'long' ? 'Лонг' : 'Шорт'}</span>
                  </td>
                  <td className="mono">
                    {t.entry.toFixed(4)} <span className="muted">→</span> {t.exit.toFixed(4)}
                  </td>
                  <td>{t.quantity}</td>
                  <td>{t.fee.toFixed(3)}</td>
                  <td className={net(t) >= 0 ? 'positive' : 'negative'}>
                    {formatMoney(net(t), true)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <div className="table-note">
        {
          'Сделки живой симуляции: исполнения смоделированы по публичным данным, реальных ордеров не было.'
        }{' '}
        Комиссии и результаты указаны в USDT.
      </div>
    </section>
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

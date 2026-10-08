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
  Square,
  Trash2,
  TrendingUp,
  Wallet,
  Waypoints,
  X,
  Zap,
} from 'lucide-react';
import { Chart, DownloadButton, Empty, Modal, SelectField, Toast, Tooltip } from './components';
import {
  createDemo,
  csvCell,
  exchanges,
  exportConfig,
  formatMoney,
  importConfig,
  makeBot,
  metrics,
  net,
  parseWorkspace,
  periodTrades,
  statusLabels,
  STORAGE_KEY,
  validateConfig,
  type Bot,
  type BotConfig,
  type Status,
  type Trade,
  type Workspace,
} from './model';

type Page = 'overview' | 'bots' | 'trades' | 'events' | 'connections';
type Tab = 'overview' | 'trades' | 'settings' | 'events';
const nav: { id: Page; label: string; icon: typeof BotIcon }[] = [
  { id: 'overview', label: 'Обзор', icon: LayoutDashboard },
  { id: 'bots', label: 'Мои боты', icon: BotIcon },
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
function download(name: string, text: string, type = 'application/json') {
  const url = URL.createObjectURL(new Blob([text], { type }));
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
function load() {
  try {
    const text = localStorage.getItem(STORAGE_KEY);
    return { workspace: text ? parseWorkspace(text) : createDemo(), error: '' };
  } catch {
    return {
      workspace: createDemo(),
      error:
        'Сохранённые данные недоступны или повреждены. Показан демопример; сохранение отключено до сброса в разделе «Подключения».',
    };
  }
}
export default function App() {
  const [initial] = useState(load);
  const [workspace, setWorkspace] = useState<Workspace>(initial.workspace);
  const [storageError, setStorageError] = useState(initial.error);
  const [storageEnabled, setStorageEnabled] = useState(!initial.error);
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
  const [confirm, setConfirm] = useState<{
    type: 'delete' | 'stop' | 'reset';
    id?: string;
  } | null>(null);
  const [toast, setToast] = useState('');
  const toastTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const bot = workspace.bots.find((b) => b.id === selected);
  const allTrades = workspace.bots.flatMap((b) =>
    b.trades.map((t) => ({ ...t, botName: b.name, symbol: b.symbol })),
  );
  const trades = periodTrades(bot?.trades ?? allTrades, days);
  const stat = metrics(trades);
  const budget = bot?.budget ?? workspace.bots.reduce((s, b) => s + b.budget, 0);
  const visibleBots = workspace.bots.filter(
    (b) =>
      `${b.name} ${b.symbol} ${b.exchange}`.toLowerCase().includes(query.toLowerCase()) &&
      (filter === 'all' || b.status === filter),
  );
  useEffect(() => {
    if (!storageEnabled) return;
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(workspace));
      setStorageError('');
    } catch {
      setStorageError(
        'Не удалось сохранить изменения в браузере. Экспортируйте настройки перед закрытием вкладки.',
      );
    }
  }, [workspace, storageEnabled]);
  useEffect(
    () => () => {
      if (toastTimer.current) clearTimeout(toastTimer.current);
    },
    [],
  );
  function notify(message: string) {
    setToast(message);
    if (toastTimer.current) clearTimeout(toastTimer.current);
    toastTimer.current = setTimeout(() => setToast(''), 4500);
  }
  function log(state: Workspace, name: string, text: string, botId: string) {
    return {
      ...state,
      events: [
        {
          id: crypto.randomUUID(),
          at: new Date().toISOString(),
          botName: name,
          botId,
          text,
        },
        ...state.events,
      ].slice(0, 200),
    };
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
  function setStatus(b: Bot, status: Status) {
    setWorkspace((s) =>
      log(
        {
          ...s,
          bots: s.bots.map((x) => (x.id === b.id ? { ...x, status } : x)),
        },
        b.name,
        `${statusLabels[status]}. Изменён только статус интерфейса; торговый движок не подключён.`,
        b.id,
      ),
    );
    notify(
      status === 'running'
        ? 'Демо запущено. Реальные заявки не отправляются.'
        : 'Статус демобота обновлён.',
    );
  }
  function save(config: BotConfig, id?: string) {
    if (!id && workspace.bots.length >= 100)
      throw new Error('В демопространстве можно создать до 100 ботов.');
    const existing = workspace.bots.find((b) => b.id === id);
    if (
      existing &&
      existing.trades.length &&
      (config.exchange !== existing.exchange ||
        config.symbol !== existing.symbol ||
        config.budget !== existing.budget)
    )
      throw new Error(
        'У бота есть история. Создайте копию для смены биржи, инструмента или начального баланса.',
      );
    const next = existing ? { ...existing, ...config } : makeBot(config);
    setWorkspace((s) =>
      log(
        {
          ...s,
          bots: existing ? s.bots.map((b) => (b.id === id ? next : b)) : [...s.bots, next],
        },
        next.name,
        existing ? 'Настройки демобота обновлены.' : 'Создан черновик бота.',
        next.id,
      ),
    );
    setEditor(null);
    setImporting(false);
    openBot(next.id);
    notify(existing ? 'Настройки сохранены' : 'Бот добавлен в рабочее пространство');
  }
  function runConfirm() {
    if (confirm?.type === 'reset') {
      setWorkspace(createDemo());
      setStorageEnabled(true);
      setStorageError('');
      go('overview');
      notify('Демонстрационные данные восстановлены');
    } else {
      const b = workspace.bots.find((x) => x.id === confirm?.id);
      if (b && confirm?.type === 'delete') {
        setWorkspace((s) =>
          log(
            { ...s, bots: s.bots.filter((x) => x.id !== b.id) },
            b.name,
            'Демобот и его локальная история удалены.',
            b.id,
          ),
        );
        if (selected === b.id) go('bots');
        notify('Бот удалён');
      } else if (b) setStatus(b, 'stopped');
    }
    setConfirm(null);
  }
  const actions = (
    <>
      <button className="button secondary" onClick={() => setImporting(true)}>
        <FolderInput size={17} />
        Импорт бота
      </button>
      <button className="button primary" onClick={() => setEditor({ config: defaultConfig })}>
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
            Моё пространство<small>Локальный профиль</small>
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
            <strong>Место для экспериментов</strong>
            <p>Настройте бота и изучите интерфейс без реальных сделок.</p>
            <button onClick={() => go('connections')}>
              О деморежиме <ArrowUpRight size={15} />
            </button>
          </div>
          <div className="sidebar-footer">
            <span className="tiny-dot" />
            Демо-пространство<span>v0.1</span>
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
              ДЕМО
            </span>
            <span className="topbar-divider" />
            <span className="profile" title="Локальный профиль">
              M
            </span>
          </div>
        </header>
        <main>
          <div className="demo-banner">
            <FlaskConical size={16} />
            <span>
              <strong>Демонстрационный режим.</strong> Данные вымышлены. Подключений к биржам и
              реальных сделок нет.
            </span>
            <button onClick={() => go('connections')}>
              Подробнее
              <ArrowRight size={14} />
            </button>
          </div>
          {storageError && (
            <div className="error-banner" role="alert">
              {storageError}
            </div>
          )}
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
                    </p>
                  </div>
                </div>
                <div className="heading-actions">
                  <StatusBadge status={bot.status} />
                  {bot.status === 'running' ? (
                    <button className="button secondary" onClick={() => setStatus(bot, 'paused')}>
                      <Pause size={16} />
                      Пауза
                    </button>
                  ) : (
                    <button className="button primary" onClick={() => setStatus(bot, 'running')}>
                      <Play size={16} />
                      Запустить демо
                    </button>
                  )}
                  <button
                    className="icon-button bordered danger-icon"
                    title="Остановить и закрыть"
                    aria-label="Остановить и закрыть"
                    onClick={() => setConfirm({ type: 'stop', id: bot.id })}
                  >
                    <Square size={16} />
                  </button>
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
                  <div className="section-line">
                    <span className="muted">Статистика демопримера</span>
                    {period}
                  </div>
                  <Stats stat={stat} budget={budget} />
                  <div className="overview-grid">
                    <Performance trades={trades} pnl={stat.pnl} />
                    <div className="panel bot-facts">
                      <h3>Параметры бота</h3>
                      <Fact label="Стратегия" value="Импульс" />
                      <Fact label="Версия" value="0.1.0 · шаблон" />
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
                    <p className="muted">Только демонстрационные исполнения</p>
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
                  />
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
                        bots: 'Создавайте, настраивайте и сравнивайте свои стратегии.',
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
              {page === 'overview' && (
                <>
                  <div className="section-line">
                    <span className="section-kicker">
                      <span className="tiny-dot" />
                      ДЕМОНСТРАЦИОННАЯ СТАТИСТИКА
                    </span>
                    {period}
                  </div>
                  <Stats stat={stat} budget={budget} />
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
                        label="Демо запущено"
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
                        onStatus={() => setStatus(b, b.status === 'running' ? 'paused' : 'running')}
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
                    <ShieldCheck size={14} /> Данные хранятся в этом браузере. Демоистория: 30
                    сентября — 7 октября 2026.
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
                        {Object.entries(statusLabels).map(([key, label]) => (
                          <option key={key} value={key}>
                            {label}
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
                          onStatus={() =>
                            setStatus(b, b.status === 'running' ? 'paused' : 'running')
                          }
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
                    <span className="muted">Вымышленные сделки · не торговый отчёт</span>
                    {period}
                  </div>
                  <TradesTable trades={periodTrades(allTrades, days)} showBot />
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
                          Настройки сохраняются локально. Секретные ключи здесь не запрашиваются.
                        </p>
                      </div>
                      <ShieldCheck size={25} className="positive" />
                    </div>
                    <div className="inline-note">
                      <FlaskConical size={18} />
                      <span>
                        Деморежим проверяет только интерфейс. Статусы не запускают алгоритм, не
                        получают рыночные данные и не создают сделки. Выбранный тикер — пример, а не
                        рекомендация.
                      </span>
                    </div>
                    <button className="button danger" onClick={() => setConfirm({ type: 'reset' })}>
                      Сбросить демопространство
                    </button>
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
            Интерфейс v0.1 <span className="dot-separator">·</span> Движок не подключён
          </span>
        </footer>
      </div>
      {editor && (
        <Modal
          title={editor.id ? 'Настроить бота' : 'Создать нового бота'}
          subtitle="Начните с шаблона. Все настройки можно изменить позже."
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
        <Modal
          title={
            confirm.type === 'delete'
              ? 'Удалить бота?'
              : confirm.type === 'reset'
                ? 'Восстановить демопример?'
                : 'Остановить демобота?'
          }
          onClose={() => setConfirm(null)}
        >
          <p className="confirm-text">
            {confirm.type === 'delete'
              ? 'Настройки и история этого бота будут удалены из браузера. При необходимости сначала экспортируйте настройки.'
              : confirm.type === 'reset'
                ? 'Все добавленные боты, настройки и журнал будут заменены исходным демонстрационным примером.'
                : 'Демобот перейдёт в статус «Остановлен». Реальных позиций и ордеров в этой версии нет.'}
          </p>
          <div className="modal-actions">
            <button className="button secondary" onClick={() => setConfirm(null)}>
              Отмена
            </button>
            <button
              className={`button ${confirm.type === 'stop' ? 'primary' : 'danger'}`}
              onClick={runConfirm}
            >
              {confirm.type === 'delete'
                ? 'Удалить'
                : confirm.type === 'reset'
                  ? 'Сбросить данные'
                  : 'Остановить'}
            </button>
          </div>
        </Modal>
      )}
      {toast && <Toast message={toast} />}
    </div>
  );
}
function StatusBadge({ status }: { status: Status }) {
  return (
    <span className={`status-badge ${status}`}>
      <span />
      {statusLabels[status]}
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
function Stats({ stat, budget }: { stat: ReturnType<typeof metrics>; budget: number }) {
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
        label="Выделено ботам"
        value={formatMoney(budget)}
        unit="USDT"
        icon={<Wallet size={18} />}
        note="Виртуальный начальный баланс"
      />
      <Stat
        label="Прибыльных сделок"
        value={stat.winRate === null ? '—' : stat.winRate.toFixed(1).replace('.', ',')}
        unit={stat.winRate === null ? '' : '%'}
        icon={<Gauge size={18} />}
        note={`${stat.count} закрытых демосделок`}
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
          <Tooltip text="Накопленный результат закрытых вымышленных сделок за выбранный период с учётом комиссий." />
        </h3>
        <span className="chart-legend">
          <span className="tiny-dot" />
          Чистый PnL
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
          text="У бота пока нет сделок за выбранный период."
        />
      )}
    </section>
  );
}
function BotCard({
  bot,
  onOpen,
  onStatus,
  onDuplicate,
}: {
  bot: Bot;
  onOpen: () => void;
  onStatus: () => void;
  onDuplicate: () => void;
}) {
  const stat = metrics(bot.trades);
  return (
    <article className="panel bot-card">
      <div className="bot-card-header">
        <span className="bot-icon">
          <Zap size={21} />
        </span>
        <button className="bot-name" onClick={onOpen}>
          {bot.name}
          <small>
            Импульс <span>v0.1.0</span>
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
          <span>Баланс</span>
          <strong>
            {formatMoney(bot.budget)} <small>USDT</small>
          </strong>
        </div>
      </div>
      <div className="bot-card-footer">
        <StatusBadge status={bot.status} />
        <div>
          <button
            className="icon-button"
            aria-label={`${bot.status === 'running' ? 'Приостановить' : 'Запустить демо'} ${bot.name}`}
            onClick={onStatus}
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
  historyLocked = false,
}: {
  config: BotConfig;
  onSave: (c: BotConfig) => void;
  submitLabel: string;
  onCancel?: () => void;
  historyLocked?: boolean;
}) {
  const [form, setForm] = useState(config);
  const [error, setError] = useState('');
  const errorId = useId();
  useEffect(() => {
    setForm(config);
    setError('');
  }, [config]);
  function submit(e: FormEvent) {
    e.preventDefault();
    try {
      const { name, exchange, symbol, budget, lossLimit, strategy, strategyVersion } = form;
      onSave(
        validateConfig({
          name,
          exchange,
          symbol,
          budget,
          lossLimit,
          strategy,
          strategyVersion,
        }),
      );
      setError('');
    } catch (e) {
      setError((e as Error).message);
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
          placeholder="Например, Импульс · ARB"
          onChange={(e) => setForm({ ...form, name: e.target.value })}
          required
        />
      </label>
      <div className="strategy-option">
        <span className="bot-icon">
          <Zap size={22} />
        </span>
        <div>
          <strong>Импульс</strong>
          <p>Шаблон стратегии · алгоритм ещё не реализован</p>
        </div>
        <Check size={18} />
      </div>
      <div className="form-grid">
        <SelectField
          label="Биржа"
          value={form.exchange}
          onChange={(exchange) => setForm({ ...form, exchange: exchange as BotConfig['exchange'] })}
        >
          {exchanges.map((x) => (
            <option key={x}>{x}</option>
          ))}
        </SelectField>
        <label className="field">
          <span>USDT-фьючерс</span>
          <input
            value={form.symbol}
            onChange={(e) => setForm({ ...form, symbol: e.target.value.toUpperCase() })}
            placeholder="ARBUSDT"
            required
          />
        </label>
        <label className="field">
          <span>Демо-баланс, USDT</span>
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
      {historyLocked && (
        <p className="field-hint">
          Биржу, инструмент и начальный баланс бота с историей можно изменить в новой копии бота.
        </p>
      )}
      <div className="inline-note">
        <FlaskConical size={17} />
        <span>
          Создаётся демобот. Доступность инструмента, минимальный ордер и достаточность маржи на
          бирже ещё не проверяются.
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
        <button className="button primary" type="submit">
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
  onImport: (c: BotConfig) => void;
}) {
  const [text, setText] = useState('');
  const [error, setError] = useState('');
  const errorId = useId();
  const [filename, setFilename] = useState('');
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
          disabled={!text.trim()}
          onClick={() => {
            try {
              onImport(importConfig(text));
            } catch (e) {
              setError((e as Error).message);
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
      'demo-trades.csv',
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
          text="После подключения движка здесь будут входы, выходы и результат каждой сделки."
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
                  <td className="table-symbol">{t.symbol ?? symbol}</td>
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
        Вымышленные исполнения. Комиссии и результаты указаны в USDT.
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

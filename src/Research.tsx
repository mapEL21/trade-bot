import { useEffect, useRef, useState, type FormEvent } from 'react';
import { ArrowRight, Database, Radio, Square, Play, Clock3 } from 'lucide-react';
import { api } from './api';

interface Quote {
  bid: string;
  ask: string;
  bidSize: string;
  askSize: string;
  mid: string;
  spreadBps: number;
  receivedUtc: string;
  ageMs: number;
  stale: boolean;
}
interface Feed {
  id: string;
  status: string;
  messages: number;
  gaps: number;
  lastEventUtc: string | null;
  eventAgeMs: number | null;
  quote: Quote | null;
}
interface Snapshot {
  id: string | null;
  state: string;
  active: boolean;
  coin: string | null;
  symbol: string | null;
  seconds: number;
  maxMb?: number;
  elapsedSeconds: number;
  written: number;
  bytes: number;
  gaps: number;
  directory: string | null;
  error: string | null;
  rawDifferencePercent: number | null;
  feeds: Feed[];
  events: { at: string; source: string; text: string }[];
  executionEnabled: boolean;
}
const sessionLabels: Record<string, string> = {
  idle: 'Запись не запущена',
  starting: 'Проверяем инструмент',
  recording: 'Идёт запись',
  stopping: 'Завершаем запись',
  completed: 'Запись завершена',
  'completed-with-gaps': 'Завершена с разрывами',
  'no-data': 'Не все потоки дали данные',
  failed: 'Ошибка записи',
  stopped: 'Запись остановлена',
};
const feedLabels: Record<string, string> = {
  idle: 'Ожидает запуска',
  connecting: 'Подключается',
  connected: 'Ожидает данные',
  live: 'Получает данные',
  stale: 'Давно нет событий',
  disconnected: 'Переподключается',
  invalid: 'Ошибка формата',
  stopped: 'Остановлен',
};
const feedNames: Record<string, string> = {
  hyperliquid: 'Hyperliquid',
  'binance-public': 'Binance · котировки',
  'binance-market': 'Binance · сделки',
};
const quantity = (n: number) => n.toLocaleString('ru-RU', { maximumFractionDigits: 0 });
const price = (s: string) => Number(s).toLocaleString('ru-RU', { maximumFractionDigits: 12 });
const duration = (n: number) =>
  `${Math.floor(n / 60)}:${String(Math.floor(n % 60)).padStart(2, '0')}`;

export function Research({ demo }: { demo: boolean }) {
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [receipt, setReceipt] = useState<number | null>(null);
  const [now, setNow] = useState(() => performance.now());
  const [streamError, setStreamError] = useState(false);
  const [error, setError] = useState('');
  const [coin, setCoin] = useState('ARB');
  const [seconds, setSeconds] = useState('60');
  const [maxMb, setMaxMb] = useState('256');
  const [busy, setBusy] = useState(false);
  const pending = useRef(false);
  const mounted = useRef(false);
  const incoming = useRef<string | null>(null);
  const openedAt = useRef(performance.now());

  useEffect(() => {
    mounted.current = true;
    if (demo)
      return () => {
        mounted.current = false;
      };
    const source = new EventSource('/api/v1/research/stream');
    source.onmessage = (event) => {
      try {
        const value = JSON.parse(event.data) as Snapshot;
        if (
          typeof value.active !== 'boolean' ||
          !Array.isArray(value.feeds) ||
          value.executionEnabled !== false
        )
          throw new Error('Invalid research state');
        const received = performance.now();
        setSnapshot(value);
        setReceipt(received);
        setNow(received);
        setStreamError(false);
        // Initialise controls on return, without overwriting user edits on every update.
        if (incoming.current !== value.id && value.active && value.coin) {
          setCoin(value.coin);
          setSeconds(String(value.seconds));
          if (value.maxMb) setMaxMb(String(value.maxMb));
        }
        incoming.current = value.id;
      } catch {
        setStreamError(true);
      }
    };
    source.onerror = () => setStreamError(true);
    const timer = setInterval(() => setNow(performance.now()), 500);
    return () => {
      mounted.current = false;
      source.close();
      clearInterval(timer);
    };
  }, [demo]);

  if (demo)
    return (
      <section className="panel research-empty">
        <Radio size={30} aria-hidden="true" />
        <h2>Исследование работает на сервере</h2>
        <p>
          Откройте серверное пространство, чтобы получать реальные котировки и записывать рынок.
        </p>
        <a className="button primary" href="/">
          Открыть серверное пространство <ArrowRight size={16} />
        </a>
      </section>
    );
  const delay = receipt === null ? 0 : Math.max(0, now - receipt);
  const offline = streamError || (receipt !== null ? delay > 3000 : now - openedAt.current > 10000);
  const ready = snapshot !== null && !offline;
  const active = snapshot?.active ?? false;
  const fresh = (q: Quote | null | undefined) =>
    ready && snapshot?.state === 'recording' && !!q && !q.stale && q.ageMs + delay <= 3000;
  const hl = snapshot?.feeds.find((f) => f.id === 'hyperliquid');
  const bn = snapshot?.feeds.find((f) => f.id === 'binance-public');
  const showDifference =
    fresh(hl?.quote) && fresh(bn?.quote) && snapshot?.rawDifferencePercent != null;
  const status = offline
    ? 'Связь с сервером потеряна'
    : snapshot
      ? (sessionLabels[snapshot.state] ?? snapshot.state)
      : 'Подключаемся к серверу…';
  const elapsed = snapshot
    ? snapshot.elapsedSeconds +
      (active && snapshot.state !== 'starting' && !offline ? delay / 1000 : 0)
    : 0;

  async function start(event: FormEvent) {
    event.preventDefault();
    if (pending.current || !ready || active) return;
    if (
      !/^[A-Z][A-Z0-9]{0,19}$/.test(coin) ||
      !Number.isInteger(Number(seconds)) ||
      Number(seconds) < 1 ||
      Number(seconds) > 3600 ||
      !Number.isInteger(Number(maxMb)) ||
      Number(maxMb) < 1 ||
      Number(maxMb) > 1024
    ) {
      setError('Проверьте тикер, длительность (1–3600 секунд) и лимит файла (1–1024 МБ).');
      return;
    }
    pending.current = true;
    setBusy(true);
    setError('');
    try {
      await api.startResearch({ coin, seconds: Number(seconds), maxMb: Number(maxMb) });
    } catch (e) {
      if (mounted.current) setError((e as Error).message);
    } finally {
      pending.current = false;
      if (mounted.current) setBusy(false);
    }
  }
  async function stop() {
    if (pending.current || !snapshot?.id || !ready) return;
    pending.current = true;
    setBusy(true);
    setError('');
    try {
      await api.stopResearch(snapshot.id);
    } catch (e) {
      if (mounted.current) setError((e as Error).message);
    } finally {
      pending.current = false;
      if (mounted.current) setBusy(false);
    }
  }

  return (
    <div className="research-page">
      <section className="panel research-control" aria-labelledby="capture-title">
        <div className="research-section-top">
          <div>
            <div className="eyebrow">ПУБЛИЧНЫЕ ДАННЫЕ · БЕЗ СДЕЛОК</div>
            <h2 id="capture-title">Наблюдаем. Записываем. Проверяем.</h2>
          </div>
          <span
            className={`research-status ${ready && snapshot?.state === 'recording' ? 'is-live' : ''}`}
            role="status"
          >
            <Radio size={14} aria-hidden="true" />
            {status}
          </span>
        </div>
        <p className="muted research-intro">
          Hyperliquid → Binance perpetual. Ключи не нужны. Закрытие вкладки не останавливает
          серверную запись.
        </p>
        <form className="research-form" onSubmit={(e) => void start(e)}>
          <label className="field">
            <span>Тикер альткоина</span>
            <input
              value={coin}
              onChange={(e) => setCoin(e.target.value.toUpperCase())}
              disabled={active || busy}
              required
              maxLength={20}
              pattern="[A-Z][A-Z0-9]{0,19}"
              aria-describedby="research-form-note"
            />
          </label>
          <label className="field">
            <span>Длительность, сек</span>
            <input
              type="number"
              min="1"
              max="3600"
              step="1"
              value={seconds}
              onChange={(e) => setSeconds(e.target.value)}
              disabled={active || busy}
              required
            />
          </label>
          <label className="field">
            <span>Лимит файла, МБ</span>
            <input
              type="number"
              min="1"
              max="1024"
              step="1"
              value={maxMb}
              onChange={(e) => setMaxMb(e.target.value)}
              disabled={active || busy}
              required
            />
          </label>
          <div className="research-buttons">
            <button className="button primary" type="submit" disabled={!ready || active || busy}>
              <Play size={16} aria-hidden="true" />
              {busy && !active ? 'Запускаем…' : 'Начать запись'}
            </button>
            <button
              className="button secondary"
              type="button"
              onClick={() => void stop()}
              disabled={!ready || !active || busy || snapshot?.state === 'stopping'}
            >
              <Square size={14} aria-hidden="true" />
              Остановить
            </button>
          </div>
        </form>
        <p id="research-form-note" className="field-hint">
          Проверим пару {coin || 'TOKEN'} / {coin || 'TOKEN'}USDT на обеих площадках. ARB — пример
          для проверки подключения, не выбранный инструмент стратегии.
        </p>
        {error && (
          <p className="form-error" role="alert">
            {error}
          </p>
        )}
        {snapshot?.error && (
          <p className="form-error" role="alert">
            {snapshot.error}
          </p>
        )}
        {offline && (
          <p className="form-error" role="alert">
            Не получаем актуальное состояние. Серверная запись могла продолжиться. Управление и
            сравнение цен недоступны до восстановления связи.
          </p>
        )}
      </section>
      <div className="research-stats">
        <div className="panel">
          <Clock3 size={17} aria-hidden="true" />
          <span>Время записи</span>
          <strong>{duration(elapsed)}</strong>
          <small>
            {snapshot?.coin
              ? `${snapshot.coin} / ${snapshot.symbol}`
              : 'Инструмент ещё не проверен'}
          </small>
        </div>
        <div className="panel">
          <Radio size={17} aria-hidden="true" />
          <span>Событий сохранено</span>
          <strong>{quantity(snapshot?.written ?? 0)}</strong>
          <small>Включая служебные сообщения</small>
        </div>
        <div className="panel">
          <Database size={17} aria-hidden="true" />
          <span>Объём записи</span>
          <strong>
            {((snapshot?.bytes ?? 0) / 1048576).toFixed(2)} <small>МБ</small>
          </strong>
          <small>Исходные сообщения на сервере</small>
        </div>
        <div className="panel">
          <Radio size={17} aria-hidden="true" />
          <span>Разрывы соединений</span>
          <strong className={snapshot?.gaps ? 'negative' : ''}>{snapshot?.gaps ?? 0}</strong>
          <small>Отмечаются в записи</small>
        </div>
      </div>
      <section className="research-quotes" aria-label="Котировки площадок">
        {[
          { title: 'Hyperliquid', subtitle: snapshot?.coin ?? 'Источник сигнала', feed: hl },
          { title: 'Binance', subtitle: snapshot?.symbol ?? 'Рынок исполнения', feed: bn },
        ].map(({ title, subtitle, feed }) => {
          const q = feed?.quote;
          const live = fresh(q);
          return (
            <article className="panel research-quote" key={title}>
              <div className="research-section-top">
                <div>
                  <h2>{title}</h2>
                  <p className="muted">{subtitle} · perpetual</p>
                </div>
                <span className={`research-status ${live ? 'is-live' : ''}`}>
                  {live ? 'Свежая котировка' : q ? 'Последняя котировка' : 'Ожидаем котировку'}
                </span>
              </div>
              <div className={`research-price-grid ${!live ? 'quote-inactive' : ''}`}>
                <div>
                  <span>Bid · покупка</span>
                  <strong>{q ? price(q.bid) : '—'}</strong>
                  <small>Объём: {q ? price(q.bidSize) : '—'}</small>
                </div>
                <div>
                  <span>Ask · продажа</span>
                  <strong>{q ? price(q.ask) : '—'}</strong>
                  <small>Объём: {q ? price(q.askSize) : '—'}</small>
                </div>
              </div>
              <div className="research-quote-footer">
                <span>
                  Спред: <strong>{q ? `${q.spreadBps.toFixed(2)} б.п.` : '—'}</strong>
                </span>
                <span>
                  Возраст BBO: <strong>{q ? `${quantity(q.ageMs + delay)} мс` : '—'}</strong>
                </span>
              </div>
              <p className="field-hint">
                {q
                  ? `Получено сервером: ${new Date(q.receivedUtc).toLocaleTimeString('ru-RU', { timeZone: 'Europe/Moscow' })} МСК`
                  : 'Цена появится после запуска записи и получения BBO.'}
              </p>
            </article>
          );
        })}
      </section>
      <section className="panel research-difference" aria-labelledby="difference-title">
        <div>
          <h2 id="difference-title">Сырая разница средних цен</h2>
          <p className="muted">(Mid Hyperliquid / Mid Binance − 1) × 100%</p>
        </div>
        <strong className="research-difference-value" data-testid="research-difference">
          {showDifference
            ? `${snapshot!.rawDifferencePercent! > 0 ? '+' : ''}${snapshot!.rawDifferencePercent!.toFixed(4)}%`
            : '—'}
        </strong>
        <p>
          {showDifference
            ? 'Без поправки на валюту котировки, базис, комиссии и задержки. Это не сигнал для входа.'
            : 'Для сравнения нужны свежие BBO обеих площадок и связь с сервером.'}
        </p>
      </section>
      <section className="panel research-feeds" aria-labelledby="feeds-title">
        <h2 id="feeds-title">Состояние потоков</h2>
        <div className="research-feed-list">
          {Object.entries(feedNames).map(([id, name]) => {
            const f = snapshot?.feeds.find((item) => item.id === id);
            const age = f?.eventAgeMs == null ? null : f.eventAgeMs + delay;
            const label = offline
              ? 'Нет связи с сервером'
              : !f
                ? 'Ожидает запуска'
                : f.status === 'live' && age !== null && age > 3000
                  ? 'Давно нет событий'
                  : (feedLabels[f.status] ?? f.status);
            return (
              <div className="research-feed" key={id}>
                <strong>{name}</strong>
                <span>{label}</span>
                <span>{quantity(f?.messages ?? 0)} сообщений</span>
              </div>
            );
          })}
        </div>
        <p className="field-hint">
          Возраст данных — время с получения последнего события, а не сетевая задержка. Экран
          обновляется раз в секунду; запись идёт независимо от него.
        </p>
      </section>
      <section className="panel research-journal" aria-labelledby="research-journal-title">
        <h2 id="research-journal-title">Журнал текущей сессии</h2>
        {snapshot?.events.length ? (
          <ul>
            {snapshot.events.map((event, i) => (
              <li key={`${event.at}-${i}`}>
                <time>
                  {new Date(event.at).toLocaleTimeString('ru-RU', { timeZone: 'Europe/Moscow' })}
                </time>
                <div>
                  <strong>{feedNames[event.source] ?? 'Регистратор'}</strong>
                  <p>{event.text}</p>
                </div>
              </li>
            ))}
          </ul>
        ) : (
          <p className="muted">После запуска здесь появятся подключения и события записи.</p>
        )}
        {snapshot?.directory && (
          <p className="research-path">
            Каталог записи: <code>{snapshot.directory}</code>
          </p>
        )}
        <p className="field-hint">
          После перезапуска сервера монитор начнётся с пустой сессии. Уже записанные файлы останутся
          на диске.
        </p>
      </section>
    </div>
  );
}

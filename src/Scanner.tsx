import { useEffect, useState, type FormEvent } from 'react';
import { Play, ScanSearch, Square } from 'lucide-react';
import {
  api,
  type StorageStatus,
  type ScanCandidate,
  type ScanLevel,
  type ScanSnapshot,
  type ScanSymbolDetail,
} from './api';
import { ScanStatsPanel } from './ScanStats';

const durations: [number, string][] = [
  [1800, '30 минут'],
  [3600, '1 час'],
  [7200, '2 часа'],
];
const states: Record<ScanSnapshot['state'], string> = {
  idle: 'Не запущен',
  starting: 'Запускается',
  scanning: 'Сканирует',
  finished: 'Завершён',
  stopped: 'Остановлен',
  failed: 'Ошибка',
  'no-data': 'Нет данных',
};
const results: Record<ScanCandidate['result'], string> = {
  open: 'Отслеживается',
  target: 'Дошёл до цели',
  stop: 'Вернулся за уровень',
  timeout: 'Время вышло',
};
const live = (state: ScanSnapshot['state']) => state === 'starting' || state === 'scanning';
const price = (n: number | null | undefined) =>
  n == null ? '—' : String(Number(n.toPrecision(7)));
const pct = (n: number, signed = true) =>
  `${signed && n > 0 ? '+' : ''}${n.toLocaleString('ru-RU', { maximumFractionDigits: 2 })}%`;
const moscow = (ms: number) =>
  new Date(ms).toLocaleTimeString('ru-RU', { timeZone: 'Europe/Moscow' });
const gb = (bytes: number) =>
  `${(bytes / 1073741824).toLocaleString('ru-RU', { maximumFractionDigits: 1 })} ГБ`;
const levelText = (l: ScanLevel | null) =>
  l ? `${price(l.price)} · ${l.touches} кас. · ${pct(l.distancePct, false)}` : '—';

// The level scanner: which coins it watches, the levels it sees, every breakout with its score and what price did next.
// It only observes public data; trading on these breakouts is a later step.
export function Scanner() {
  const [snapshot, setSnapshot] = useState<ScanSnapshot | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [seconds, setSeconds] = useState(3600);
  const [minVolume, setMinVolume] = useState(150);
  const [maxSymbols, setMaxSymbols] = useState(20);
  const [exclude, setExclude] = useState('');
  const [continuous, setContinuous] = useState(false);
  const [storage, setStorage] = useState<StorageStatus | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [detail, setDetail] = useState<ScanSymbolDetail | null>(null);
  const running = snapshot ? live(snapshot.state) : false;

  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    async function poll() {
      try {
        const data = await api.scanner();
        if (stopped) return;
        setSnapshot(data);
        setError('');
        timer = setTimeout(poll, live(data.state) ? 2000 : 10000);
      } catch (e) {
        if (!stopped) {
          setError((e as Error).message);
          timer = setTimeout(poll, 5000);
        }
      }
    }
    void poll();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    api
      .storage(controller.signal)
      .then(setStorage)
      .catch(() => setStorage(null));
    return () => controller.abort();
  }, [snapshot?.id]);

  useEffect(() => {
    if (!selected) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    async function load() {
      try {
        const data = await api.scannerSymbol(selected!);
        if (!stopped) setDetail(data);
      } catch {
        if (!stopped) setDetail(null);
      }
      if (!stopped && running) timer = setTimeout(load, 5000);
    }
    void load();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, [selected, running]);

  async function act(action: () => Promise<unknown>) {
    setBusy(true);
    setError('');
    try {
      await action();
      setSnapshot(await api.scanner());
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  function start(e: FormEvent) {
    e.preventDefault();
    const symbols = exclude
      .toUpperCase()
      .split(/[\s,;]+/)
      .filter(Boolean)
      .map((x) => (x.endsWith('USDT') ? x : `${x}USDT`));
    void act(() =>
      api.startScanner({
        seconds,
        minQuoteVolume: minVolume * 1_000_000,
        maxSymbols,
        exclude: symbols,
        continuous,
      }),
    );
  }

  return (
    <div className="scanner-page">
      <section className="panel scanner-control" aria-labelledby="scanner-title">
        <div className="section-line">
          <h2 id="scanner-title">
            <ScanSearch size={18} aria-hidden="true" /> Сканер пробоев уровня · Binance
          </h2>
          <span className={`research-status ${running ? 'is-live' : ''}`} role="status">
            {snapshot ? states[snapshot.state] : 'Загрузка'}
          </span>
        </div>
        <p className="field-hint">
          Сканер следит за самыми торгуемыми фьючерсами Binance, строит уровни по минутным свечам
          (зона от 2 касаний, ширина зависит от волатильности монеты) и оценивает каждый пробой от 0
          до 100: касания, проторговка, подлёт, активность ленты, крупные принты, плотность за
          уровнем. Затем {snapshot?.settings.outcomeMinutes ?? 10} минут смотрит, дошла ли цена до
          цели +{snapshot?.settings.targetPct ?? 1.5}% или вернулась за уровень. Сделок нет — это
          замер, по которому потом настроим бота.
        </p>
        {!running ? (
          <form className="scanner-form" onSubmit={start}>
            <label className="field">
              <span>Длительность</span>
              <select value={seconds} onChange={(e) => setSeconds(Number(e.target.value))}>
                {durations.map(([value, label]) => (
                  <option key={value} value={value}>
                    {label}
                  </option>
                ))}
              </select>
            </label>
            <label className="field">
              <span>Объём за 24 ч от, млн $</span>
              <input
                type="number"
                min="0"
                step="1"
                value={Number.isNaN(minVolume) ? '' : minVolume}
                onChange={(e) => setMinVolume(e.target.valueAsNumber)}
                required
              />
            </label>
            <label className="field">
              <span>Монет не больше</span>
              <input
                type="number"
                min="1"
                max="60"
                step="1"
                value={Number.isNaN(maxSymbols) ? '' : maxSymbols}
                onChange={(e) => setMaxSymbols(e.target.valueAsNumber)}
                required
              />
            </label>
            <label className="field">
              <span>Исключить</span>
              <input
                value={exclude}
                placeholder="BTC, ETH"
                onChange={(e) => setExclude(e.target.value)}
              />
            </label>
            <label className="checkbox-field">
              <input
                type="checkbox"
                checked={continuous}
                onChange={(e) => setContinuous(e.target.checked)}
              />
              <span>Непрерывно: после окончания сразу новая запись, пока не остановлю</span>
            </label>
            <button className="button primary" disabled={busy}>
              <Play size={16} /> Запустить сканер
            </button>
          </form>
        ) : (
          <div className="scanner-form">
            <span className="muted">
              Событий: {snapshot!.events.toLocaleString('ru-RU')} · запись{' '}
              {(snapshot!.bytes / 1048576).toLocaleString('ru-RU', { maximumFractionDigits: 0 })} МБ
              {snapshot!.ignored > 0 && ` · не разобрано: ${snapshot!.ignored}`}
            </span>
            <button
              className="button secondary"
              disabled={busy}
              onClick={() => void act(() => api.stopScanner(snapshot!.id!))}
            >
              <Square size={15} /> Остановить
            </button>
          </div>
        )}
        {snapshot && snapshot.state !== 'idle' && (
          <p className="field-hint">
            {snapshot.continuous && `Непрерывный режим · запись № ${snapshot.segment}. `}
            {snapshot.explanation}
          </p>
        )}
        {storage?.enabled && (
          <p className="field-hint">
            Диск: свободно {storage.freeBytes != null ? gb(storage.freeBytes) : '—'}
            {storage.totalBytes != null && ` из ${gb(storage.totalBytes)}`} · записи{' '}
            {gb(storage.recordingBytes)} ({storage.recordings}) · хранятся{' '}
            {storage.retentionDays > 0 ? `${storage.retentionDays} дн.` : 'без ограничения'}
            {storage.lastBackup && ` · копия базы ${storage.lastBackup}`}
            {storage.error && ` · ошибка обслуживания: ${storage.error}`}
          </p>
        )}
        {error && <p role="alert">{error}</p>}
        <p className="field-hint">
          Около 1 МБ записи в минуту на монету: час по 20 монетам занимает до 1,5 ГБ на диске.
        </p>
      </section>

      <ScanStatsPanel />

      {snapshot && snapshot.candidates.length > 0 && (
        <section className="panel" aria-labelledby="scanner-candidates">
          <div className="section-line">
            <h3 id="scanner-candidates">
              Пробои <span className="count-badge">{snapshot.candidates.length}</span>
            </h3>
          </div>
          <div className="table-scroll">
            <table>
              <caption className="sr-only">Пробои уровней с оценкой и результатом</caption>
              <thead>
                <tr>
                  <th>Время · МСК</th>
                  <th>Монета</th>
                  <th>Сторона</th>
                  <th>Уровень → цена пробоя</th>
                  <th>Оценка</th>
                  <th>Результат</th>
                  <th>Лучший / худший ход</th>
                </tr>
              </thead>
              <tbody>
                {snapshot.candidates.map((c) => (
                  <tr key={c.id}>
                    <td className="mono">{moscow(c.atMs)}</td>
                    <td>
                      <button className="text-button" onClick={() => setSelected(c.symbol)}>
                        {c.symbol}
                      </button>
                    </td>
                    <td>
                      <span className={`side ${c.side}`}>
                        {c.side === 'long' ? 'Лонг' : 'Шорт'}
                      </span>
                    </td>
                    <td className="mono">
                      {price(c.level)} <span className="muted">→</span> {price(c.price)}
                    </td>
                    <td>
                      <details className="score-details">
                        <summary>
                          <strong>{c.score}</strong> / 100
                        </summary>
                        <ul>
                          {c.items.map((item) => (
                            <li key={item.name}>
                              <span className={item.points >= 0 ? 'positive' : 'negative'}>
                                {item.points > 0 ? '+' : ''}
                                {item.points}
                              </span>{' '}
                              {item.name}: {item.detail}
                            </li>
                          ))}
                        </ul>
                      </details>
                    </td>
                    <td
                      className={
                        c.result === 'target' ? 'positive' : c.result === 'stop' ? 'negative' : ''
                      }
                    >
                      {results[c.result]}
                    </td>
                    <td className="mono">
                      <span className="positive">{pct(c.maxFavorablePct)}</span> /{' '}
                      <span className="negative">{pct(c.maxAdversePct)}</span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}

      {selected && (
        <section className="panel scanner-chart" aria-labelledby="scanner-chart-title">
          <div className="section-line">
            <h3 id="scanner-chart-title">{selected} · минутные свечи и зоны уровней</h3>
            <button className="text-button" onClick={() => setSelected(null)}>
              Закрыть
            </button>
          </div>
          {detail ? <CandleChart detail={detail} /> : <p className="muted">Загружаем…</p>}
        </section>
      )}

      {snapshot && snapshot.symbols.length > 0 && (
        <section className="panel" aria-labelledby="scanner-symbols">
          <div className="section-line">
            <h3 id="scanner-symbols">
              Монеты <span className="count-badge">{snapshot.symbols.length}</span>
            </h3>
            <span className="muted">Ближе к уровню — выше в списке</span>
          </div>
          <div className="table-scroll">
            <table>
              <caption className="sr-only">Монеты сканера и ближайшие уровни</caption>
              <thead>
                <tr>
                  <th>Монета</th>
                  <th>Объём 24 ч</th>
                  <th>Цена</th>
                  <th>Сопротивление</th>
                  <th>Поддержка</th>
                  <th>Пробоев</th>
                </tr>
              </thead>
              <tbody>
                {snapshot.symbols.map((s) => (
                  <tr key={s.symbol}>
                    <td>
                      <button className="text-button" onClick={() => setSelected(s.symbol)}>
                        {s.symbol}
                      </button>
                    </td>
                    <td>{Math.round(s.quoteVolume / 1_000_000).toLocaleString('ru-RU')} млн $</td>
                    <td className="mono">{price(s.price)}</td>
                    <td className="mono">{levelText(s.resistance)}</td>
                    <td className="mono">{levelText(s.support)}</td>
                    <td>{s.candidates}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}
    </div>
  );
}

// Minute candles with level zones and breakout marks; the zone is the band that counted as a touch.
function CandleChart({ detail }: { detail: ScanSymbolDetail }) {
  const candles = detail.candles.slice(-150);
  if (candles.length === 0) return <p className="muted">Свечей пока нет.</p>;
  const width = 900;
  const height = 300;
  const pad = 8;
  const values = [
    ...candles.flatMap((c) => [c.high, c.low]),
    ...detail.levels.flatMap((l) => [l.zoneLow, l.zoneHigh]),
  ];
  const max = Math.max(...values);
  const min = Math.min(...values);
  const y = (v: number) => pad + ((max - v) / (max - min || 1)) * (height - 2 * pad);
  const step = (width - 60) / candles.length;
  const x = (i: number) => i * step + step / 2;
  const index = (ms: number) =>
    candles.findIndex((c) => c.openMs === Math.floor(ms / 60000) * 60000);
  return (
    <svg
      className="candle-chart"
      viewBox={`0 0 ${width} ${height}`}
      role="img"
      aria-label={`${detail.symbol}: ${candles.length} минутных свечей, уровней ${detail.levels.length}, пробоев ${detail.candidates.length}`}
    >
      {detail.levels.map((l) => (
        <g key={`${l.side}-${l.price}`}>
          <rect
            x={0}
            width={width - 60}
            y={y(l.zoneHigh)}
            height={Math.max(1, y(l.zoneLow) - y(l.zoneHigh))}
            className={`zone ${l.side}`}
          />
          <line
            x1={0}
            x2={width - 60}
            y1={y(l.price)}
            y2={y(l.price)}
            className={`level ${l.side}`}
          />
          <text x={width - 56} y={y(l.price) + 4} className="axis">
            {price(l.price)}
          </text>
        </g>
      ))}
      {candles.map((c, i) => {
        const up = c.close >= c.open;
        return (
          <g key={c.openMs} className={up ? 'up' : 'down'}>
            <line x1={x(i)} x2={x(i)} y1={y(c.high)} y2={y(c.low)} />
            <rect
              x={x(i) - Math.max(1, step * 0.35)}
              width={Math.max(2, step * 0.7)}
              y={y(Math.max(c.open, c.close))}
              height={Math.max(1, Math.abs(y(c.open) - y(c.close)))}
            />
          </g>
        );
      })}
      {detail.candidates.map((c) => {
        const i = index(c.atMs);
        if (i < 0) return null;
        const yy = y(c.price);
        const long = c.side === 'long';
        return (
          <polygon
            key={c.id}
            className={`mark ${c.side}`}
            points={
              long
                ? `${x(i)},${yy + 4} ${x(i) - 6},${yy + 14} ${x(i) + 6},${yy + 14}`
                : `${x(i)},${yy - 4} ${x(i) - 6},${yy - 14} ${x(i) + 6},${yy - 14}`
            }
          >
            <title>{`${long ? 'Лонг' : 'Шорт'} · оценка ${c.score} · ${results[c.result]}`}</title>
          </polygon>
        );
      })}
      <text x={width - 56} y={14} className="axis">
        {price(max)}
      </text>
      <text x={width - 56} y={height - 4} className="axis">
        {price(min)}
      </text>
    </svg>
  );
}

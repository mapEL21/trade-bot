import { useEffect, useState, type FormEvent } from 'react';
import { Database, Play, Square } from 'lucide-react';
import { api, type StorageStatus, type ScanSnapshot } from './api';

const durations: [number, string][] = [
  [1800, '30 минут'],
  [3600, '1 час'],
  [7200, '2 часа'],
];
const states: Record<ScanSnapshot['state'], string> = {
  idle: 'Не идёт',
  starting: 'Запускается',
  scanning: 'Пишет',
  finished: 'Завершена',
  stopped: 'Остановлена',
  failed: 'Ошибка',
  'no-data': 'Нет данных',
};
const live = (state: ScanSnapshot['state']) => state === 'starting' || state === 'scanning';
const gb = (bytes: number) =>
  `${(bytes / 1073741824).toLocaleString('ru-RU', { maximumFractionDigits: 1 })} ГБ`;
// The data recorder: writes trades, book and liquidations of the most traded Binance futures for the signal lab.
// It only observes public data and makes no trades.
export function Scanner() {
  const [snapshot, setSnapshot] = useState<ScanSnapshot | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [seconds, setSeconds] = useState(3600);
  const [minVolume, setMinVolume] = useState(150);
  const [maxSymbols, setMaxSymbols] = useState(20);
  const [exclude, setExclude] = useState('');
  const [continuous, setContinuous] = useState(false);
  const [minRange, setMinRange] = useState(4);
  const [storage, setStorage] = useState<StorageStatus | null>(null);
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
        minRangePct: minRange,
      }),
    );
  }

  return (
    <div className="scanner-page">
      <section className="panel scanner-control" aria-labelledby="scanner-title">
        <div className="section-line">
          <h2 id="scanner-title">
            <Database size={18} aria-hidden="true" /> Запись данных · Binance
          </h2>
          <span className={`research-status ${running ? 'is-live' : ''}`} role="status">
            <span className={`live-dot ${running ? 'on' : ''}`} aria-hidden="true" />
            {snapshot ? states[snapshot.state] : 'Загрузка'}
          </span>
        </div>
        <p className="field-hint">
          Записывает сделки, стакан (20 уровней) и ликвидации самых торгуемых фьючерсов Binance, а
          также историю минутных свечей. Из этих записей лаборатория сигналов ищет события и
          считает, что дала бы сделка после комиссий. Сделок нет.
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
              <span>Размах за 24 ч от, %</span>
              <input
                type="number"
                min="0"
                max="100"
                step="0.5"
                value={Number.isNaN(minRange) ? '' : minRange}
                onChange={(e) => setMinRange(e.target.valueAsNumber)}
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
            <p className="field-hint scanner-range-hint">
              Монеты, которые за сутки прошли меньше заданного размаха (обычно BTC и ETH), не
              записываются. На них ход цены мал по сравнению с комиссией: чтобы движение её покрыло,
              нужна большая позиция, а при размере от риска комиссия съедает большую часть этого
              риска.
            </p>
            <button className="button primary" disabled={busy}>
              <Play size={16} /> Начать запись
            </button>
          </form>
        ) : (
          <div className="scanner-form">
            <label className="checkbox-field">
              <input
                type="checkbox"
                checked={snapshot!.continuous ?? false}
                disabled={busy}
                onChange={(e) => {
                  const value = e.target.checked;
                  void act(() => api.setScannerContinuous(snapshot!.id!, value));
                }}
              />
              <span>Авто-продление: после этой записи сразу начать следующую</span>
            </label>
            <span className="muted">
              Событий: {snapshot!.events.toLocaleString('ru-RU')} · запись{' '}
              {(snapshot!.bytes / 1048576).toLocaleString('ru-RU', { maximumFractionDigits: 0 })} МБ
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
          Час записи по 20 монетам — около 0,7 ГБ; завершённые записи сжимаются примерно в 10 раз.
        </p>
      </section>

      {snapshot && snapshot.symbols.length > 0 && (
        <section className="panel" aria-labelledby="scanner-symbols">
          <div className="section-line">
            <h3 id="scanner-symbols">
              Монеты <span className="count-badge">{snapshot.symbols.length}</span>
            </h3>
          </div>
          <div className="table-scroll">
            <table>
              <caption className="sr-only">
                Монеты записи: сколько сделок и ликвидаций получено
              </caption>
              <thead>
                <tr>
                  <th>Монета</th>
                  <th>Объём 24 ч</th>
                  <th>Размах 24 ч</th>
                  <th>Сделок записано</th>
                  <th>Ликвидаций</th>
                </tr>
              </thead>
              <tbody>
                {snapshot.symbols.map((s) => (
                  <tr key={s.symbol}>
                    <td>{s.symbol}</td>
                    <td>{Math.round(s.quoteVolume / 1_000_000).toLocaleString('ru-RU')} млн $</td>
                    <td className="mono">
                      {s.rangePct > 0
                        ? `${s.rangePct.toLocaleString('ru-RU', { maximumFractionDigits: 1 })}%`
                        : '—'}
                    </td>
                    <td className="mono">{s.trades.toLocaleString('ru-RU')}</td>
                    <td className="mono">{s.liquidations.toLocaleString('ru-RU')}</td>
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

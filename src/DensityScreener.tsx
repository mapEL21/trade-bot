import { useEffect, useState } from 'react';
import { Layers, Play, Square } from 'lucide-react';
import { api, type ScreenerSnapshot, type ScreenerWall } from './api';

const statuses: Record<ScreenerWall['status'], string> = {
  stands: 'Стоит',
  near: 'Цена рядом',
  eating: 'Проедают',
  eaten: 'Проели',
  pulled: 'Сняли',
};
const states: Record<string, string> = {
  idle: 'Не запущен',
  starting: 'Запускается',
  running: 'Работает',
  failed: 'Ошибка',
  stopped: 'Остановлен',
};
const num = (n: number, digits = 2) => n.toLocaleString('ru-RU', { maximumFractionDigits: digits });
const usd = (n: number) =>
  n >= 1_000_000 ? `${num(n / 1_000_000, 2)} млн $` : `${num(n / 1000, 1)} тыс. $`;
const minutes = (ms: number) => {
  const m = Math.max(0, Math.round(ms / 60000));
  return m >= 60 ? `${Math.floor(m / 60)} ч ${m % 60} мин` : `${m} мин`;
};
const moscow = (ms: number) =>
  new Date(ms).toLocaleTimeString('ru-RU', {
    timeZone: 'Europe/Moscow',
    hour: '2-digit',
    minute: '2-digit',
  });

// The density screener: what the bots see in the order books of every Binance USDT perpetual — large resting orders,
// how far they are from the price in % and in price steps, how long they stand and what happened to them.
export function DensityScreenerPanel() {
  const [snapshot, setSnapshot] = useState<ScreenerSnapshot | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    async function poll() {
      try {
        const data = await api.screener();
        if (!stopped) {
          setSnapshot(data);
          setError('');
        }
      } catch (e) {
        if (!stopped) setError((e as Error).message);
      }
      if (!stopped) timer = setTimeout(poll, 10000);
    }
    void poll();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, []);
  async function act(action: () => Promise<unknown>) {
    setBusy(true);
    try {
      await action();
      setSnapshot(await api.screener());
      setError('');
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  const running = snapshot?.state === 'running' || snapshot?.state === 'starting';
  const now = Date.now();
  return (
    <div className="signal-lab">
      <section className="panel" aria-labelledby="screener-title">
        <div className="section-line">
          <h2 id="screener-title" className="lab-title">
            <Layers size={18} aria-hidden="true" /> Плотности
          </h2>
          <span className={`research-status ${running ? 'is-live' : ''}`} role="status">
            <span className={`live-dot ${running ? 'on' : ''}`} aria-hidden="true" />
            {snapshot ? (states[snapshot.state] ?? snapshot.state) : 'Загрузка'}
          </span>
        </div>
        <p className="field-hint">
          Крупные заявки в стаканах всех фьючерсов Binance: самый крупный уровень своей стороны в 3%
          от цены, втрое больше следующего, в 10 раз больше среднего и не меньше 10 минут обычной
          торговли монетой. Засчитывается, если простояла два круга обхода подряд. Скринер ничего не
          торгует: его читают боты.
        </p>
        <div className="scanner-form">
          {running ? (
            <button
              className="button secondary"
              disabled={busy}
              onClick={() => void act(() => api.stopScreener())}
            >
              <Square size={15} /> Остановить
            </button>
          ) : (
            <button
              className="button primary"
              disabled={busy}
              onClick={() => void act(() => api.startScreener())}
            >
              <Play size={16} /> Запустить
            </button>
          )}
          {snapshot && (
            <span className="muted">
              {snapshot.explanation}
              {snapshot.roundMs !== null &&
                ` Последний круг закончен в ${moscow(snapshot.roundMs)} МСК${snapshot.roundSeconds !== null ? `, за ${minutes(snapshot.roundSeconds * 1000)}` : ''}.`}
            </span>
          )}
        </div>
        {error && <p role="alert">{error}</p>}
        {snapshot && (
          <div className="quality-grid">
            <div className="quality-item">
              <span>Сейчас в стаканах</span>
              <strong>{snapshot.walls.length}</strong>
              <small>
                из них у цены {snapshot.walls.filter((w) => w.status !== 'stands').length}
              </small>
            </div>
            <div className="quality-item">
              <span>Найдено за сутки</span>
              <strong>{snapshot.day.found}</strong>
              <small>новых плотностей</small>
            </div>
            <div className="quality-item">
              <span>Проели · сняли за сутки</span>
              <strong>
                {snapshot.day.eaten} · {snapshot.day.pulled}
              </strong>
              <small>
                {snapshot.day.medianMinutes !== null
                  ? `обычно стоят ${minutes(snapshot.day.medianMinutes * 60000)}`
                  : 'пока нет завершённых'}
              </small>
            </div>
          </div>
        )}
      </section>
      {snapshot && snapshot.walls.length > 0 && (
        <WallTable
          title="Плотности сейчас"
          caption="Плотности в стаканах сейчас, ближайшие к цене сверху"
          walls={snapshot.walls}
          now={now}
        />
      )}
      {snapshot && snapshot.finished.length > 0 && (
        <WallTable
          title="Чем закончились"
          caption="Последние плотности, которые проели или сняли"
          walls={snapshot.finished}
          now={now}
          finished
        />
      )}
    </div>
  );
}

function WallTable({
  title,
  caption,
  walls,
  now,
  finished = false,
}: {
  title: string;
  caption: string;
  walls: ScreenerWall[];
  now: number;
  finished?: boolean;
}) {
  return (
    <section className="panel">
      <h3>{title}</h3>
      <div className="table-scroll" tabIndex={0} role="region" aria-label={`${title}, прокрутка`}>
        <table>
          <caption className="sr-only">{caption}</caption>
          <thead>
            <tr>
              <th>Монета</th>
              <th>Сторона</th>
              <th>Цена</th>
              <th>Размер</th>
              <th>× среднего уровня</th>
              <th>Минут торговли</th>
              <th>До цены</th>
              <th>{finished ? 'Простояла' : 'Стоит'}</th>
              <th>Статус</th>
            </tr>
          </thead>
          <tbody>
            {walls.map((w) => (
              <tr
                key={`${w.symbol}|${w.side}|${w.price}|${w.firstMs}`}
                className={!finished && w.status !== 'stands' ? 'lab-selected' : ''}
              >
                <td>{w.symbol}</td>
                <td>
                  <span className={`side ${w.side > 0 ? 'long' : 'short'}`}>
                    {w.side > 0 ? 'Покупка' : 'Продажа'}
                  </span>
                </td>
                <td className="mono">{w.price}</td>
                <td className="mono">
                  {usd(w.quote)}
                  {w.peak > w.quote * 1.05 && <small className="muted"> из {usd(w.peak)}</small>}
                </td>
                <td className="mono">{num(w.ratio, 1)}×</td>
                <td className="mono">{num(w.minutes, 1)}</td>
                <td className="mono">
                  {num(w.distancePct)}% · {w.ticks} шаг.
                </td>
                <td>{minutes((finished ? w.seenMs : now) - w.firstMs)}</td>
                <td
                  className={
                    w.status === 'eaten' || w.status === 'eating'
                      ? 'negative'
                      : w.status === 'near'
                        ? 'positive'
                        : ''
                  }
                >
                  {statuses[w.status]}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

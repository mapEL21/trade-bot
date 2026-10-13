import { useEffect, useState } from 'react';
import { RefreshCw } from 'lucide-react';
import { api, type ScanGroup, type ScanStats } from './api';

const pct = (n: number) =>
  `${n > 0 ? '+' : ''}${n.toLocaleString('ru-RU', { maximumFractionDigits: 2 })}%`;
const r = (n: number) =>
  `${n > 0 ? '+' : ''}${n.toLocaleString('ru-RU', { maximumFractionDigits: 2 })} R`;
const share = (part: number, count: number) =>
  count ? `${Math.round((part * 100) / count)}%` : '—';
const tone = (n: number) => (n > 0 ? 'positive' : n < 0 ? 'negative' : '');

function Row({ group }: { group: ScanGroup }) {
  return (
    <tr>
      <td>{group.name}</td>
      <td>{group.count}</td>
      <td>{share(group.targets, group.count)}</td>
      <td>{share(group.stops, group.count)}</td>
      <td>{share(group.timeouts, group.count)}</td>
      <td className={tone(group.meanPct)}>{group.count ? pct(group.meanPct) : '—'}</td>
      <td className={tone(group.meanR)}>{group.count ? r(group.meanR) : '—'}</td>
    </tr>
  );
}

function Groups({ caption, groups }: { caption: string; groups: ScanGroup[] }) {
  return (
    <div className="table-scroll">
      <table>
        <caption className="sr-only">{caption}</caption>
        <thead>
          <tr>
            <th>{caption}</th>
            <th>Пробоев</th>
            <th>До цели</th>
            <th>За уровень</th>
            <th>Время вышло</th>
            <th>Средний результат</th>
            <th>Средний R</th>
          </tr>
        </thead>
        <tbody>
          {groups.map((g) => (
            <Row key={g.name} group={g} />
          ))}
        </tbody>
      </table>
    </div>
  );
}

// Whether the scanner's breakouts carry an edge after fees: all of them, by score band, by side and by each scored sign.
// It answers "is this worth trading", before any bot is built on it.
export function ScanStatsPanel() {
  const [fee, setFee] = useState(0.1);
  const [stats, setStats] = useState<ScanStats | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  async function load(feePct = fee) {
    if (!Number.isFinite(feePct)) return;
    setBusy(true);
    try {
      setStats(await api.scannerStats(feePct));
      setError('');
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  useEffect(() => {
    void load();
    // Loaded once on open; the user refreshes it when more breakouts are collected.
  }, []);

  return (
    <section className="panel scan-stats" aria-labelledby="scan-stats-title">
      <div className="section-line">
        <h3 id="scan-stats-title">Статистика пробоев</h3>
        <div className="scan-stats-controls">
          <label className="field">
            <span>Комиссии круга, %</span>
            <input
              type="number"
              min="0"
              max="1"
              step="0.01"
              value={Number.isNaN(fee) ? '' : fee}
              onChange={(e) => setFee(e.target.valueAsNumber)}
            />
          </label>
          <button className="button secondary" disabled={busy} onClick={() => void load()}>
            <RefreshCw size={15} /> Пересчитать
          </button>
        </div>
      </div>
      {error && <p role="alert">{error}</p>}
      {stats && stats.total === 0 && (
        <p className="muted">
          Пробоев пока нет. Запустите сканер: статистика соберётся из журналов всех записей.
        </p>
      )}
      {stats && stats.total > 0 && (
        <>
          <div className="quality-grid">
            <div className="quality-item">
              <span>Завершённых пробоев</span>
              <strong>{stats.all.count}</strong>
              <small>
                ещё отслеживаются {stats.open} · журналов {stats.journals}
                {stats.unreadable > 0 && ` · не прочитано ${stats.unreadable}`}
              </small>
            </div>
            <div className="quality-item">
              <span>Дошли до цели</span>
              <strong>{share(stats.all.targets, stats.all.count)}</strong>
              <small>
                за уровень {share(stats.all.stops, stats.all.count)} · время вышло{' '}
                {share(stats.all.timeouts, stats.all.count)}
              </small>
            </div>
            <div className="quality-item">
              <span>Средний результат на пробой</span>
              <strong className={tone(stats.all.meanR)}>
                {pct(stats.all.meanPct)} · {r(stats.all.meanR)}
              </strong>
              <small>после комиссий, если входить во все пробои</small>
            </div>
            <div className="quality-item">
              <span>Сумма за все пробои</span>
              <strong className={tone(stats.all.sumR)}>{r(stats.all.sumR)}</strong>
              <small>при риске 1% депозита на сделку это {pct(stats.all.sumR)} депозита</small>
            </div>
          </div>
          <Groups caption="По оценке" groups={stats.byScore} />
          <Groups caption="По стороне" groups={stats.bySide} />
          <div className="table-scroll">
            <table>
              <caption className="sr-only">По признакам оценки</caption>
              <thead>
                <tr>
                  <th>Признак</th>
                  <th>С признаком: пробоев · до цели · средний R</th>
                  <th>Без признака: пробоев · до цели · средний R</th>
                </tr>
              </thead>
              <tbody>
                {stats.features.map((f) => (
                  <tr key={f.name}>
                    <td>{f.name}</td>
                    {[f.with, f.without].map((g, i) => (
                      <td key={i}>
                        {g.count} · {share(g.targets, g.count)} ·{' '}
                        <span className={tone(g.meanR)}>{g.count ? r(g.meanR) : '—'}</span>
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <ul className="field-hint scan-stats-notes">
            {stats.notes.map((n) => (
              <li key={n}>{n}</li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}

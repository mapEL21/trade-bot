import { useEffect, useState, type ReactNode } from 'react';
import { FlaskConical, Play } from 'lucide-react';
import {
  api,
  type HypothesisCheck,
  type LabReport,
  type LabRow,
  type LabSessionRow,
  type LabSpreadRow,
  type LabStatus,
  tradingSessions,
  type Verdict,
} from './api';
import { metrics, type Trade } from './model';
import { Performance, TradesTable } from './TradeAnalysis';

const modes = { follow: 'По движению', fade: 'Против движения' };
const horizons: Record<number, string> = { 5: '5 с', 15: '15 с', 60: '1 мин', 300: '5 мин' };
const pct = (n: number | null) =>
  n === null
    ? '—'
    : `${n > 0 ? '+' : ''}${n.toLocaleString('ru-RU', { maximumFractionDigits: 3 })}%`;
const tone = (n: number | null) => (n === null ? '' : n > 0 ? 'positive' : n < 0 ? 'negative' : '');
const key = (r: LabRow) => `${r.type}|${r.mode}|${r.horizon}`;
const verdicts: Record<Verdict, string> = {
  collecting: 'Собираем данные',
  confirmed: 'Подтверждена',
  rejected: 'Не подтвердилась',
};
const verdictTone: Record<Verdict, string> = {
  collecting: '',
  confirmed: 'positive',
  rejected: 'negative',
};
// The "from" moment of the check is a per-viewer setting, kept in this browser only.
const fromKey = 'trade-bot.lab-from';
function savedFrom() {
  try {
    return localStorage.getItem(fromKey) ?? '';
  } catch {
    return '';
  }
}
// datetime-local value (local time) to epoch ms; empty means all data.
const fromMsOf = (value: string) => (value ? new Date(value).getTime() : null);

// The signal lab: for every event type, trading with or against it, and every holding time — what it would have made after
// fees and spread on the recorded data, and whether that holds on both halves of the events. Research only, no orders.
export function SignalLabPanel() {
  const [status, setStatus] = useState<LabStatus | null>(null);
  const [report, setReport] = useState<LabReport | null>(null);
  const [hypothesis, setHypothesis] = useState<HypothesisCheck | null>(null);
  const [fee, setFee] = useState(0.1);
  const [all, setAll] = useState(false);
  const [from, setFrom] = useState(savedFrom);
  const [session, setSession] = useState('');
  const [selected, setSelected] = useState<LabRow | null>(null);
  const [bySession, setBySession] = useState<LabSessionRow[] | null>(null);
  const [spreads, setSpreads] = useState<LabSpreadRow[] | null>(null);
  const [trades, setTrades] = useState<Trade[] | null>(null);
  const [error, setError] = useState('');

  async function load(feePct = fee, since = from, within = session) {
    if (!Number.isFinite(feePct)) return;
    try {
      const [next, check, spread] = await Promise.all([
        api.labReport(feePct, fromMsOf(since), within || null),
        api.labHypothesis(),
        api.labSpreads(fromMsOf(since)),
      ]);
      setReport(next);
      setHypothesis(check);
      setSpreads(spread);
      setError('');
    } catch (e) {
      setError((e as Error).message);
    }
  }
  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    async function poll() {
      try {
        const next = await api.labStatus();
        if (stopped) return;
        setStatus((previous) => {
          // A run that just finished brings new events: refresh the table.
          if (previous?.state === 'running' && next.state !== 'running') void load();
          return next;
        });
        timer = setTimeout(poll, next.state === 'running' ? 1500 : 10000);
      } catch (e) {
        if (!stopped) setError((e as Error).message);
      }
    }
    void poll();
    void load();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
    // Polling starts once; the fee is applied by the recalculation button.
  }, []);

  useEffect(() => {
    if (!selected) return;
    let stopped = false;
    setBySession(null);
    api
      .labSessions(selected, fee, fromMsOf(from))
      .then((s) => {
        if (!stopped) setBySession(s);
      })
      .catch(() => {});
    api
      .labTrades(selected, fee, fromMsOf(from))
      .then((t) => {
        if (!stopped) setTrades(t);
      })
      .catch((e) => {
        if (!stopped) setError((e as Error).message);
      });
    return () => {
      stopped = true;
    };
  }, [selected, report]);

  async function run() {
    try {
      await api.runLab();
      setStatus(await api.labStatus());
    } catch (e) {
      setError((e as Error).message);
    }
  }

  const running = status?.state === 'running';
  const rows = (report?.rows ?? [])
    .filter((r) => r.episodes > 0 && (all || r.episodes >= 30))
    .sort((a, b) => b.meanPct - a.meanPct);
  const stable = rows.filter((r) => r.stable).length;
  return (
    <div className="signal-lab">
      {hypothesis && <HypothesisPanel check={hypothesis} />}
      <section className="panel" aria-labelledby="lab-title">
        <div className="section-line">
          <h2 id="lab-title" className="lab-title">
            <FlaskConical size={18} aria-hidden="true" /> Лаборатория сигналов
          </h2>
          <span className={`research-status ${running ? 'is-live' : ''}`} role="status">
            <span className={`live-dot ${running ? 'on' : ''}`} aria-hidden="true" />
            {running
              ? `Обработка ${status!.done + 1} из ${status!.total} · строк ${status!.lines.toLocaleString('ru-RU')}`
              : report
                ? `Записей ${report.recordings} · ${report.hours.toLocaleString('ru-RU')} ч · событий ${report.events}`
                : 'Загрузка'}
          </span>
        </div>
        <p className="field-hint">
          Лаборатория проходит записи рынка и находит события: всплеск объёма, перекос покупок и
          продаж, каскад ликвидаций, вынос цены, перекос стакана, пробой уровня. Для каждого
          считает, что дала бы сделка по движению и против него через 5 с, 15 с, 1 мин и 5 мин — со
          спредом и комиссиями. Пороги событий фиксированные: подгонять их под эти же данные нельзя.
        </p>
        <div className="scanner-form">
          <button className="button primary" disabled={running} onClick={() => void run()}>
            <Play size={16} /> Обработать новые записи
          </button>
          <label className="field lab-fee">
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
          <label className="field lab-from">
            <span>Только события с</span>
            <input
              type="datetime-local"
              value={from}
              onChange={(e) => {
                setFrom(e.target.value);
                try {
                  localStorage.setItem(fromKey, e.target.value);
                } catch {
                  // Storage may be unavailable; the choice then lasts for this page only.
                }
              }}
            />
          </label>
          <label className="field lab-session">
            <span>Торговая сессия</span>
            <select
              value={session}
              onChange={(e) => {
                setSession(e.target.value);
                void load(fee, from, e.target.value);
              }}
            >
              <option value="">Все часы</option>
              {tradingSessions.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name}
                </option>
              ))}
            </select>
          </label>
          <button className="button secondary" onClick={() => void load()}>
            Пересчитать
          </button>
          {from && (
            <button
              className="text-button"
              onClick={() => {
                setFrom('');
                try {
                  localStorage.removeItem(fromKey);
                } catch {
                  // See above.
                }
                void load(fee, '');
              }}
            >
              Все данные
            </button>
          )}
          <label className="checkbox-field">
            <input type="checkbox" checked={all} onChange={(e) => setAll(e.target.checked)} />
            <span>Показывать и редкие (меньше 30 эпизодов)</span>
          </label>
        </div>
        {status && status.state !== 'idle' && !running && (
          <p className="field-hint">{status.explanation}</p>
        )}
        {error && <p role="alert">{error}</p>}
        {report && report.events === 0 && (
          <p className="muted">
            Событий пока нет. Нужны завершённые записи: запустите запись данных — новые записи
            обрабатываются сами раз в несколько минут, или нажмите «Обработать новые записи».
          </p>
        )}
        {report && report.events > 0 && (
          <>
            <div className="quality-grid">
              {report.counts.map((c) => (
                <div className="quality-item" key={c.type}>
                  <span>{c.type}</span>
                  <strong>{c.episodes}</strong>
                  <small>
                    эпизодов · событий {c.count} · {c.perHour.toLocaleString('ru-RU')} в час
                  </small>
                </div>
              ))}
            </div>
            <p className={stable > 0 ? 'quality-hint positive' : 'quality-hint'}>
              {stable > 0
                ? `Устойчиво в плюсе после комиссий: ${stable}. Это кандидаты — их нужно подтвердить на новых днях.`
                : 'Устойчиво прибыльных сигналов пока нет: ни одна комбинация не в плюсе на обеих половинах данных при 100+ эпизодах.'}
              {report.fromMs !== null &&
                ` Учтены только события с ${new Date(report.fromMs).toLocaleString('ru-RU', { timeZone: 'Europe/Moscow' })} (МСК).`}
              {report.session &&
                ` Только сессия «${tradingSessions.find((s) => s.id === report.session)?.name}».`}
            </p>
            <div
              className="table-scroll"
              tabIndex={0}
              role="region"
              aria-label="Сигналы, прокрутка"
            >
              <table>
                <caption className="sr-only">Результат сигналов после спреда и комиссий</caption>
                <thead>
                  <tr>
                    <th>Сигнал</th>
                    <th>Сделка</th>
                    <th>Время</th>
                    <th>Эпизодов</th>
                    <th>Событий</th>
                    <th>Средний результат</th>
                    <th>Прибыльных</th>
                    <th>1-я половина</th>
                    <th>2-я половина</th>
                    <th>Устойчиво</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r) => (
                    <tr
                      key={key(r)}
                      className={selected && key(selected) === key(r) ? 'lab-selected' : ''}
                    >
                      <td>
                        <button className="text-button" onClick={() => setSelected(r)}>
                          {r.type}
                        </button>
                      </td>
                      <td>{modes[r.mode]}</td>
                      <td>{horizons[r.horizon] ?? `${r.horizon} с`}</td>
                      <td>{r.episodes}</td>
                      <td>{r.count}</td>
                      <td className={tone(r.meanPct)}>{pct(r.meanPct)}</td>
                      <td>{r.winRate.toLocaleString('ru-RU')}%</td>
                      <td className={tone(r.firstHalf)}>{pct(r.firstHalf)}</td>
                      <td className={tone(r.secondHalf)}>{pct(r.secondHalf)}</td>
                      <td>{r.stable ? 'да' : '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <ul className="field-hint scan-stats-notes">
              {report.notes.map((n) => (
                <li key={n}>{n}</li>
              ))}
            </ul>
          </>
        )}
      </section>
      {selected && trades && (
        <>
          <div className="section-line">
            <h3>
              {selected.type} · {modes[selected.mode].toLowerCase()} · {horizons[selected.horizon]}{' '}
              · сделки по 100 USDT
            </h3>
            <button className="text-button" onClick={() => setSelected(null)}>
              Закрыть
            </button>
          </div>
          {bySession && <SessionTable rows={bySession} />}
          <Performance trades={trades} pnl={metrics(trades).pnl} />
          <TradesTable
            trades={trades}
            note="Расчёт по записям рынка: вход и выход по лучшим ценам стакана, сделок не было. График и сделки — за все часы."
          />
        </>
      )}
      {spreads && spreads.length > 0 && <SpreadPanel rows={spreads} />}
    </div>
  );
}

// The chosen signal in each trading session: more activity does not mean a better result, so each is shown on its own.
function SessionTable({ rows }: { rows: LabSessionRow[] }) {
  return (
    <section className="panel" aria-labelledby="lab-sessions-title">
      <h3 id="lab-sessions-title">По торговым сессиям</h3>
      <div className="table-scroll" tabIndex={0} role="region" aria-label="Сессии, прокрутка">
        <table>
          <caption className="sr-only">Результат выбранного сигнала по торговым сессиям</caption>
          <thead>
            <tr>
              <th>Сессия</th>
              <th>Записано, ч</th>
              <th>Эпизодов</th>
              <th>Средний результат</th>
              <th>Прибыльных</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.session}>
                <td>{r.name}</td>
                <td>{r.hours.toLocaleString('ru-RU')}</td>
                <td>{r.episodes}</td>
                <td className={tone(r.meanPct)}>{pct(r.meanPct)}</td>
                <td>{r.winRate === null ? '—' : `${r.winRate.toLocaleString('ru-RU')}%`}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="field-hint">
        Сессии по часам UTC; разбивка уменьшает число эпизодов в каждой, поэтому разница между
        сессиями при десятках эпизодов почти всегда случайна.
      </p>
    </section>
  );
}

// Scalping on the spread pays only where the spread alone covers two maker fees and both orders get filled.
function SpreadPanel({ rows }: { rows: LabSpreadRow[] }) {
  const [open, setOpen] = useState(false);
  const shown = open ? rows : rows.slice(0, 8);
  return (
    <section className="panel" aria-labelledby="lab-spreads-title">
      <div className="section-line">
        <h3 id="lab-spreads-title">Пипсовка: спред по монетам</h3>
      </div>
      <p className="field-hint">
        Пипсовка зарабатывает на спреде: покупка по bid и продажа по ask заявками maker. Это имеет
        смысл, только если спред сам покрывает две комиссии maker (0,02% × 2 = 4 б.п.) и обе заявки
        исполняются, а цена за это время не уходит против. Таблица — первая проверка: как часто
        спред вообще был шире 4 и 10 б.п. Исполнение и очередь здесь не учитываются.
      </p>
      <div className="table-scroll" tabIndex={0} role="region" aria-label="Спреды, прокрутка">
        <table>
          <caption className="sr-only">Спред лучших цен по монетам записи</caption>
          <thead>
            <tr>
              <th>Монета</th>
              <th>Средний спред, б.п.</th>
              <th>Спред ≥ 4 б.п.</th>
              <th>Спред ≥ 10 б.п.</th>
              <th>Снимков стакана</th>
            </tr>
          </thead>
          <tbody>
            {shown.map((r) => (
              <tr key={r.symbol}>
                <td>{r.symbol}</td>
                <td className="mono">{r.meanBps.toLocaleString('ru-RU')}</td>
                <td className="mono">{r.above4Pct.toLocaleString('ru-RU')}% времени</td>
                <td className="mono">{r.above10Pct.toLocaleString('ru-RU')}% времени</td>
                <td className="mono">{r.snapshots.toLocaleString('ru-RU')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {rows.length > 8 && (
        <button className="text-button" onClick={() => setOpen(!open)}>
          {open ? 'Свернуть' : `Показать все монеты (${rows.length})`}
        </button>
      )}
    </section>
  );
}

// The hypothesis fixed before the data it is checked on: progress towards the number of episodes each signal needs and the
// verdict by the rule fixed at the same time. The intermediate mean is shown, but no verdict is drawn from it.
function HypothesisPanel({ check }: { check: HypothesisCheck }) {
  const since = new Date(check.fromMs).toLocaleString('ru-RU', {
    timeZone: 'Europe/Moscow',
    dateStyle: 'short',
    timeStyle: 'short',
  });
  return (
    <section className="panel hypothesis" aria-labelledby="hypothesis-title">
      <div className="section-line">
        <h2 id="hypothesis-title">Проверка гипотезы</h2>
        <span className={`research-status ${verdictTone[check.verdict]}`}>
          {verdicts[check.verdict]}
        </span>
      </div>
      <p className="hypothesis-statement">{check.statement}</p>
      <p className="field-hint">
        Только события с {since} (МСК) — данные, которых не было, когда гипотезу выбрали · записано{' '}
        {check.hours.toLocaleString('ru-RU')} ч · комиссии круга{' '}
        {check.feePct.toLocaleString('ru-RU')}%. Правило вывода зафиксировано заранее: каждому
        сигналу нужно {check.signals[0]?.needed ?? 50} эпизодов; подтверждена, если средний
        результат после комиссий выше нуля и в целом, и на обеих половинах.
      </p>
      <div className="hypothesis-grid">
        {check.signals.map((s) => (
          <div className="quality-item" key={`${s.type}|${s.mode}|${s.horizon}`}>
            <span>
              {s.type} · {modes[s.mode].toLowerCase()} · {horizons[s.horizon] ?? `${s.horizon} с`}
            </span>
            <strong>
              {s.episodes} из {s.needed}
            </strong>
            <progress
              max={s.needed}
              value={Math.min(s.episodes, s.needed)}
              aria-label={`${s.type}: эпизодов ${s.episodes} из ${s.needed}`}
            />
            <small>
              {s.meanPct === null
                ? 'эпизодов пока нет'
                : `${s.verdict === 'collecting' ? 'пока ' : ''}${pct(s.meanPct)} в среднем · в плюсе ${s.winRate?.toLocaleString('ru-RU')}%`}
              {s.verdict !== 'collecting' &&
                ` · половины ${pct(s.firstHalf)} / ${pct(s.secondHalf)}`}
            </small>
            <small className={verdictTone[s.verdict]}>{verdicts[s.verdict]}</small>
          </div>
        ))}
      </div>
      <p className="field-hint">{check.explanation}</p>
    </section>
  );
}

// The Laboratory page: the data recording and the signal lab that studies it.
export function Laboratory({ scanner }: { scanner: ReactNode }) {
  const [tab, setTab] = useState<'lab' | 'scanner'>('lab');
  return (
    <>
      <div className="detail-tabs">
        {(
          [
            ['lab', 'Лаборатория сигналов'],
            ['scanner', 'Запись данных'],
          ] as const
        ).map(([value, label]) => (
          <button
            key={value}
            className={tab === value ? 'active' : ''}
            aria-pressed={tab === value}
            onClick={() => setTab(value)}
          >
            {label}
          </button>
        ))}
      </div>
      {tab === 'lab' ? <SignalLabPanel /> : scanner}
    </>
  );
}

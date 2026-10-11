import { useEffect, useRef, useState } from 'react';
import { ApiError, request } from './api';

export interface SeriesRecordingOption {
  id: string;
  coin: string;
  sourceExchange: string;
  exchange: string;
  startedUtc: string;
  status: string;
}
interface Total {
  mode: string;
  delayMs: number;
  recordings: number;
  recordingsPositive: number;
  ordersSent: number;
  entriesFilled: number;
  ordersUnfilled: number;
  trades: number;
  unresolved: number;
  sumNetBps: number;
  meanNetBps: number | null;
  winPercent: number | null;
  drawdownBps: number;
  closedNetQuote: number | null;
  drawdownFromTruncatedLog: boolean;
}
interface Row {
  id: string;
  coin: string | null;
  sourceExchange: string | null;
  exchange: string | null;
  startedUtc: string | null;
  status: string;
  explanation: string;
  leader: string | null;
  executionExchange: string | null;
}
interface Series {
  version: string;
  createdUtc: string;
  settings: { executionModel: string; entryOrder?: string; orderNotional: number; feeBps: number };
  recordings: Row[];
  totals: Total[];
  notes: string[];
}

const n = (value: number | null | undefined) =>
  value == null ? '—' : value.toLocaleString('ru-RU', { maximumFractionDigits: 3 });
const mode = (value: string) => (value === 'follow' ? 'По импульсу' : 'Против импульса');
const statusText: Record<string, string> = {
  evaluated: 'проверено',
  'no-leader': 'поводырь не определён',
  'no-basis': 'мало совместных котировок',
  'too-short': 'слишком короткая',
  incomplete: 'запись неполная',
  'no-execution-data': 'нет данных для исполнения',
  error: 'ошибка',
};
const label = (r: {
  coin: string | null;
  sourceExchange: string | null;
  exchange: string | null;
  startedUtc: string | null;
  id: string;
}) =>
  r.coin
    ? `${r.coin} · ${r.sourceExchange} ↔ ${r.exchange} · ${new Date(r.startedUtc!).toLocaleString('ru-RU')}`
    : r.id;

export function StrategySeries({
  recordings,
  settings,
}: {
  recordings: SeriesRecordingOption[];
  settings: Record<string, unknown>;
}) {
  const completed = recordings.filter((r) => r.status === 'completed');
  // null means "all completed recordings", so recordings that load later are included until the user picks.
  const [chosen, setChosen] = useState<string[] | null>(null);
  const [series, setSeries] = useState<Series | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const pendingRequest = useRef<AbortController | null>(null);
  useEffect(() => {
    const read = new AbortController();
    request<Series>('/research/series/replay', { signal: read.signal })
      .then((data) => {
        if (!read.signal.aborted) setSeries(data);
      })
      .catch((e) => {
        if (!read.signal.aborted && !(e instanceof ApiError && e.status === 404))
          setError(e.message);
      });
    return () => {
      read.abort();
      pendingRequest.current?.abort();
    };
  }, []);
  const selected = (chosen ?? completed.map((r) => r.id)).filter((id) =>
    completed.some((r) => r.id === id),
  );
  async function run() {
    const controller = new AbortController();
    pendingRequest.current = controller;
    setBusy(true);
    setError('');
    try {
      const result = await request<Series>('/research/series/replay', {
        method: 'POST',
        body: JSON.stringify({ ids: selected, settings }),
        signal: AbortSignal.any([controller.signal, AbortSignal.timeout(70000 * selected.length)]),
      });
      if (!controller.signal.aborted) setSeries(result);
    } catch (e) {
      if (!controller.signal.aborted)
        setError(e instanceof Error ? e.message : 'Не удалось проверить серию записей.');
    } finally {
      if (!controller.signal.aborted) setBusy(false);
    }
  }
  const depth = series?.settings.executionModel === 'depth';
  return (
    <section className="strategy-series" aria-labelledby="series-title">
      <h4 id="series-title">Проверка на серии записей</h4>
      <p>
        Те же настройки стратегии применяются к каждой выбранной записи; результаты складываются.
        Так видно, повторяется ли результат, а не только удачная строка одной записи. Каждая запись
        пересчитывается заново, и её сохранённый результат тоже обновляется.
      </p>
      {completed.length === 0 ? (
        <p>Нет завершённых записей для проверки.</p>
      ) : (
        <form
          onSubmit={(e) => {
            e.preventDefault();
            void run();
          }}
        >
          <fieldset disabled={busy}>
            <legend>Записи для проверки</legend>
            {completed.map((r) => (
              <label key={r.id} className="series-choice">
                <input
                  type="checkbox"
                  checked={selected.includes(r.id)}
                  onChange={(e) =>
                    setChosen(
                      e.target.checked ? [...selected, r.id] : selected.filter((x) => x !== r.id),
                    )
                  }
                />
                {label({ ...r })}
              </label>
            ))}
          </fieldset>
          <button
            className="button primary"
            disabled={busy || selected.length === 0 || selected.length > 30}
          >
            {busy
              ? `Проверяем ${selected.length} записей…`
              : `Проверить на выбранных записях (${selected.length})`}
          </button>
          {selected.length > 30 && (
            <p className="field-hint">Можно выбрать не больше 30 записей.</p>
          )}
        </form>
      )}
      {error && <p role="alert">{error}</p>}
      {series && (
        <div aria-live="polite">
          <p>
            Стратегия v{series.version}, расчёт от{' '}
            {new Date(series.createdUtc).toLocaleString('ru-RU')}. Модель:{' '}
            {depth ? 'по стакану' : 'упрощённая BBO'}, вход{' '}
            {series.settings.entryOrder === 'maker' ? 'maker' : 'taker'}, комиссия taker{' '}
            {n(series.settings.feeBps)} б.п.
            {depth && <>, номинал {n(series.settings.orderNotional)}</>}.
          </p>
          {series.totals.length === 0 ? (
            <p>Ни одна запись не прошла проверку, итогов нет. Причины — в таблице записей.</p>
          ) : (
            <div className="analysis-table" tabIndex={0}>
              <table>
                <caption>Итоги по сценариям на всех проверенных записях</caption>
                <thead>
                  <tr>
                    <th>Вариант</th>
                    <th>Задержка</th>
                    <th>Записей в плюсе</th>
                    <th>Заявок отправлено</th>
                    <th>Исполнено</th>
                    <th>Не исполнено</th>
                    <th>Закрыто сделок</th>
                    <th>Неопределённо</th>
                    <th>Средний итог, б.п.</th>
                    <th>Плюсовых, %</th>
                    <th>Сумма, б.п.</th>
                    <th>Просадка, б.п.</th>
                    {depth && <th>Итог закрытых</th>}
                  </tr>
                </thead>
                <tbody>
                  {series.totals.map((t) => (
                    <tr key={`${t.mode}-${t.delayMs}`}>
                      <td>{mode(t.mode)}</td>
                      <td>{t.delayMs} мс</td>
                      <td>
                        {t.recordingsPositive} из {t.recordings}
                      </td>
                      <td>{t.ordersSent}</td>
                      <td>{t.entriesFilled}</td>
                      <td>{t.ordersUnfilled}</td>
                      <td>{t.trades}</td>
                      <td>{t.unresolved}</td>
                      <td>{n(t.meanNetBps)}</td>
                      <td>{n(t.winPercent)}</td>
                      <td>{n(t.sumNetBps)}</td>
                      <td>
                        {n(t.drawdownBps)}
                        {t.drawdownFromTruncatedLog ? '*' : ''}
                      </td>
                      {depth && <td>{n(t.closedNetQuote)}</td>}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {series.totals.some((t) => t.drawdownFromTruncatedLog) && (
            <p className="field-hint">
              * Просадка посчитана по первым 500 сделкам каждой записи; сумма учитывает все сделки.
            </p>
          )}
          <div className="analysis-table" tabIndex={0}>
            <table>
              <caption>Записи серии</caption>
              <thead>
                <tr>
                  <th>Запись</th>
                  <th>Статус</th>
                  <th>Поводырь → исполнение</th>
                  <th>Пояснение</th>
                </tr>
              </thead>
              <tbody>
                {series.recordings.map((r) => (
                  <tr key={r.id}>
                    <td>{label(r)}</td>
                    <td>{statusText[r.status] ?? r.status}</td>
                    <td>{r.leader ? `${r.leader} → ${r.executionExchange}` : '—'}</td>
                    <td>{r.explanation}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <details>
            <summary>Как читать итоги серии</summary>
            <ul>
              {series.notes.map((note) => (
                <li key={note}>{note}</li>
              ))}
            </ul>
          </details>
        </div>
      )}
    </section>
  );
}

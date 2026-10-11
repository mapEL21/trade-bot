import { useEffect, useRef, useState } from 'react';
import { ApiError, request } from './api';

const initial = {
  calibrationSeconds: 120,
  impulseBps: 3,
  windowMs: 250,
  maxAgeMs: 500,
  feeBps: 5,
  slippageBps: 1,
  minEdgeBps: 2,
  stopLossBps: 20,
  maxHoldMs: 2000,
  leaderAdvantageBps: 0.5,
};
type Settings = typeof initial;
interface Scenario {
  mode: string;
  delayMs: number;
  trades: number;
  meanNetBps: number | null;
  winPercent: number | null;
  sumNetBps: number;
  closedTradeDrawdownBps: number;
  unresolvedPositions: number;
  skipped: number;
  signals: number;
  journalTruncated: boolean;
  tradeLog: {
    entrySeconds: number;
    exitSeconds: number;
    side: string;
    entryPrice: number;
    exitPrice: number;
    netBps: number;
    exitReason: string;
  }[];
  journal: {
    seconds: number;
    action: string;
    reason: string;
    side: string | null;
    price: number | null;
    netBps: number | null;
  }[];
}
interface Report {
  status: string;
  explanation: string;
  version: string;
  leader: string | null;
  executionExchange: string | null;
  normalBasisBps: number | null;
  durationSeconds: number;
  settings: Settings;
  scenarios: Scenario[];
  notes: string[];
  candidates: {
    sourceExchange: string;
    exchange: string;
    samples: number;
    responseBps: number | null;
  }[];
}
const fields: [keyof Settings, string, number, number, number][] = [
  ['calibrationSeconds', 'Участок выбора поводыря, с', 30, 1800, 1],
  ['impulseBps', 'Порог импульса, б.п.', 0.1, 1000, 0.1],
  ['windowMs', 'Окно движения, мс', 10, 5000, 1],
  ['maxAgeMs', 'Свежесть котировки, мс', 10, 5000, 1],
  ['feeBps', 'Комиссия стратегии за сторону, б.п.', 0, 100, 0.1],
  ['slippageBps', 'Проскальзывание стратегии, б.п.', 0, 100, 0.1],
  ['minEdgeBps', 'Запас сверх расходов, б.п.', 0, 100, 0.1],
  ['stopLossBps', 'Порог убытка сделки, б.п.', 1, 1000, 0.1],
  ['maxHoldMs', 'Максимальное удержание, мс', 100, 10000, 1],
  ['leaderAdvantageBps', 'Преимущество поводыря, б.п.', 0.1, 100, 0.1],
];
const n = (value: number | null) =>
  value == null ? '—' : value.toLocaleString('ru-RU', { maximumFractionDigits: 3 });
const price = (value: number | null) =>
  value == null ? '—' : value.toLocaleString('ru-RU', { maximumSignificantDigits: 12 });
const mode = (value: string) => (value === 'follow' ? 'По импульсу' : 'Против импульса');

export function StrategyReplay({ id, disabled }: { id: string; disabled: boolean }) {
  const [settings, setSettings] = useState(initial);
  const [report, setReport] = useState<Report | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [selection, setSelection] = useState('follow-100');
  const pendingRequest = useRef<AbortController | null>(null);
  const requestVersion = useRef(0);
  useEffect(() => {
    const version = ++requestVersion.current;
    const read = new AbortController();
    if (id)
      request<Report>(`/research/recordings/${id}/replay`, {
        signal: AbortSignal.any([read.signal, AbortSignal.timeout(10000)]),
      })
        .then((data) => {
          if (!read.signal.aborted && version === requestVersion.current) setReport(data);
        })
        .catch((e) => {
          if (
            !read.signal.aborted &&
            version === requestVersion.current &&
            !(e instanceof ApiError && e.status === 404)
          )
            setError(e.message);
        });
    return () => read.abort();
  }, [id]);
  useEffect(() => () => pendingRequest.current?.abort(), []);
  async function run() {
    requestVersion.current++;
    const controller = new AbortController();
    pendingRequest.current = controller;
    setBusy(true);
    setError('');
    try {
      const result = await request<Report>(`/research/recordings/${id}/replay`, {
        method: 'POST',
        body: JSON.stringify(settings),
        signal: AbortSignal.any([controller.signal, AbortSignal.timeout(70000)]),
      });
      if (!controller.signal.aborted) setReport(result);
    } catch (e) {
      if (!controller.signal.aborted)
        setError(e instanceof Error ? e.message : 'Не удалось выполнить симуляцию.');
    } finally {
      if (!controller.signal.aborted) setBusy(false);
    }
  }
  const selected = report?.scenarios.find((s) => `${s.mode}-${s.delayMs}` === selection);
  return (
    <section className="strategy-replay" aria-labelledby="replay-title">
      <h3 id="replay-title">Первая стратегия: поводырь → отстающая биржа</h3>
      <p>
        Симуляция на выбранной выше записи. Сначала выбираем поводыря, затем проверяем сделки на
        оставшихся данных. Реальные заявки не отправляются.
      </p>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          void run();
        }}
      >
        <details>
          <summary>Настройки стратегии</summary>
          <p className="field-hint">
            1 б.п. = 0,01%. Комиссия по умолчанию — пример, не тариф аккаунта. Порог убытка не
            гарантирует цену выхода: к ней тоже применяется задержка.
          </p>
          <div className="analysis-settings">
            {fields.map(([key, label, min, max, step]) => (
              <label key={key}>
                {label}
                <input
                  required
                  type="number"
                  min={min}
                  max={max}
                  step={step}
                  disabled={busy}
                  value={Number.isNaN(settings[key]) ? '' : settings[key]}
                  onChange={(e) => setSettings((s) => ({ ...s, [key]: e.target.valueAsNumber }))}
                />
              </label>
            ))}
          </div>
        </details>
        <button className="button primary" disabled={busy || disabled || !id}>
          {busy ? 'Проверяем стратегию…' : 'Проверить первую стратегию'}
        </button>
      </form>
      {disabled && id && (
        <p className="field-hint">
          Проверка доступна для записи, завершённой без ошибок и разрывов, после окончания другого
          расчёта.
        </p>
      )}
      {error && <p role="alert">{error}</p>}
      {report && (
        <div aria-live="polite">
          <h4>Результат стратегии v{report.version}</h4>
          <p>{report.explanation}</p>
          <p>
            Выбор поводыря: первые {report.settings.calibrationSeconds} с. Запись:{' '}
            {n(report.durationSeconds)} с. Торговая проверка начинается только после выбора.
          </p>
          <details>
            <summary>Параметры сохранённого результата</summary>
            <ul>
              {fields.map(([key, label]) => (
                <li key={key}>
                  {label}: {n(report.settings[key])}
                </li>
              ))}
            </ul>
            <p>Изменение настроек формы требует повторной проверки.</p>
          </details>
          <div className="analysis-table" tabIndex={0}>
            <table>
              <caption>Кандидаты в поводыря на начальном участке</caption>
              <thead>
                <tr>
                  <th>Направление</th>
                  <th>Наблюдения</th>
                  <th>Реакция через 100 мс, б.п.</th>
                </tr>
              </thead>
              <tbody>
                {report.candidates.map((c) => (
                  <tr key={c.sourceExchange}>
                    <td>
                      {c.sourceExchange} → {c.exchange}
                    </td>
                    <td>{c.samples}</td>
                    <td>{n(c.responseBps)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {report.leader && (
            <p>
              <strong>
                Поводырь: {report.leader}. Условное исполнение: {report.executionExchange}.
              </strong>{' '}
              Обычная разница цен: {n(report.normalBasisBps)} б.п.
            </p>
          )}
          {report.scenarios.length > 0 && (
            <>
              <div className="analysis-table" tabIndex={0}>
                <table>
                  <caption>Сравнение независимых сценариев</caption>
                  <thead>
                    <tr>
                      <th>Вариант</th>
                      <th>Задержка</th>
                      <th>Сделки</th>
                      <th>Средний итог, б.п.</th>
                      <th>Плюсовых, %</th>
                      <th>Сумма, б.п.</th>
                      <th>Просадка закрытых, б.п.</th>
                      <th>Не оценено позиций</th>
                    </tr>
                  </thead>
                  <tbody>
                    {report.scenarios.map((s) => (
                      <tr key={`${s.mode}-${s.delayMs}`}>
                        <td>{mode(s.mode)}</td>
                        <td>{s.delayMs} мс</td>
                        <td>{s.trades}</td>
                        <td>{n(s.meanNetBps)}</td>
                        <td>{n(s.winPercent)}</td>
                        <td>{n(s.sumNetBps)}</td>
                        <td>{n(s.closedTradeDrawdownBps)}</td>
                        <td>{s.unresolvedPositions}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <p className="field-hint">
                Результаты после спреда, комиссии и проскальзывания. Сумма в б.п. не является
                доходностью счёта. Если есть неоценённая позиция, результат сценария неполный.
              </p>
              <label className="replay-selection">
                Журнал сценария
                <select value={selection} onChange={(e) => setSelection(e.target.value)}>
                  {report.scenarios.map((s) => (
                    <option key={`${s.mode}-${s.delayMs}`} value={`${s.mode}-${s.delayMs}`}>
                      {mode(s.mode)} · {s.delayMs} мс
                    </option>
                  ))}
                </select>
              </label>
              {selected && (
                <>
                  <p>
                    Сигналов: {selected.signals}. Пропущено: {selected.skipped}. Закрыто сделок:{' '}
                    {selected.trades}.
                  </p>
                  {selected.trades === 0 && (
                    <p>
                      Закрытых сделок нет. Причины пропусков показаны в журнале; параметры не
                      подбираются автоматически ради положительного результата.
                    </p>
                  )}
                  {selected.journalTruncated && (
                    <p>
                      Сохранены первые 500 записей каждого журнала. Сводная статистика учитывает
                      весь сценарий.
                    </p>
                  )}
                  <details>
                    <summary>Причины входов, пропусков и выходов</summary>
                    <div className="analysis-table" tabIndex={0}>
                      <table>
                        <thead>
                          <tr>
                            <th>Секунда</th>
                            <th>Действие</th>
                            <th>Причина</th>
                            <th>Сторона</th>
                            <th>Цена</th>
                            <th>Итог, б.п.</th>
                          </tr>
                        </thead>
                        <tbody>
                          {selected.journal.map((row, i) => (
                            <tr key={i}>
                              <td>{n(row.seconds)}</td>
                              <td>{row.action}</td>
                              <td>{row.reason}</td>
                              <td>{row.side ?? '—'}</td>
                              <td>{price(row.price)}</td>
                              <td>{n(row.netBps)}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  </details>
                  <details>
                    <summary>Условные сделки</summary>
                    <div className="analysis-table" tabIndex={0}>
                      <table>
                        <thead>
                          <tr>
                            <th>Вход, с</th>
                            <th>Выход, с</th>
                            <th>Сторона</th>
                            <th>Цена входа</th>
                            <th>Цена выхода</th>
                            <th>Итог, б.п.</th>
                            <th>Причина выхода</th>
                          </tr>
                        </thead>
                        <tbody>
                          {selected.tradeLog.map((row, i) => (
                            <tr key={i}>
                              <td>{n(row.entrySeconds)}</td>
                              <td>{n(row.exitSeconds)}</td>
                              <td>{row.side}</td>
                              <td>{price(row.entryPrice)}</td>
                              <td>{price(row.exitPrice)}</td>
                              <td>{n(row.netBps)}</td>
                              <td>{row.exitReason}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  </details>
                </>
              )}
            </>
          )}
          <details>
            <summary>Как устроена проверка и её ограничения</summary>
            <ul>
              {report.notes.map((note) => (
                <li key={note}>{note}</li>
              ))}
            </ul>
          </details>
          <p className="field-hint">Последний результат стратегии сохранён вместе с записью.</p>
        </div>
      )}
    </section>
  );
}

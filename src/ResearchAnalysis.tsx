import { useEffect, useRef, useState } from 'react';
import { request } from './api';

interface Recording {
  id: string;
  coin: string;
  sourceExchange: string;
  exchange: string;
  startedUtc: string;
  status: string;
  hasReport: boolean;
}
interface Settings {
  impulseBps: number;
  windowMs: number;
  entryDelayMs: number;
  holdMs: number;
  maxAgeMs: number;
  feeBps: number;
  slippageBps: number;
}
interface Report {
  recording: Recording;
  settings: Settings;
  durationSeconds: number;
  events: number;
  gaps: number;
  invalidQuotes: number;
  outOfOrderQuotes: number;
  coveragePercent: number;
  notes: string[];
  feeds: { exchange: string; quotes: number; averageSpreadBps: number | null }[];
  directions: {
    sourceExchange: string;
    exchange: string;
    impulses: number;
    horizons: {
      horizonMs: number;
      samples: number;
      sameDirectionPercent: number | null;
      meanMoveBps: number | null;
    }[];
    model: {
      trades: number;
      excluded: number;
      winPercent: number | null;
      meanGrossBps: number | null;
      meanNetBps: number | null;
      meanFeeBps: number | null;
    };
  }[];
}
const defaults: Settings = {
  impulseBps: 3,
  windowMs: 250,
  entryDelayMs: 100,
  holdMs: 1000,
  maxAgeMs: 1000,
  feeBps: 5,
  slippageBps: 1,
};
const fields: [keyof Settings, string, number, number, number][] = [
  ['impulseBps', 'Импульс цены, б.п.', 0.1, 1000, 0.1],
  ['windowMs', 'Окно импульса, мс', 10, 5000, 1],
  ['entryDelayMs', 'Задержка входа, мс', 0, 5000, 1],
  ['holdMs', 'Удержание позиции, мс', 100, 10000, 1],
  ['maxAgeMs', 'Допустимый возраст цены, мс', 10, 5000, 1],
  ['feeBps', 'Комиссия за сторону, б.п.', 0, 100, 0.1],
  ['slippageBps', 'Проскальзывание за сторону, б.п.', 0, 100, 0.1],
];
const number = (n: number | null, digits = 2) =>
  n == null ? '—' : n.toLocaleString('ru-RU', { maximumFractionDigits: digits });

export function ResearchAnalysis({ active }: { active: boolean }) {
  const [items, setItems] = useState<Recording[]>([]);
  const [id, setId] = useState('');
  const [settings, setSettings] = useState(defaults);
  const [report, setReport] = useState<Report | null>(null);
  const [error, setError] = useState('');
  const [unavailable, setUnavailable] = useState(0);
  const [reload, setReload] = useState(0);
  const [busy, setBusy] = useState(false);
  const pending = useRef<AbortController | null>(null);
  useEffect(() => () => pending.current?.abort(), []);
  useEffect(() => {
    const controller = new AbortController();
    request<{ items: Recording[]; unavailable: number }>('/research/recordings', {
      signal: controller.signal,
    })
      .then((data) => {
        if (controller.signal.aborted) return;
        setItems(data.items);
        setUnavailable(data.unavailable);
        setId((old) =>
          data.items.some((x) => x.id === old)
            ? old
            : (data.items.find((x) => x.status !== 'recording')?.id ?? ''),
        );
      })
      .catch((e) => {
        if (!controller.signal.aborted) setError(e.message);
      });
    return () => controller.abort();
  }, [active, reload]);
  const selected = items.find((x) => x.id === id);
  useEffect(() => {
    const controller = new AbortController();
    setReport(null);
    setError('');
    if (id && selected?.hasReport)
      request<Report>(`/research/recordings/${id}/analysis`, { signal: controller.signal })
        .then((data) => {
          if (!controller.signal.aborted) setReport(data);
        })
        .catch((e) => {
          if (!controller.signal.aborted) setError(e.message);
        });
    return () => controller.abort();
  }, [id, selected?.hasReport]);
  async function analyze() {
    const controller = new AbortController();
    pending.current = controller;
    setBusy(true);
    setError('');
    try {
      const data = await request<Report>(`/research/recordings/${id}/analysis`, {
        method: 'POST',
        body: JSON.stringify(settings),
        signal: AbortSignal.any([controller.signal, AbortSignal.timeout(70000)]),
      });
      if (!controller.signal.aborted) {
        setReport(data);
        setReload((x) => x + 1);
      }
    } catch (e) {
      if (!controller.signal.aborted)
        setError(e instanceof Error ? e.message : 'Не удалось выполнить анализ.');
    } finally {
      if (!controller.signal.aborted) setBusy(false);
    }
  }
  return (
    <section className="panel research-analysis" aria-labelledby="analysis-title">
      <h2 id="analysis-title">Анализатор записей</h2>
      <p className="muted">
        Проверяем, следует ли цена одной площадки за импульсом на другой, и что остаётся после
        расходов.
      </p>
      <div className="analysis-controls">
        <label>
          Сохранённая запись
          <select value={id} disabled={busy} onChange={(e) => setId(e.target.value)}>
            <option value="">Выберите запись</option>
            {items.map((item) => (
              <option key={item.id} value={item.id} disabled={item.status === 'recording'}>
                {item.coin} · {item.sourceExchange} → {item.exchange} ·{' '}
                {new Date(item.startedUtc).toLocaleString('ru-RU')}{' '}
                {item.status === 'recording' ? '(не завершена)' : ''}
              </option>
            ))}
          </select>
        </label>
        <button
          type="button"
          className="button secondary"
          disabled={busy}
          onClick={() => setReload((x) => x + 1)}
        >
          Обновить список
        </button>
      </div>
      {!items.length && (
        <p>
          Пока нет записей. Запустите исследование рынка и завершите запись — она появится здесь.
        </p>
      )}
      {unavailable > 0 && <p role="status">Не удалось прочитать записей: {unavailable}.</p>}
      <form
        onSubmit={(e) => {
          e.preventDefault();
          void analyze();
        }}
      >
        <details>
          <summary>Параметры расчёта</summary>
          <p className="field-hint">
            1 базисный пункт (б.п.) = 0,01%. Комиссия 5 б.п. — пример для расчёта, не подтверждённый
            тариф вашего аккаунта.
          </p>
          <div className="analysis-settings">
            {fields.map(([key, label, min, max, step]) => (
              <label key={key}>
                {label}
                <input
                  type="number"
                  required
                  min={min}
                  max={max}
                  step={step}
                  value={Number.isNaN(settings[key]) ? '' : settings[key]}
                  disabled={busy}
                  onChange={(e) => setSettings((s) => ({ ...s, [key]: e.target.valueAsNumber }))}
                />
              </label>
            ))}
          </div>
        </details>
        <button
          className="button primary"
          disabled={busy || !selected || selected.status === 'recording'}
        >
          {busy ? 'Рассчитываем…' : 'Проанализировать запись'}
        </button>
      </form>
      {error && <p role="alert">{error}</p>}
      {report && (
        <div aria-live="polite">
          <h3>Отчёт: {report.recording.coin}</h3>
          <p>
            Длительность: {number(report.durationSeconds, 1)} с · Событий:{' '}
            {number(report.events, 0)} · Свежие цены обеих площадок:{' '}
            {number(report.coveragePercent, 1)}% времени.
          </p>
          <p>
            Разрывов: {report.gaps} · Некорректных котировок: {report.invalidQuotes} · Отброшено
            устаревших обновлений: {report.outOfOrderQuotes}.
          </p>
          {report.feeds.map((feed) => (
            <p key={feed.exchange}>
              {feed.exchange}: {number(feed.quotes, 0)} обновлений BBO, средний спред по обновлениям
              — {number(feed.averageSpreadBps)} б.п.
            </p>
          ))}
          <p className="field-hint">
            Параметры этого отчёта: импульс {report.settings.impulseBps} б.п. за{' '}
            {report.settings.windowMs} мс; вход через {report.settings.entryDelayMs} мс; удержание{' '}
            {report.settings.holdMs} мс; возраст цены до {report.settings.maxAgeMs} мс; комиссия{' '}
            {report.settings.feeBps} и проскальзывание {report.settings.slippageBps} б.п. за
            сторону. Изменение формы требует нового расчёта.
          </p>
          {report.directions.map((direction) => (
            <article key={direction.sourceExchange}>
              <h3>
                {direction.sourceExchange} → {direction.exchange}
              </h3>
              <p>
                {direction.impulses === 0
                  ? 'При выбранном пороге импульсы не найдены. По этой записи оценить входы нельзя.'
                  : direction.model.trades === 0
                    ? 'Импульсы найдены, но для оценки входа и выхода не хватило непрерывных свежих котировок.'
                    : direction.model.meanNetBps != null && direction.model.meanNetBps <= 0
                      ? 'В этом сценарии среднее движение не покрывает расходы. Положительного среднего результата на записи нет.'
                      : 'На этой записи средний результат модели положительный. Это предварительное наблюдение: его нужно проверить на других записях.'}
              </p>
              <p>
                Найдено импульсов: {direction.impulses}. Оценено входов: {direction.model.trades};
                исключено из-за разрывов, устаревших цен или конца записи:{' '}
                {direction.model.excluded}.
              </p>
              <p>
                <strong>
                  Средний результат после расходов: {number(direction.model.meanNetBps)} б.п.
                </strong>{' '}
                · Положительных результатов: {number(direction.model.winPercent, 1)}%.
              </p>
              <p className="field-hint">
                До комиссии, но уже со спредом и проскальзыванием:{' '}
                {number(direction.model.meanGrossBps)} б.п. Средняя комиссия за вход и выход:{' '}
                {number(direction.model.meanFeeBps)} б.п. Это условные входы по BBO, не исполненные
                сделки.
              </p>
              <div className="analysis-table">
                <table>
                  <caption>Движение второй площадки после импульса</caption>
                  <thead>
                    <tr>
                      <th>Через</th>
                      <th>Наблюдений</th>
                      <th>В сторону импульса</th>
                      <th>Среднее движение</th>
                    </tr>
                  </thead>
                  <tbody>
                    {direction.horizons.map((h) => (
                      <tr key={h.horizonMs}>
                        <td>{h.horizonMs} мс</td>
                        <td>{h.samples}</td>
                        <td>{number(h.sameDirectionPercent, 1)}%</td>
                        <td>{number(h.meanMoveBps)} б.п.</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </article>
          ))}
          <p className="field-hint">
            Плюс в таблице — движение вслед за импульсом; нулевое движение не считается совпадением.
            Таблица использует среднюю цену bid/ask без расходов. Импульсы отбираются с интервалом
            не менее 5 секунд; это не прогноз частоты сделок.
          </p>
          <ul>
            {report.notes.map((note) => (
              <li key={note}>{note}</li>
            ))}
          </ul>
          <p className="field-hint">
            Отчёт сохранён вместе с записью и доступен после перезапуска.
          </p>
        </div>
      )}
    </section>
  );
}

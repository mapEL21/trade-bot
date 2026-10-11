import { useEffect, useRef, useState } from 'react';
import { ApiError, request } from './api';
import { StrategySeries, type SeriesRecordingOption } from './StrategySeries';

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
  orderNotional: 10,
  priceLimitBps: 5,
  makerFeeBps: 2,
  makerWaitMs: 1000,
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
  partialEntries?: number;
  partialExits?: number;
  closedNetQuote?: number | null;
  unresolvedQuantity?: number | null;
  makerOrders?: number | null;
  makerFills?: number | null;
  tradeLog: {
    entrySeconds: number;
    exitSeconds: number;
    side: string;
    entryPrice: number;
    exitPrice: number;
    netBps: number;
    exitReason: string;
    quantity?: number | null;
    netQuote?: number | null;
    feesQuote?: number | null;
  }[];
  journal: {
    seconds: number;
    action: string;
    reason: string;
    side: string | null;
    price: number | null;
    netBps: number | null;
    quantity?: number | null;
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
  settings: Settings & { executionModel?: string; entryOrder?: string; leader?: string };
  instrument?: {
    quantityStep: number;
    minimumQuantity: number;
    minimumNotional: number;
    maximumQuantity: number | null;
    quoteCurrency: string;
    ruleSource: string;
  } | null;
  scenarios: Scenario[];
  notes: string[];
  candidates: {
    sourceExchange: string;
    exchange: string;
    samples: number;
    responseBps: number | null;
  }[];
}
// Shared with the bot form so both name and limit the strategy settings the same way.
export const strategyFields: [keyof Settings, string, number, number, number][] = [
  ['calibrationSeconds', 'Начальный участок, с', 30, 1800, 1],
  ['impulseBps', 'Порог импульса, б.п.', 0.1, 1000, 0.1],
  ['windowMs', 'Окно движения, мс', 10, 5000, 1],
  ['maxAgeMs', 'Свежесть котировки, мс', 10, 5000, 1],
  ['feeBps', 'Комиссия taker за сторону, б.п.', 0, 100, 0.01],
  ['slippageBps', 'Дополнительное проскальзывание, б.п.', 0, 100, 0.01],
  ['minEdgeBps', 'Запас сверх расходов, б.п.', 0, 100, 0.1],
  ['stopLossBps', 'Порог убытка сделки, б.п.', 1, 1000, 0.1],
  ['maxHoldMs', 'Максимальное удержание, мс', 100, 10000, 1],
  ['leaderAdvantageBps', 'Преимущество поводыря (автовыбор), б.п.', 0.1, 100, 0.1],
  ['orderNotional', 'Номинал заявки в валюте расчёта', 1, 100000, 0.01],
  ['priceLimitBps', 'Предел отклонения цены заявки, б.п.', 0, 100, 0.1],
  ['makerFeeBps', 'Комиссия maker за вход, б.п.', -10, 100, 0.01],
  ['makerWaitMs', 'Ожидание исполнения maker, мс', 10, 60000, 1],
];
const makerFields: (keyof Settings)[] = ['makerFeeBps', 'makerWaitMs'];
const n = (value: number | null | undefined) =>
  value == null ? '—' : value.toLocaleString('ru-RU', { maximumFractionDigits: 3 });
const price = (value: number | null | undefined) =>
  value == null ? '—' : value.toLocaleString('ru-RU', { maximumSignificantDigits: 12 });
const mode = (value: string) => (value === 'follow' ? 'По импульсу' : 'Против импульса');

export function StrategyReplay({
  id,
  disabled,
  recordings = [],
}: {
  id: string;
  disabled: boolean;
  recordings?: SeriesRecordingOption[];
}) {
  const [settings, setSettings] = useState(initial);
  const [executionModel, setExecutionModel] = useState('depth');
  const [entryOrder, setEntryOrder] = useState('taker');
  // As in the bot, the signal exchange of the recording leads unless an automatic choice is asked for.
  const [leader, setLeader] = useState<'source' | 'auto'>('source');
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
        body: JSON.stringify({ ...settings, executionModel, entryOrder, leader }),
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
  const maker = report?.scenarios.some((s) => s.makerOrders != null) ?? false;
  return (
    <section className="strategy-replay" aria-labelledby="replay-title">
      <h3 id="replay-title">Первая стратегия: поводырь → отстающая биржа</h3>
      <p>
        Симуляция на выбранной выше записи. Начальный участок задаёт обычную разницу цен, сделки
        проверяются на оставшихся данных. Реальные заявки не отправляются.
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
          <label>
            Поводырь
            <select
              value={leader}
              disabled={busy}
              onChange={(e) => setLeader(e.target.value as 'source' | 'auto')}
            >
              <option value="source">Источник сигнала записи — как в боте</option>
              <option value="auto">Определить по начальному участку</option>
            </select>
          </label>
          <p className="field-hint">
            Автоматический выбор может не найти поводыря, и тогда сделки не моделируются. Замер
            опережения показывается в результате в обоих случаях.
          </p>
          <label>
            Модель исполнения
            <select
              value={executionModel}
              disabled={busy}
              onChange={(e) => setExecutionModel(e.target.value)}
            >
              <option value="depth">По записанному стакану и объёму</option>
              <option value="bbo">Упрощённая: только лучшие цены BBO</option>
            </select>
          </label>
          <p className="field-hint">
            Номинал — размер сделки в USDT (USDC для Hyperliquid), а не баланс или маржа. Предел
            цены отсчитывается от котировки при решении отправить заявку и действует на вход и
            выход. В модели BBO номинал и предел цены не используются.
          </p>
          <label>
            Вход в сделку
            <select
              value={entryOrder}
              disabled={busy}
              onChange={(e) => setEntryOrder(e.target.value)}
            >
              <option value="taker">Taker: сразу по встречной цене</option>
              <option value="maker">Maker: заявка по своей лучшей цене</option>
            </select>
          </label>
          <p className="field-hint">
            Maker-заявка платит меньшую комиссию и не платит спред, но исполняется, только если цена
            сама придёт к ней — часто это значит, что рынок пошёл против сделки. Выход всегда taker.
          </p>
          <div className="analysis-settings">
            {strategyFields.map(([key, label, min, max, step]) => (
              <label key={key}>
                {label}
                <input
                  required
                  type="number"
                  min={min}
                  max={max}
                  step={step}
                  disabled={
                    busy ||
                    (executionModel === 'bbo' &&
                      (key === 'orderNotional' || key === 'priceLimitBps')) ||
                    (entryOrder !== 'maker' && makerFields.includes(key)) ||
                    (leader === 'source' && key === 'leaderAdvantageBps')
                  }
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
            Начальный участок: первые {report.settings.calibrationSeconds} с. Запись:{' '}
            {n(report.durationSeconds)} с. Торговая проверка начинается только после него.
          </p>
          <details>
            <summary>Параметры сохранённого результата</summary>
            <p>
              Модель:{' '}
              {report.settings.executionModel === 'depth'
                ? 'По записанному стакану и объёму'
                : 'Упрощённая BBO, без проверки объёма'}
              . Вход: {report.settings.entryOrder === 'maker' ? 'maker' : 'taker'}. Поводырь:{' '}
              {report.settings.leader === 'source'
                ? 'задан — источник сигнала'
                : 'выбран по начальному участку'}
              .
            </p>
            <ul>
              {strategyFields
                .filter(
                  ([key]) => report.settings.entryOrder === 'maker' || !makerFields.includes(key),
                )
                .map(([key, label]) => (
                  <li key={key}>
                    {label}: {n(report.settings[key])}
                  </li>
                ))}
            </ul>
            <p>Изменение настроек формы требует повторной проверки.</p>
            {report.instrument && (
              <p>
                Шаг количества: {price(report.instrument.quantityStep)} монет. Минимум:{' '}
                {price(report.instrument.minimumQuantity)} монет; минимальный номинал:{' '}
                {n(report.instrument.minimumNotional)} {report.instrument.quoteCurrency}.{' '}
                {report.instrument.ruleSource}.
              </p>
            )}
          </details>
          <div className="analysis-table" tabIndex={0}>
            <table>
              <caption>Опережение бирж на начальном участке</caption>
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
                      {maker && <th>Исполнено maker-заявок</th>}
                      {report.instrument && (
                        <th>Итог закрытых, {report.instrument.quoteCurrency}</th>
                      )}
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
                        {maker && (
                          <td>
                            {s.makerFills ?? 0} из {s.makerOrders ?? 0}
                          </td>
                        )}
                        {report.instrument && <td>{price(s.closedNetQuote)}</td>}
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
                  {report.instrument && (
                    <p>
                      Частичных входов: {selected.partialEntries ?? 0}. Частичных выходов:{' '}
                      {selected.partialExits ?? 0}.
                      {selected.unresolvedQuantity != null && (
                        <>
                          {' '}
                          Незакрытый остаток: {price(selected.unresolvedQuantity)} монет.
                          Незавершённая сделка не включена в денежный итог.
                        </>
                      )}
                    </p>
                  )}
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
                            {report.instrument && <th>Объём, монет</th>}
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
                              {report.instrument && <td>{price(row.quantity)}</td>}
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
                            {report.instrument && (
                              <>
                                <th>Объём, монет</th>
                                <th>Комиссии, {report.instrument.quoteCurrency}</th>
                                <th>Итог, {report.instrument.quoteCurrency}</th>
                              </>
                            )}
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
                              {report.instrument && (
                                <>
                                  <td>{price(row.quantity)}</td>
                                  <td>{price(row.feesQuote)}</td>
                                  <td>{price(row.netQuote)}</td>
                                </>
                              )}
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
      <StrategySeries
        recordings={recordings}
        settings={{ ...settings, executionModel, entryOrder, leader }}
      />
    </section>
  );
}

import { useEffect, useRef, useState } from 'react';
import { Pause, Play, Square } from 'lucide-react';
import { api, type PaperRunRecord, type PaperSnapshot, type PaperTrade } from './api';
import type { Bot } from './model';

export const activeStatuses = ['calibrating', 'running', 'paused', 'stopping'];
const n = (value: number | null | undefined, digits = 3) =>
  value == null ? '—' : value.toLocaleString('ru-RU', { maximumFractionDigits: digits });
const price = (value: number | null | undefined) =>
  value == null ? '—' : value.toLocaleString('ru-RU', { maximumSignificantDigits: 12 });
const stateText: Record<string, string> = {
  starting: 'Подключение',
  calibrating: 'Калибровка',
  running: 'Работает',
  finished: 'Завершён',
  stopped: 'Остановлен',
  failed: 'Ошибка',
  interrupted: 'Прерван',
  'no-leader': 'Нет поводыря',
  'no-basis': 'Мало данных',
  'no-execution-data': 'Нет данных исполнения',
};
const durations = [
  [600, '10 минут'],
  [1800, '30 минут'],
  [3600, '1 час'],
] as const;

function Trades({ trades, caption }: { trades: PaperTrade[]; caption: string }) {
  if (!trades.length) return null;
  return (
    <div className="analysis-table" tabIndex={0}>
      <table>
        <caption>{caption}</caption>
        <thead>
          <tr>
            <th>Вход, с</th>
            <th>Выход, с</th>
            <th>Сторона</th>
            <th>Цена входа</th>
            <th>Цена выхода</th>
            <th>Итог, б.п.</th>
            <th>Итог, USDT</th>
            <th>Причина выхода</th>
          </tr>
        </thead>
        <tbody>
          {trades.slice(0, 50).map((t, i) => (
            <tr key={i}>
              <td>{n(t.entrySeconds)}</td>
              <td>{n(t.exitSeconds)}</td>
              <td>{t.side}</td>
              <td>{price(t.entryPrice)}</td>
              <td>{price(t.exitPrice)}</td>
              <td>{n(t.netBps)}</td>
              <td>{price(t.netQuote)}</td>
              <td>{t.exitReason}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// Live paper run of a leader-lag bot: controls, current state while it runs, and the history of finished runs.
export function PaperRun({ bot, onChanged }: { bot: Bot; onChanged: () => void }) {
  const active = activeStatuses.includes(bot.status);
  const [snapshot, setSnapshot] = useState<PaperSnapshot | null>(null);
  const [runs, setRuns] = useState<PaperRunRecord[]>([]);
  const [seconds, setSeconds] = useState(1800);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const wasActive = useRef(active);
  const changed = useRef(onChanged);
  changed.current = onChanged;
  useEffect(() => {
    const controller = new AbortController();
    api
      .paperRuns(bot, controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) setRuns(data);
      })
      .catch((e) => {
        if (!controller.signal.aborted) setError(e.message);
      });
    return () => controller.abort();
    // Reload history when the bot changes or a run starts or ends, not on every workspace refresh.
  }, [bot.id, active]);
  useEffect(() => {
    if (!active) return;
    let stopped = false;
    let controller: AbortController | null = null;
    async function poll() {
      while (!stopped) {
        controller = new AbortController();
        try {
          const data = await api.paper(controller.signal);
          if (stopped) return;
          if (data.botId === bot.id) setSnapshot(data);
          // The run ended on the server: reload the workspace so the card shows the final status.
          if (
            data.botId !== bot.id ||
            !['starting', 'calibrating', 'running'].includes(data.state)
          ) {
            changed.current();
            return;
          }
        } catch (e) {
          if (!stopped) setError((e as Error).message);
        }
        await new Promise((resolve) => setTimeout(resolve, 1000));
      }
    }
    void poll();
    return () => {
      stopped = true;
      controller?.abort();
    };
  }, [active, bot.id]);
  useEffect(() => {
    if (wasActive.current && !active) setSnapshot(null);
    wasActive.current = active;
  }, [active]);
  async function act(action: () => Promise<unknown>) {
    setBusy(true);
    setError('');
    try {
      await action();
      changed.current();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  const live = active && snapshot?.botId === bot.id ? snapshot : null;
  const calibration =
    live?.settings?.calibrationSeconds ?? bot.parameters?.settings.calibrationSeconds ?? 120;
  return (
    <section className="panel paper-run" aria-labelledby="paper-title">
      <div className="section-line">
        <h2 id="paper-title">Живая симуляция</h2>
        <span className="neutral-badge">Реальные заявки не отправляются</span>
      </div>
      <p className="field-hint">
        Бот получает текущие публичные данные {bot.exchange} и {bot.parameters?.signalExchange},
        первые {calibration} с выбирает поводыря, затем виртуально входит на {bot.exchange}. Все
        исполнения считаются моделью; одновременно возможна одна симуляция или запись рынка.
      </p>
      <div className="paper-controls">
        {!active && (
          <>
            <label className="paper-duration">
              Длительность
              <select
                value={seconds}
                disabled={busy}
                onChange={(e) => setSeconds(Number(e.target.value))}
              >
                {durations.map(([value, label]) => (
                  <option key={value} value={value}>
                    {label}
                  </option>
                ))}
              </select>
            </label>
            <button
              className="button primary"
              disabled={busy}
              onClick={() => void act(() => api.startPaper(bot, seconds))}
            >
              <Play size={16} />
              Запустить симуляцию
            </button>
          </>
        )}
        {(bot.status === 'running' || bot.status === 'calibrating') && (
          <button
            className="button secondary"
            disabled={busy}
            onClick={() => void act(() => api.pausePaper(bot, true))}
          >
            <Pause size={16} />
            Пауза
          </button>
        )}
        {bot.status === 'paused' && (
          <button
            className="button secondary"
            disabled={busy}
            onClick={() => void act(() => api.pausePaper(bot, false))}
          >
            <Play size={16} />
            Продолжить
          </button>
        )}
        {active && bot.status !== 'stopping' && (
          <button
            className="button secondary"
            disabled={busy}
            onClick={() => void act(() => api.stopPaper(bot, false))}
          >
            <Square size={16} />
            Остановить
          </button>
        )}
        {bot.status === 'stopping' && (
          <button
            className="button danger"
            disabled={busy}
            onClick={() => void act(() => api.stopPaper(bot, true))}
          >
            <Square size={16} />
            Остановить сразу
          </button>
        )}
      </div>
      {active && (
        <p className="field-hint">
          Пауза запрещает новые входы, открытая позиция ведётся дальше. «Остановить» ждёт закрытия
          позиции по правилам стратегии; «Остановить сразу» оставит её неоценённой.
        </p>
      )}
      {error && <p role="alert">{error}</p>}
      {live && (
        <div className="paper-live" aria-live="polite">
          <p>
            <strong>{stateText[live.state] ?? live.state}.</strong> {live.explanation}
          </p>
          {live.state === 'calibrating' && (
            <p>
              Калибровка: {n(Math.min(live.sessionSeconds, calibration), 0)} из {calibration} с.
            </p>
          )}
          {live.entriesBlocked && <p className="field-hint">{live.entriesBlocked}</p>}
          <div className="paper-facts">
            <span>
              Поводырь
              <strong>{live.leader ? `${live.leader} → ${live.executionExchange}` : '—'}</strong>
            </span>
            <span>
              Позиция или заявка
              <strong>{live.positionOpen ? 'Есть' : 'Нет'}</strong>
            </span>
            <span>
              Закрытых сделок
              <strong>{live.trades.length}</strong>
            </span>
            <span>
              Итог закрытых, б.п.
              <strong>{n(live.sumNetBps)}</strong>
            </span>
            <span>
              Итог закрытых, USDT
              <strong>{price(live.closedNetQuote)}</strong>
            </span>
            <span>
              Обычная разница цен, б.п.
              <strong>{n(live.normalBasisBps)}</strong>
            </span>
          </div>
          {live.lateEvents > 0 && (
            <p className="field-hint">
              Опоздавших событий: {live.lateEvents}. Результат может отличаться от проверки этой
              записи.
            </p>
          )}
          {live.decisions.length > 0 && (
            <div className="analysis-table" tabIndex={0}>
              <table>
                <caption>Последние решения</caption>
                <thead>
                  <tr>
                    <th>Секунда</th>
                    <th>Действие</th>
                    <th>Причина</th>
                    <th>Сторона</th>
                    <th>Цена</th>
                  </tr>
                </thead>
                <tbody>
                  {live.decisions.slice(0, 20).map((d, i) => (
                    <tr key={i}>
                      <td>{n(d.seconds)}</td>
                      <td>{d.action}</td>
                      <td>{d.reason}</td>
                      <td>{d.side ?? '—'}</td>
                      <td>{price(d.price)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <Trades trades={live.trades} caption="Закрытые сделки этого запуска" />
        </div>
      )}
      {runs.length > 0 && (
        <>
          <div className="analysis-table" tabIndex={0}>
            <table>
              <caption>История запусков</caption>
              <thead>
                <tr>
                  <th>Начало</th>
                  <th>Состояние</th>
                  <th>Поводырь</th>
                  <th>Сделки</th>
                  <th>Итог, б.п.</th>
                  <th>Итог, USDT</th>
                  <th>Не оценено</th>
                </tr>
              </thead>
              <tbody>
                {runs.map((run) => (
                  <tr key={run.id}>
                    <td>{new Date(run.startedUtc).toLocaleString('ru-RU')}</td>
                    <td title={run.explanation}>{stateText[run.state] ?? run.state}</td>
                    <td>{run.leader ? `${run.leader} → ${run.executionExchange}` : '—'}</td>
                    <td>{run.trades}</td>
                    <td>{n(run.sumNetBps)}</td>
                    <td>{price(run.closedNetQuote)}</td>
                    <td>{run.unresolved}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {!active && (
            <>
              <p className="field-hint">Последний запуск: {runs[0].explanation}</p>
              <Trades trades={runs[0].tradeLog} caption="Сделки последнего запуска" />
            </>
          )}
        </>
      )}
    </section>
  );
}

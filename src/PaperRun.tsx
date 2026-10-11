import { useEffect, useRef, useState } from 'react';
import { Pause, Play, Square } from 'lucide-react';
import {
  api,
  type OpenPosition,
  type PaperRunRecord,
  type PaperSnapshot,
  type PaperTrade,
} from './api';
import { Modal } from './components';
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

// One line about the open simulated position: side, size, entry, current exit price and the result after both fees.
function PositionLine({ position, coin }: { position: OpenPosition; coin: string }) {
  const loss = (position.markNetQuote ?? position.markNetBps) < 0;
  return (
    <p className={`paper-position ${loss ? 'loss' : 'gain'}`}>
      <strong>{position.side === 'long' ? 'Лонг' : 'Шорт'}</strong> {n(position.quantity, 8)} {coin}{' '}
      · вход {price(position.entryPrice)} · сейчас {price(position.markPrice)} · результат при
      закрытии сейчас{' '}
      <strong>
        {position.markNetQuote == null
          ? '—'
          : `${position.markNetQuote >= 0 ? '+' : ''}${n(position.markNetQuote, 4)} USDT`}{' '}
        ({position.markNetBps >= 0 ? '+' : ''}
        {n(position.markNetBps / 100, 4)}%)
      </strong>{' '}
      после комиссий
    </p>
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
  const [confirmStop, setConfirmStop] = useState(false);
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
          const data = await api.paper(bot, controller.signal);
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
  const coin = bot.symbol.replace(/USDT$/, '');
  const position = live?.position ?? null;
  // With nothing open the bot just turns off (an entry already in flight is awaited); with a position the user chooses.
  function stop() {
    if (position) setConfirmStop(true);
    else void act(() => api.stopPaper(bot, 'wait'));
  }
  const calibration =
    live?.settings?.calibrationSeconds ?? bot.parameters?.settings.calibrationSeconds ?? 120;
  return (
    <section className="panel paper-run" aria-labelledby="paper-title">
      <div className="section-line">
        <h2 id="paper-title">Живая симуляция</h2>
        <span className="neutral-badge">Реальные заявки не отправляются</span>
      </div>
      <p className="field-hint">
        Бот следит за текущими публичными данными двух выбранных бирж: {bot.exchange} — биржа бота
        (здесь он виртуально торгует), {bot.parameters?.signalExchange} — биржа сигнала. Первые{' '}
        {calibration} с он определяет, какая из них ведёт. Если ведёт биржа сигнала, бот входит на
        бирже бота, пока та отстаёт; если ведёт биржа бота, запуск останавливается с объяснением.
        Все исполнения считаются моделью. Одновременно могут работать до 4 симуляций разных ботов, у
        каждой своя запись рынка.
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
          <button className="button secondary" disabled={busy} onClick={stop}>
            <Square size={16} />
            Остановить
          </button>
        )}
        {bot.status === 'stopping' && live?.positionOpen && !live.closing && (
          <button
            className="button secondary"
            disabled={busy}
            onClick={() => void act(() => api.stopPaper(bot, 'close'))}
          >
            <Square size={16} />
            Закрыть позицию сейчас
          </button>
        )}
        {bot.status === 'stopping' && (
          <button
            className="button danger"
            disabled={busy}
            title="Завершить запись немедленно: открытая позиция останется без оценки"
            onClick={() => void act(() => api.stopPaper(bot, 'force'))}
          >
            <Square size={16} />
            Остановить без оценки
          </button>
        )}
      </div>
      {active && (
        <p className="field-hint">
          Пауза запрещает новые входы, открытая позиция ведётся дальше. «Остановить» без открытой
          позиции сразу выключает бота, а с позицией предложит дождаться выхода по правилам или
          закрыть её сейчас. «Остановить без оценки» — аварийный вариант, если закрытие зависло.
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
          {position && <PositionLine position={position} coin={coin} />}
          {live.closing && <p className="field-hint">Позиция закрывается по команде остановки.</p>}
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
      {confirmStop && (
        <Modal
          title="Остановить бота?"
          subtitle="Бот сейчас в сделке. Выберите, как её закрыть."
          onClose={() => setConfirmStop(false)}
        >
          {position ? (
            <PositionLine position={position} coin={coin} />
          ) : (
            <p>Позиция уже закрыта — бот просто выключится.</p>
          )}
          {position && (position.markNetQuote ?? position.markNetBps) < 0 && (
            <p className="stop-warning" role="alert">
              Сделка сейчас в минусе. «Закрыть сейчас» зафиксирует этот убыток. Если дождаться, бот
              закроет её по правилам стратегии — по сближению цен, развороту поводыря, порогу убытка{' '}
              {n((bot.parameters?.settings.stopLossBps ?? 0) / 100, 4)}% или через{' '}
              {n((bot.parameters?.settings.maxHoldMs ?? 0) / 1000, 1)} с удержания. Итог может стать
              и лучше, и хуже.
            </p>
          )}
          <div className="modal-actions stop-actions">
            <button
              className="button secondary"
              data-autofocus
              onClick={() => setConfirmStop(false)}
            >
              Отмена
            </button>
            <button
              className="button primary"
              disabled={busy}
              onClick={() => {
                setConfirmStop(false);
                void act(() => api.stopPaper(bot, 'wait'));
              }}
            >
              Дождаться выхода по правилам
            </button>
            <button
              className="button danger"
              disabled={busy}
              onClick={() => {
                setConfirmStop(false);
                void act(() => api.stopPaper(bot, position ? 'close' : 'wait'));
              }}
            >
              Закрыть сейчас
            </button>
          </div>
          <p className="field-hint">
            В обоих случаях новые входы прекращаются сразу, а после закрытия позиции бот
            выключается. «Закрыть сейчас» — виртуальный выход по текущему стакану с той же задержкой
            и комиссией; реальных ордеров нет.
          </p>
        </Modal>
      )}
    </section>
  );
}

import { useEffect, useState, type Dispatch, type SetStateAction } from 'react';
import { Play, Square } from 'lucide-react';
import { api, tradingSessions, type ExchangeKeys, type SignalRunView } from './api';
import { ExchangeBalanceCard, ExchangeKeysPanel, ExchangeLog } from './ExchangePanel';
import { SelectField } from './components';
import {
  formatMoney,
  maxLeverage,
  strategies,
  type Bot,
  type BotConfig,
  type SignalParameters,
} from './model';

const num = (n: number, digits = 2) => n.toLocaleString('ru-RU', { maximumFractionDigits: digits });
const moscow = (ms: number) =>
  new Date(ms).toLocaleString('ru-RU', {
    timeZone: 'Europe/Moscow',
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });

// Settings of a bot in the bot form: the strategy and money management. Entries and exits are the strategy's own logic.
export function SignalFields({
  form,
  setForm,
}: {
  form: BotConfig;
  setForm: Dispatch<SetStateAction<BotConfig>>;
}) {
  const p = form.signal!;
  const set = (patch: Partial<SignalParameters>) =>
    setForm((f) => ({ ...f, signal: { ...f.signal!, ...patch } }));
  const number = (
    label: string,
    key: keyof SignalParameters,
    min: number,
    max: number,
    step: number,
  ) => (
    <label className="field">
      <span>{label}</span>
      <input
        type="number"
        min={min}
        max={max}
        step={step}
        value={Number.isNaN(p[key] as number) ? '' : (p[key] as number)}
        onChange={(e) => set({ [key]: e.target.valueAsNumber })}
        required
      />
    </label>
  );
  return (
    <>
      <div className="form-grid">
        <SelectField label="Стратегия" value={p.signal} onChange={(signal) => set({ signal })}>
          {strategies.map((s) => (
            <option key={s.id}>{s.id}</option>
          ))}
        </SelectField>
        {number('Риск на сделку с комиссиями, % депозита', 'riskPct', 0.1, 5, 0.1)}
        {number('Комиссии круга, %', 'feePct', 0, 1, 0.01)}
        {number('Плечо не больше', 'maxLeverage', 1, maxLeverage, 1)}
        {number('Сделок одновременно', 'maxPositions', 1, 20, 1)}
        {number('Дневной лимит убытка, % начального депозита', 'dailyLossPct', 1, 100, 1)}
        {number('Позиция не больше, USDT', 'maxNotional', 5, 1_000_000, 1)}
      </div>
      <p className="field-hint signal-example">
        {strategies.find((s) => s.id === p.signal)?.description} Вход и выход — по логике стратегии,
        у каждой сделки записана причина. Риск {num(p.riskPct)}% — потеря при отмене сценария вместе
        с комиссиями: из него считается размер позиции.
      </p>
      <fieldset className="signal-sessions">
        <legend>Торговые сессии (ни одной — все часы)</legend>
        {tradingSessions.map((s) => (
          <label className="checkbox-field" key={s.id}>
            <input
              type="checkbox"
              checked={p.sessions?.includes(s.id) ?? false}
              onChange={(e) => {
                const next = (p.sessions ?? []).filter((x) => x !== s.id);
                set({ sessions: e.target.checked ? [...next, s.id] : next });
              }}
            />
            <span>{s.name}</span>
          </label>
        ))}
      </fieldset>
    </>
  );
}

// Controls and state of a signal bot: it trades on the processed recordings from its start moment, so new trades appear
// after each recording is processed (about once an hour with continuous recording).
export function SignalRunPanel({ bot, onChanged }: { bot: Bot; onChanged: () => void }) {
  const [runs, setRuns] = useState<SignalRunView[] | null>(null);
  const [from, setFrom] = useState('');
  const [execution, setExecution] = useState<'simulation' | 'testnet'>('simulation');
  const [keys, setKeys] = useState<ExchangeKeys | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const running = bot.status === 'running';
  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    async function load() {
      try {
        const data = await api.signalRuns(bot);
        if (!stopped) {
          setRuns(data);
          setError('');
        }
      } catch (e) {
        if (!stopped) setError((e as Error).message);
      }
      if (!stopped && running) timer = setTimeout(load, 5000);
    }
    void load();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, [bot.id, running, bot.trades.length]);

  async function act(action: () => Promise<unknown>) {
    setBusy(true);
    setError('');
    try {
      await action();
      onChanged();
      setRuns(await api.signalRuns(bot));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  const current = runs?.[0];
  const result = current?.result;
  const density = bot.signal?.signal === 'Плотность';
  const live = current?.state === 'running' && current.run.live;
  const onExchange = current?.state === 'running' && current.run.execution === 'testnet';
  const skipped = Object.entries(result?.skipped ?? {}).sort((a, b) => b[1] - a[1]);
  return (
    <section className="panel paper-run signal-run" aria-labelledby="signal-run-title">
      <div className="section-line">
        <h2 id="signal-run-title">{live ? 'Живой режим' : 'Работа бота'}</h2>
        <span className={`research-status ${running ? 'is-live' : ''}`} role="status">
          <span className={`live-dot ${running ? 'on' : ''}`} aria-hidden="true" />
          {running ? 'Работает' : 'Не запущен'}
        </span>
      </div>
      <p className="field-hint">
        {density
          ? 'С этого момента бот работает вживую: монеты, у которых скринер нашёл плотность ближе 1,5% к цене, подключаются к потоку сделок и стакана, и бот решает по каждой сделке. С датой в прошлом он проходит прошлые записи данных.'
          : 'Бот проходит каждую завершённую запись рынка: новые сделки появляются после её окончания, примерно раз в час. С датой в прошлом он сначала пройдёт записи с этого момента.'}{' '}
        {current?.state === 'running' && current.run.execution === 'testnet'
          ? 'Решения уходят заявками в тестовую сеть Binance.'
          : 'Сделки виртуальные: заявки на биржу не отправляются.'}{' '}
        Бот не входит по устаревшему стакану; после дневного лимита убытка новые сделки ждут
        следующего дня по Москве.
      </p>
      <div className="paper-controls">
        {running ? (
          <button
            className="button secondary"
            disabled={busy}
            onClick={() => {
              if (
                onExchange &&
                !window.confirm(
                  'Остановить бота и закрыть его позиции на бирже рыночными заявками? Заявки на его монетах будут сняты.',
                )
              )
                return;
              void act(() => api.stopSignal(bot));
            }}
          >
            <Square size={15} /> {onExchange ? 'Остановить и закрыть всё' : 'Остановить'}
          </button>
        ) : (
          <>
            <button
              className="button primary"
              disabled={busy}
              onClick={() =>
                void act(() => api.startSignal(bot, null, density ? execution : 'simulation'))
              }
            >
              <Play size={16} /> Запустить с этого момента
            </button>
            {density && (
              <label className="field signal-execution">
                <span>Исполнение</span>
                <select
                  value={execution}
                  onChange={(e) => setExecution(e.target.value as 'simulation' | 'testnet')}
                >
                  <option value="simulation">Симуляция (без заявок)</option>
                  <option value="testnet" disabled={!keys?.testnet.configured}>
                    Тестовая сеть Binance{keys?.testnet.configured ? '' : ' — нужны ключи'}
                  </option>
                </select>
              </label>
            )}
            <label className="field signal-from">
              <span>Или с даты (по прошлым записям)</span>
              <input type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} />
            </label>
            <button
              className="button secondary"
              disabled={busy || !from}
              onClick={() => void act(() => api.startSignal(bot, new Date(from).getTime()))}
            >
              Запустить с даты
            </button>
          </>
        )}
      </div>
      {error && <p role="alert">{error}</p>}
      {current && result && (
        <>
          <p className="field-hint">
            {current.state === 'running' ? 'Работает' : 'Последний запуск'} с{' '}
            {moscow(current.run.fromMs)}
            {current.run.toMs !== null && ` по ${moscow(current.run.toMs)}`} · записей{' '}
            {result.recordings}
            {result.lastDataMs !== null && ` · данные до ${moscow(result.lastDataMs)}`} · сигналов{' '}
            {result.signals} · сделок {result.trades.length}
            {result.incomplete > 0 && ` · не закрыты к концу записи: ${result.incomplete}`} ·
            депозит {formatMoney(result.deposit)} USDT
          </p>
          {live && current?.live === null && (
            <p className="field-hint negative">
              Живой режим выключен на этом компьютере: запустите приложение через «Запустить Trade
              Bot.cmd».
            </p>
          )}
          {live && current?.run.execution === 'testnet' && (
            <p className="field-hint">
              <span className="mode-pill">ТЕСТОВАЯ СЕТЬ</span> Решения бота уходят заявками на
              тестовую биржу Binance; у каждой позиции — защитный стоп на бирже.
              {current.live?.exchangeProblem && (
                <span className="negative"> {current.live.exchangeProblem}</span>
              )}
            </p>
          )}
          {live && current?.live?.exchange && current.live.exchange.length > 0 && (
            <div className="live-watch">
              <strong>Позиции на бирже</strong>
              <ul>
                {current.live.exchange.map((p) => (
                  <li key={p.symbol}>
                    {p.symbol} {p.side === 'long' ? 'лонг' : 'шорт'} {p.quantity} от {p.entry},
                    защитный стоп {p.stopPrice}
                  </li>
                ))}
              </ul>
            </div>
          )}
          {live && current?.live && (
            <div className="live-watch">
              <strong>
                Следит за монетами: {current.live.watching.length}
                {current.live.watching.length === 0 &&
                  ' — ждёт, пока скринер найдёт плотность у цены'}
              </strong>
              {current.live.watching.length > 0 && (
                <ul>
                  {current.live.watching.map((w) => (
                    <li key={w.symbol}>
                      {w.symbol} — плотность на {w.side > 0 ? 'покупку' : 'продажу'} {w.wall}, цена
                      в {num(w.distancePct)}%
                    </li>
                  ))}
                </ul>
              )}
              {current.live.open.length > 0 && (
                <>
                  <strong>Открытые позиции</strong>
                  <ul>
                    {current.live.open.map((p) => (
                      <li key={p.symbol}>
                        {p.symbol} {p.side === 'long' ? 'лонг' : 'шорт'} от {p.entry}, сейчас{' '}
                        {p.mark} — {p.entryReason}
                      </li>
                    ))}
                  </ul>
                </>
              )}
            </div>
          )}
          {result.pending > 0 && (
            <p className="field-hint" role="status">
              Считаются записи: {result.pending}. Сделки по ним появятся, когда расчёт закончится.
            </p>
          )}
          {result.entriesBlocked && <p className="field-hint negative">{result.entriesBlocked}</p>}
          {skipped.length > 0 && (
            <div className="signal-skipped">
              <strong>Пропущенные сигналы</strong>
              <ul>
                {skipped.map(([reason, count]) => (
                  <li key={reason}>
                    {reason}: {count}
                  </li>
                ))}
              </ul>
            </div>
          )}
        </>
      )}
      {runs && runs.length === 0 && <p className="muted">Бот ещё не запускался.</p>}
      {current?.run.execution === 'testnet' && (
        <ExchangeLog bot={bot} refresh={bot.trades.length + (runs?.length ?? 0)} />
      )}
      {density && (
        <ExchangeBalanceCard
          refresh={
            bot.trades.length * 10 + (runs?.length ?? 0) * 2 + (keys?.testnet.configured ? 1 : 0)
          }
        />
      )}
      {density && <ExchangeKeysPanel onChanged={setKeys} />}
    </section>
  );
}

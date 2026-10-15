import { useEffect, useState, type Dispatch, type SetStateAction } from 'react';
import { Play, Square } from 'lucide-react';
import { api, type SignalRunView } from './api';
import { SelectField } from './components';
import {
  formatMoney,
  maxLeverage,
  signalTypes,
  type Bot,
  type BotConfig,
  type SignalParameters,
} from './model';

const holds: [number, string][] = [
  [5, '5 с'],
  [15, '15 с'],
  [60, '1 мин'],
  [120, '2 мин'],
  [300, '5 мин'],
];
const num = (n: number, digits = 2) => n.toLocaleString('ru-RU', { maximumFractionDigits: digits });
const moscow = (ms: number) =>
  new Date(ms).toLocaleString('ru-RU', {
    timeZone: 'Europe/Moscow',
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });

// How a trade of the bot is sized, on an example coin, so the numbers behind the settings are visible before saving.
export function sizingExample(p: SignalParameters, deposit: number, typicalPct = 0.3) {
  const stop = p.stopRanges * typicalPct;
  const feeShare = p.feePct / (stop + p.feePct);
  const risk = (deposit * p.riskPct) / 100;
  const notional = Math.min(risk / ((stop + p.feePct) / 100), deposit * p.maxLeverage);
  return {
    stop,
    feeShare,
    risk,
    notional,
    leverage: notional / deposit,
    skipped: feeShare > p.maxFeeShare,
  };
}

// Settings of a signal bot in the bot form.
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
    scale = 1,
  ) => (
    <label className="field">
      <span>{label}</span>
      <input
        type="number"
        min={min}
        max={max}
        step={step}
        value={
          Number.isNaN(p[key] as number) ? '' : Math.round((p[key] as number) * scale * 1e6) / 1e6
        }
        onChange={(e) => set({ [key]: e.target.valueAsNumber / scale })}
        required
      />
    </label>
  );
  const example = sizingExample(p, form.budget || 0);
  return (
    <>
      <div className="form-grid">
        <SelectField label="Сигнал" value={p.signal} onChange={(signal) => set({ signal })}>
          {signalTypes.map((t) => (
            <option key={t}>{t}</option>
          ))}
        </SelectField>
        <SelectField
          label="Направление"
          value={p.mode}
          onChange={(mode) => set({ mode: mode as SignalParameters['mode'] })}
        >
          <option value="follow">По движению</option>
          <option value="fade">Против движения</option>
        </SelectField>
        <SelectField
          label="Удержание"
          value={String(p.holdSeconds)}
          onChange={(v) => set({ holdSeconds: Number(v) })}
        >
          {holds.map(([value, label]) => (
            <option key={value} value={value}>
              {label}
            </option>
          ))}
        </SelectField>
        {number('Риск на сделку с комиссиями, % депозита', 'riskPct', 0.1, 5, 0.1)}
        {number('Стоп, обычных минутных свечей', 'stopRanges', 0.5, 20, 0.5)}
        {number('Комиссии круга, %', 'feePct', 0, 1, 0.01)}
        {number('Комиссия не больше, % риска', 'maxFeeShare', 5, 90, 1, 100)}
        {number('Плечо не больше', 'maxLeverage', 1, maxLeverage, 1)}
        {number('Сделок одновременно', 'maxPositions', 1, 20, 1)}
      </div>
      <p className="field-hint signal-example">
        Пример: монета с обычной минутной свечой 0,3% — стоп {num(example.stop)}% от входа, комиссия{' '}
        {num(example.feeShare * 100, 0)}% риска
        {example.skipped
          ? ' — больше допустимого, сделка будет пропущена.'
          : `. При стопе бот теряет ${formatMoney(example.risk)} USDT (${num(p.riskPct)}% депозита) вместе с комиссиями, поэтому позиция ${formatMoney(example.notional)} USDT, плечо ${num(example.leverage, 1)}x.`}{' '}
        Чем меньше обычный ход монеты, тем ближе стоп, больше позиция и плечо — и тем большую часть
        риска съедает комиссия.
      </p>
    </>
  );
}

// Controls and state of a signal bot: it trades on the processed recordings from its start moment, so new trades appear
// after each recording is processed (about once an hour with continuous recording).
export function SignalRunPanel({ bot, onChanged }: { bot: Bot; onChanged: () => void }) {
  const [runs, setRuns] = useState<SignalRunView[] | null>(null);
  const [from, setFrom] = useState('');
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
      if (!stopped && running) timer = setTimeout(load, 30000);
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
  const skipped = Object.entries(result?.skipped ?? {}).sort((a, b) => b[1] - a[1]);
  return (
    <section className="panel paper-run signal-run" aria-labelledby="signal-run-title">
      <div className="section-line">
        <h2 id="signal-run-title">Симуляция по записям данных</h2>
        <span className={`research-status ${running ? 'is-live' : ''}`} role="status">
          <span className={`live-dot ${running ? 'on' : ''}`} aria-hidden="true" />
          {running ? 'Работает' : 'Не запущен'}
        </span>
      </div>
      <p className="field-hint">
        Бот берёт сигнал «{bot.signal?.signal}» из каждой новой обработанной записи и торгует его
        виртуально на всех монетах записи. Сделки появляются после обработки записи — при
        непрерывной записи примерно раз в час. Если запустить с даты в прошлом, бот сначала пройдёт
        записи с этого момента. Реальные заявки не отправляются.
      </p>
      <div className="paper-controls">
        {running ? (
          <button
            className="button secondary"
            disabled={busy}
            onClick={() => void act(() => api.stopSignal(bot))}
          >
            <Square size={15} /> Остановить
          </button>
        ) : (
          <>
            <button
              className="button primary"
              disabled={busy}
              onClick={() => void act(() => api.startSignal(bot, null))}
            >
              <Play size={16} /> Запустить с этого момента
            </button>
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
    </section>
  );
}

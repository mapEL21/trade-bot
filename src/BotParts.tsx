import { useEffect, useId, useRef, useState, type FormEvent, type ReactNode } from 'react';
import {
  Activity,
  ArrowRight,
  ArrowUpRight,
  Clock3,
  Copy,
  FolderInput,
  Pause,
  Play,
  Zap,
} from 'lucide-react';
import { SignalFields } from './SignalBot';
import { Empty, Modal } from './components';
import {
  formatMoney,
  importConfig,
  metrics,
  statusLabels,
  onExchange,
  validateConfig,
  type Bot,
  type BotConfig,
  type Execution,
  type Status,
  type Workspace,
} from './model';

// Parts of the bots pages: the status badge, a bot's card in the list, the form that creates and edits a bot, the import
// dialog and the bot's events.
const statusLabel = (status: Status) => statusLabels[status];
export const strategyName = (bot: BotConfig) => ({ signal: 'Сигнал лаборатории' })[bot.strategy];

export function StatusBadge({ status }: { status: Status }) {
  return (
    <span className={`status-badge ${status}`}>
      <span />
      {statusLabel(status)}
    </span>
  );
}
export function Fact({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="fact">
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}
export function BotCard({
  bot,
  onOpen,
  onDuplicate,
}: {
  bot: Bot;
  onOpen: () => void;
  onDuplicate: () => void;
}) {
  const stat = metrics(bot.trades);
  // The deposit of the latest run (for a bot on an exchange, the account balance at its start) plus the closed trades.
  const balance = (bot.deposit ?? bot.budget) + stat.pnl;
  return (
    <article className="panel bot-card">
      <div className="bot-card-header">
        <span className="bot-icon">
          <Zap size={21} />
        </span>
        <button className="bot-name" onClick={onOpen}>
          {bot.name}
          <small>
            {strategyName(bot)} <span>v{bot.strategyVersion}</span>
          </small>
        </button>
        <button
          className="icon-button"
          aria-label={`Дублировать ${bot.name}`}
          title="Создать копию настроек"
          onClick={onDuplicate}
        >
          <Copy size={16} />
        </button>
      </div>
      <div className="bot-market">
        <span className="exchange-label">{bot.exchange}</span>
        <span>{bot.symbol === '*' ? 'Все монеты записи' : bot.symbol}</span>
        <span className="market-type">PERP</span>
      </div>
      <div className="bot-card-stats">
        <div>
          <span>Чистый PnL · вся история</span>
          <strong className={stat.pnl >= 0 ? 'positive' : 'negative'}>
            {formatMoney(stat.pnl, true)} <small>USDT</small>
          </strong>
        </div>
        <div>
          <span>Сделки</span>
          <strong>{stat.count}</strong>
        </div>
        <div>
          <span>{onExchange(bot) ? 'Баланс · демо-счёт' : 'Баланс · симуляция'}</span>
          <strong>
            {formatMoney(balance)} <small>USDT</small>
          </strong>
        </div>
      </div>
      <div className="bot-card-footer">
        <StatusBadge status={bot.status} />
        <div>
          {/* Run controls live on the bot page, next to its state. */}
          <button
            className="icon-button"
            aria-label={`Управление симуляцией ${bot.name}`}
            title="Управление симуляцией"
            onClick={onOpen}
          >
            {bot.status === 'running' ? <Pause size={16} /> : <Play size={16} />}
          </button>
          <button className="text-button" onClick={onOpen}>
            Статистика
            <ArrowUpRight size={15} />
          </button>
        </div>
      </div>
    </article>
  );
}
export function ConfigForm({
  config,
  onSave,
  submitLabel,
  onCancel,
}: {
  config: BotConfig;
  onSave: (c: BotConfig) => Promise<void>;
  submitLabel: string;
  onCancel?: () => void;
}) {
  const [form, setForm] = useState(config);
  const [submitting, setSubmitting] = useState(false);
  const pending = useRef(false);
  const [error, setError] = useState('');
  const errorId = useId();
  useEffect(() => {
    setForm(config);
    setError('');
  }, [config]);
  const density = form.signal?.signal === 'Плотность';
  const exchangeBot = onExchange(form);
  // On an exchange the deposit is the account balance: the budget becomes 100 and the loss limit a percentage of it.
  function setExecution(execution: Execution) {
    if (onExchange({ execution }) === exchangeBot) return setForm({ ...form, execution });
    const lossPct = (form.lossLimit / form.budget) * 100;
    setForm(
      onExchange({ execution })
        ? {
            ...form,
            execution,
            budget: 100,
            lossLimit: Number.isFinite(lossPct) ? Math.round(lossPct * 10) / 10 : 30,
          }
        : { ...form, execution, budget: 100, lossLimit: form.lossLimit },
    );
  }
  // Only the density strategy trades on an exchange so far.
  useEffect(() => {
    if (!density && exchangeBot) setForm((f) => ({ ...f, execution: 'simulation' }));
  }, [density, exchangeBot]);
  async function submit(e: FormEvent) {
    e.preventDefault();
    if (pending.current) return;
    pending.current = true;
    setSubmitting(true);
    try {
      const {
        name,
        exchange,
        symbol,
        budget,
        lossLimit,
        strategy,
        strategyVersion,
        signal,
        execution,
      } = form;
      await onSave(
        validateConfig({
          name,
          exchange,
          symbol,
          budget,
          lossLimit,
          strategy,
          strategyVersion,
          signal,
          execution: execution ?? 'simulation',
        }),
      );
      setError('');
    } catch (e) {
      setError((e as Error).message);
    } finally {
      pending.current = false;
      setSubmitting(false);
    }
  }
  return (
    <form onSubmit={submit} className="config-form" aria-describedby={error ? errorId : undefined}>
      <label className="field">
        <span>Название бота</span>
        <input
          data-autofocus
          value={form.name}
          maxLength={40}
          placeholder="Например, Вынос цены · 5 мин"
          onChange={(e) => setForm({ ...form, name: e.target.value })}
          required
        />
      </label>
      <label className="field">
        <span>Где торгует</span>
        <select
          value={form.execution ?? 'simulation'}
          onChange={(e) => setExecution(e.target.value as Execution)}
        >
          <option value="simulation">Симуляция: виртуальные сделки, без биржи</option>
          <option value="testnet" disabled={!density}>
            Демо-счёт Binance: заявки с ненастоящими деньгами
            {density ? '' : ' (только «Плотность»)'}
          </option>
          <option value="live" disabled>
            Реальный счёт — после проверки на демо-счёте
          </option>
        </select>
      </label>
      {exchangeBot ? (
        <div className="form-grid">
          <p className="field-hint">
            Депозит — баланс счёта в момент запуска; размер сделок считается от него.
          </p>
          <label className="field">
            <span>Лимит убытка, % депозита</span>
            <input
              type="number"
              min="0.1"
              max="100"
              step="0.1"
              value={Number.isNaN(form.lossLimit) ? '' : form.lossLimit}
              onChange={(e) => setForm({ ...form, lossLimit: e.target.valueAsNumber })}
              required
            />
          </label>
        </div>
      ) : (
        <div className="form-grid">
          <label className="field">
            <span>Депозит бота, USDT</span>
            <input
              type="number"
              min="1"
              max="1000000"
              step="0.01"
              value={Number.isNaN(form.budget) ? '' : form.budget}
              onChange={(e) => setForm({ ...form, budget: e.target.valueAsNumber })}
              required
            />
          </label>
          <label className="field">
            <span>Лимит убытка, USDT</span>
            <input
              type="number"
              min="0.01"
              max={form.budget}
              step="0.01"
              value={Number.isNaN(form.lossLimit) ? '' : form.lossLimit}
              onChange={(e) => setForm({ ...form, lossLimit: e.target.valueAsNumber })}
              required
            />
          </label>
        </div>
      )}
      {form.signal && <SignalFields form={form} setForm={setForm} />}
      {error && (
        <p id={errorId} className="form-error" role="alert">
          {error}
        </p>
      )}
      <div className="modal-actions">
        {onCancel && (
          <button type="button" className="button secondary" onClick={onCancel}>
            Отмена
          </button>
        )}
        <button className="button primary" type="submit" disabled={submitting}>
          {submitLabel}
          <ArrowRight size={16} />
        </button>
      </div>
    </form>
  );
}
export function ImportDialog({
  onClose,
  onImport,
}: {
  onClose: () => void;
  onImport: (c: BotConfig) => Promise<void>;
}) {
  const [text, setText] = useState('');
  const [error, setError] = useState('');
  const errorId = useId();
  const [filename, setFilename] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const pending = useRef(false);
  async function file(f?: File) {
    if (!f) return;
    setError('');
    setText('');
    setFilename('');
    if (f.size > 32768) {
      setError('Файл должен быть не больше 32 КБ.');
      return;
    }
    try {
      const contents = await f.text();
      importConfig(contents);
      setText(contents);
      setFilename(f.name);
    } catch (e) {
      setError((e as Error).message);
    }
  }
  return (
    <Modal
      title="Импорт бота"
      subtitle="Добавьте настройки, экспортированные из Trade Bot."
      onClose={onClose}
    >
      <label
        className="upload-zone"
        onDragOver={(e) => e.preventDefault()}
        onDrop={(e) => {
          e.preventDefault();
          void file(e.dataTransfer.files[0]);
        }}
      >
        <FolderInput size={30} />
        <strong>{filename || 'Выберите JSON-файл или перетащите сюда'}</strong>
        <span>Наш формат · версия 1 · до 32 КБ</span>
        <input
          type="file"
          accept=".json,application/json"
          aria-label="Файл настроек бота"
          onChange={(e) => void file(e.target.files?.[0])}
        />
      </label>
      <label className="field">
        <span>Или вставьте содержимое JSON</span>
        <textarea
          rows={6}
          aria-invalid={!!error}
          aria-describedby={error ? errorId : undefined}
          value={text}
          onChange={(e) => {
            setText(e.target.value);
            setError('');
          }}
          placeholder={'{ "schemaVersion": 1, "config": { … } }'}
        />
      </label>
      <p className="field-hint">
        Импортируется только конфигурация. История, статус запуска, API-ключи и программный код не
        переносятся.
      </p>
      {error && (
        <p id={errorId} className="form-error" role="alert">
          {error}
        </p>
      )}
      <div className="modal-actions">
        <button className="button secondary" onClick={onClose}>
          Отмена
        </button>
        <button
          className="button primary"
          disabled={!text.trim() || submitting}
          onClick={async () => {
            if (pending.current) return;
            pending.current = true;
            setSubmitting(true);
            try {
              await onImport(importConfig(text));
            } catch (e) {
              setError((e as Error).message);
            } finally {
              pending.current = false;
              setSubmitting(false);
            }
          }}
        >
          <FolderInput size={16} />
          Импортировать
        </button>
      </div>
    </Modal>
  );
}
export function Events({ events }: { events: Workspace['events'] }) {
  return (
    <section className="panel events-panel">
      <div className="section-line">
        <h3>Журнал событий</h3>
        <span className="muted">Последние {events.length} событий</span>
      </div>
      {events.length ? (
        events.map((e) => (
          <div className="event" key={e.id}>
            <span className="event-icon">
              <Activity size={16} />
            </span>
            <div>
              <strong>{e.botName}</strong>
              <p>{e.text}</p>
            </div>
            <time>
              {new Date(e.at).toLocaleString('ru-RU', {
                day: '2-digit',
                month: '2-digit',
                hour: '2-digit',
                minute: '2-digit',
                timeZone: 'Europe/Moscow',
              })}
            </time>
          </div>
        ))
      ) : (
        <Empty
          icon={<Clock3 size={26} />}
          title="Пока без событий"
          text="Здесь появятся изменения настроек и состояния бота."
        />
      )}
    </section>
  );
}

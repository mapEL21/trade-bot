import { useEffect, useState, type FormEvent } from 'react';
import { KeyRound } from 'lucide-react';
import { api, type ExchangeKeys, type ExchangeLogEntry } from './api';
import { type Bot } from './model';

const time = (ms: number) =>
  new Date(ms).toLocaleString('ru-RU', {
    timeZone: 'Europe/Moscow',
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });

// API keys of Binance's futures test network. They are entered here by the user, checked by asking the exchange for the
// balance, kept encrypted on this computer only and never shown again. A real account is not available yet.
export function ExchangeKeysPanel({ onChanged }: { onChanged?: (keys: ExchangeKeys) => void }) {
  const [keys, setKeys] = useState<ExchangeKeys | null>(null);
  const [apiKey, setApiKey] = useState('');
  const [secret, setSecret] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  useEffect(() => {
    api
      .exchangeKeys()
      .then((k) => {
        setKeys(k);
        onChanged?.(k);
      })
      .catch((e) => setError((e as Error).message));
  }, []);
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError('');
    setMessage('');
    try {
      const result = await api.saveExchangeKeys('testnet', apiKey, secret);
      setApiKey('');
      setSecret('');
      setMessage(
        `Ключи подошли. Баланс тестового счёта: ${result.balance.toLocaleString('ru-RU', { maximumFractionDigits: 2 })} USDT.`,
      );
      const next = await api.exchangeKeys();
      setKeys(next);
      onChanged?.(next);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function remove() {
    setBusy(true);
    try {
      await api.deleteExchangeKeys('testnet');
      const next = await api.exchangeKeys();
      setKeys(next);
      onChanged?.(next);
      setMessage('Ключи удалены с этого компьютера.');
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  const status = keys?.testnet;
  return (
    <details className="panel exchange-keys">
      <summary>
        <KeyRound size={16} aria-hidden="true" /> Тестовая сеть Binance:{' '}
        {status?.configured ? `ключи сохранены (…${status.keyEnd})` : 'ключей нет'}
      </summary>
      <p className="field-hint">
        Тестовая сеть — биржа Binance с ненастоящими деньгами: на ней проверяется, как бот
        отправляет заявки, ставит защитный стоп и сверяется с биржей. Ключи создаются на
        testnet.binancefuture.com (не на основном сайте). Они хранятся только на этом компьютере в
        зашифрованном виде и в интерфейс больше не возвращаются. Реальный счёт пока не подключается.
      </p>
      <form className="scanner-form" onSubmit={save}>
        <label className="field">
          <span>API-ключ</span>
          <input
            value={apiKey}
            onChange={(e) => setApiKey(e.target.value)}
            autoComplete="off"
            required
          />
        </label>
        <label className="field">
          <span>Секрет</span>
          <input
            type="password"
            value={secret}
            onChange={(e) => setSecret(e.target.value)}
            autoComplete="new-password"
            required
          />
        </label>
        <button className="button primary" disabled={busy}>
          Сохранить и проверить
        </button>
        {status?.configured && (
          <button
            type="button"
            className="text-button"
            disabled={busy}
            onClick={() => void remove()}
          >
            Удалить ключи
          </button>
        )}
      </form>
      {message && (
        <p className="field-hint positive" role="status">
          {message}
        </p>
      )}
      {error && <p role="alert">{error}</p>}
    </details>
  );
}

// The run's orders on the exchange as they happened: entries, protective stops, exits, refusals.
export function ExchangeLog({ bot, refresh }: { bot: Bot; refresh: number }) {
  const [entries, setEntries] = useState<ExchangeLogEntry[] | null>(null);
  useEffect(() => {
    api
      .exchangeLog(bot)
      .then(setEntries)
      .catch(() => setEntries(null));
  }, [bot.id, refresh]);
  if (!entries || entries.length === 0) return null;
  return (
    <div
      className="table-scroll"
      tabIndex={0}
      role="region"
      aria-label="Заявки на бирже, прокрутка"
    >
      <table>
        <caption className="sr-only">Заявки бота на бирже</caption>
        <thead>
          <tr>
            <th>Время · МСК</th>
            <th>Монета</th>
            <th>Действие</th>
            <th>Сторона</th>
            <th>Кол-во</th>
            <th>Цена</th>
            <th>Пояснение</th>
          </tr>
        </thead>
        <tbody>
          {entries.map((e, i) => (
            <tr key={`${e.atMs}-${i}`}>
              <td className="mono">{time(e.atMs)}</td>
              <td>{e.symbol || '—'}</td>
              <td className={e.action === 'ошибка' || e.action === 'внимание' ? 'negative' : ''}>
                {e.action}
              </td>
              <td>{e.side === 'BUY' ? 'покупка' : e.side === 'SELL' ? 'продажа' : '—'}</td>
              <td className="mono">{e.quantity || '—'}</td>
              <td className="mono">{e.price || '—'}</td>
              <td className="muted reason">{e.message}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

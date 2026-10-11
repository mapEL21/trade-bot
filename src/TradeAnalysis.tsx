import type { ReactNode } from 'react';
import { Activity, ArrowDownToLine, Gauge, ShieldCheck, TrendingUp, Wallet } from 'lucide-react';
import { Chart, Empty, Tooltip, download } from './components';
import {
  csvCell,
  formatMoney,
  metrics,
  net,
  tradeMoveBps,
  tradeNotional,
  tradeQuality,
  type Trade,
} from './model';

// The analysis every strategy gets for free from its closed trades: result, balance, curve, trade quality and history.
// Strategy-specific views (for example the leader analysis of leader-lag) live next to their strategy, see strategyViews.tsx.
export function Stats({
  stat,
  budget,
  balance,
}: {
  stat: ReturnType<typeof metrics>;
  budget: number;
  balance: number;
}) {
  return (
    <div className="stats-grid">
      <Stat
        label="Чистый результат"
        value={`${formatMoney(stat.pnl, true)}`}
        unit="USDT"
        icon={<TrendingUp size={18} />}
        accent={stat.pnl >= 0 ? 'positive' : 'negative'}
        note={`После комиссий · ${formatMoney(stat.fees)} USDT`}
      />
      <Stat
        label={'Баланс · симуляция'}
        value={formatMoney(balance)}
        unit="USDT"
        icon={<Wallet size={18} />}
        note={`Выделено ${formatMoney(budget)} USDT, изменение — по закрытым сделкам симуляции`}
      />
      <Stat
        label="Прибыльных сделок"
        value={stat.winRate === null ? '—' : stat.winRate.toFixed(1).replace('.', ',')}
        unit={stat.winRate === null ? '' : '%'}
        icon={<Gauge size={18} />}
        note={`${stat.count} закрытых ${'сделок'}`}
      />
      <Stat
        label="Макс. просадка"
        value={formatMoney(stat.drawdown)}
        unit="USDT"
        icon={<ShieldCheck size={18} />}
        note="По закрытым сделкам за период"
      />
    </div>
  );
}
function Stat({
  label,
  value,
  unit,
  icon,
  note,
  accent = '',
}: {
  label: string;
  value: string;
  unit: string;
  icon: ReactNode;
  note: string;
  accent?: string;
}) {
  return (
    <div className="panel stat">
      <div className="stat-label">
        {label}
        {icon}
      </div>
      <div className={`stat-value ${accent}`}>
        {value}
        <span>{unit}</span>
      </div>
      <div className="stat-note">{note}</div>
    </div>
  );
}
export function Performance({ trades, pnl }: { trades: Trade[]; pnl: number }) {
  return (
    <section className="panel performance">
      <div className="section-line">
        <h3>
          Динамика результата{' '}
          <Tooltip
            text={
              'Накопленный результат закрытых сделок живой симуляции за выбранный период с учётом комиссий. Это не реальная торговля.'
            }
          />
        </h3>
        <span className="chart-legend">
          <span className="tiny-dot" />
          {'Чистый PnL · симуляция'}
        </span>
      </div>
      <div className="chart-summary">
        <strong className={pnl >= 0 ? 'positive' : 'negative'}>
          {formatMoney(pnl, true)} <span>USDT</span>
        </strong>
        <span>за выбранный период</span>
      </div>
      {trades.length ? (
        <Chart trades={trades} />
      ) : (
        <Empty
          icon={<TrendingUp size={26} />}
          title="Здесь появится кривая результата"
          text={'Закрытых сделок симуляции за выбранный период нет.'}
        />
      )}
    </section>
  );
}
// Prices keep enough significant digits to show small moves on cheap coins: 0.1234 → 0.12343 is a real 2.4 b.p. move.
const formatPrice = (n: number) => String(Number(n.toPrecision(7)));
// Price move in the trade's direction, before fees.
// Price move in the trade's direction, before fees, in percent.
const tradeMove = (t: Trade) => tradeMoveBps(t) / 100;
function formatHold(seconds: number | null) {
  if (seconds === null) return '—';
  if (seconds < 60)
    return `${seconds.toLocaleString('ru-RU', { maximumFractionDigits: seconds < 10 ? 2 : 1 })} с`;
  const m = Math.floor(seconds / 60);
  return `${m} мин ${Math.round(seconds - m * 60)} с`;
}
const moscowTime = (at: string) =>
  new Date(at).toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    timeZone: 'Europe/Moscow',
  });
const bps = (n: number | null, signed = true) =>
  n === null
    ? '—'
    : `${signed && n > 0 ? '+' : ''}${n.toLocaleString('ru-RU', { maximumFractionDigits: 1 })} б.п.`;
const share = (n: number | null) =>
  n === null ? '—' : `${n.toLocaleString('ru-RU', { maximumFractionDigits: 0 })}%`;
function QualityItem({ label, value, note }: { label: string; value: ReactNode; note: string }) {
  return (
    <div className="quality-item">
      <span>{label}</span>
      <strong>{value}</strong>
      <small>{note}</small>
    </div>
  );
}
// Trade quality for any strategy: where the result is lost — on entries (price never moved past the fees) or on
// exits (it did, and the move was given back) — and how each exit reason performs.
export function TradeQuality({ trades }: { trades: Trade[] }) {
  const q = tradeQuality(trades);
  if (!q.count) return null;
  const hints: string[] = [];
  if (q.reachedFeesShare !== null && q.reachedFeesShare < 50)
    hints.push(
      'В большинстве сделок цена ни разу не ушла в сторону сделки дальше комиссий круга: результат теряется на входе — сигнал не даёт достаточного движения.',
    );
  else if (q.givenBackBps !== null && q.avgFeeBps !== null && q.givenBackBps > q.avgFeeBps)
    hints.push(
      'Цена часто уходила дальше комиссий, но к выходу движение возвращалось: результат теряется на выходе.',
    );
  if (q.count < 30)
    hints.push(`Сделок пока ${q.count}: выводы предварительные, нужна серия запусков.`);
  return (
    <section className="panel quality-panel">
      <div className="section-line">
        <h3>
          Качество сделок{' '}
          <Tooltip
            text={
              'Общий разбор для любой стратегии. Ход цены — движение в сторону сделки до комиссий, в б.п. (0,01%). Лучший и худший ход — крайние значения за время сделки по цене, по которой можно было выйти.'
            }
          />
        </h3>
      </div>
      <div className="quality-grid">
        <QualityItem
          label="Средняя сделка"
          value={q.avgNet === null ? '—' : `${formatMoney(q.avgNet, true)} USDT`}
          note={`прибыльная ${q.avgWin === null ? '—' : formatMoney(q.avgWin, true)} · убыточная ${q.avgLoss === null ? '—' : formatMoney(q.avgLoss, true)}`}
        />
        <QualityItem
          label="Профит-фактор"
          value={q.profitFactor === null ? '—' : q.profitFactor.toFixed(2).replace('.', ',')}
          note="прибыль ÷ убыток закрытых сделок; больше 1 — в плюсе"
        />
        <QualityItem
          label="Ход цены / комиссии"
          value={`${bps(q.avgMoveBps)} / ${bps(q.avgFeeBps, false)}`}
          note="в среднем за сделку; ход должен быть больше комиссий круга"
        />
        <QualityItem label="Время в сделке" value={formatHold(q.avgHoldSeconds)} note="в среднем" />
        <QualityItem
          label="Лучший / худший ход"
          value={`${bps(q.avgFavorableBps)} / ${bps(q.avgAdverseBps)}`}
          note={`в среднем за время сделки · есть у ${q.measured} из ${q.count}`}
        />
        <QualityItem
          label="Цена прошла комиссии"
          value={share(q.reachedFeesShare)}
          note="доля сделок, где лучший ход был больше комиссий круга"
        />
        <QualityItem
          label="Отдано до выхода"
          value={bps(q.givenBackBps, false)}
          note="от лучшего хода до цены выхода, в среднем"
        />
      </div>
      {hints.length > 0 && <p className="quality-hint">{hints.join(' ')}</p>}
      <div className="table-scroll">
        <table>
          <thead>
            <tr>
              <th>Причина выхода</th>
              <th>Сделок</th>
              <th>Прибыльных</th>
              <th>Ход цены</th>
              <th>Лучший ход</th>
              <th>В сделке</th>
              <th>Чистый PnL</th>
            </tr>
          </thead>
          <tbody>
            {q.reasons.map((r) => (
              <tr key={r.reason}>
                <td>{r.reason}</td>
                <td>{r.count}</td>
                <td>{share(r.winRate)}</td>
                <td className={r.avgMoveBps >= 0 ? 'positive' : 'negative'}>{bps(r.avgMoveBps)}</td>
                <td>{bps(r.avgFavorableBps)}</td>
                <td>{formatHold(r.avgHoldSeconds)}</td>
                <td className={r.pnl >= 0 ? 'positive' : 'negative'}>{formatMoney(r.pnl, true)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
// Trade history shared by every strategy: instrument and exchange of each trade come from the run that made it.
// The leader column appears only when some trade has one (a strategy-specific field).
export function TradesTable({
  trades,
  botName,
  showBot = false,
}: {
  trades: (Trade & { botName?: string })[];
  botName?: string;
  showBot?: boolean;
}) {
  const sorted = [...trades].sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
  const withLeader = trades.some((t) => t.leader);
  function csv() {
    const rows = [
      [
        'Вход UTC',
        'Выход UTC',
        'Бот',
        'Пара',
        'Биржа',
        'Поводырь',
        'Сторона',
        'Вход',
        'Выход',
        'Движение %',
        'Время в сделке, с',
        'Размер USDT',
        'Количество',
        'Комиссия USDT',
        'Чистый PnL USDT',
        'Причина выхода',
      ],
      ...sorted.map((t) => [
        t.openedAt ?? '',
        t.at,
        t.botName ?? botName ?? '',
        t.symbol ?? '',
        t.exchange ?? '',
        t.leader ?? '',
        t.side,
        t.entry,
        t.exit,
        tradeMove(t),
        t.holdSeconds ?? '',
        tradeNotional(t),
        t.quantity,
        t.fee,
        net(t),
        t.exitReason ?? '',
      ]),
    ];
    download(
      'simulated-trades.csv',
      '﻿' + rows.map((r) => r.map(csvCell).join(';')).join('\r\n'),
      'text/csv;charset=utf-8',
    );
  }
  return (
    <section className="panel trades-panel">
      <div className="section-line">
        <h3>
          История сделок <span className="count-badge">{trades.length}</span>
        </h3>
        <button className="text-button" disabled={!trades.length} onClick={csv}>
          <ArrowDownToLine size={15} />
          Скачать CSV
        </button>
      </div>
      {!trades.length ? (
        <Empty
          icon={<Activity size={26} />}
          title="Сделок пока нет"
          text={'Здесь появятся закрытые сделки живой симуляции бота.'}
        />
      ) : (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th>Вход · МСК</th>
                {showBot && <th>Бот</th>}
                <th>Инструмент</th>
                <th>Биржа</th>
                {withLeader && <th>Поводырь</th>}
                <th>Сторона</th>
                <th>Вход → выход</th>
                <th>В сделке</th>
                <th>Размер, USDT</th>
                <th>Комиссия</th>
                <th>Чистый PnL</th>
                <th>Причина выхода</th>
              </tr>
            </thead>
            <tbody>
              {sorted.map((t) => {
                const move = tradeMove(t);
                return (
                  <tr key={t.id}>
                    <td className="mono">{moscowTime(t.openedAt ?? t.at)}</td>
                    {showBot && <td>{t.botName ?? botName}</td>}
                    <td className="table-symbol">
                      {t.symbol ?? '—'}
                      {t.simulated && (
                        <span
                          className="sim-tag"
                          title="Сделка живой симуляции, не реальное исполнение"
                        >
                          SIM
                        </span>
                      )}
                    </td>
                    <td>{t.exchange ?? '—'}</td>
                    {withLeader && <td>{t.leader ?? '—'}</td>}
                    <td>
                      <span className={`side ${t.side}`}>
                        {t.side === 'long' ? 'Лонг' : 'Шорт'}
                      </span>
                    </td>
                    <td className="mono">
                      {formatPrice(t.entry)} <span className="muted">→</span> {formatPrice(t.exit)}{' '}
                      <span
                        className={move >= 0 ? 'positive' : 'negative'}
                        title="Движение цены в сторону сделки, до комиссий"
                      >
                        {move >= 0 ? '+' : ''}
                        {move.toLocaleString('ru-RU', { maximumFractionDigits: 3 })}%
                      </span>
                    </td>
                    <td>{t.holdSeconds != null ? formatHold(t.holdSeconds) : '—'}</td>
                    <td>{formatMoney(tradeNotional(t))}</td>
                    <td>{t.fee.toFixed(3)}</td>
                    <td className={net(t) >= 0 ? 'positive' : 'negative'}>
                      {formatMoney(net(t), true)}
                    </td>
                    <td className="muted">{t.exitReason ?? '—'}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      <div className="table-note">
        {
          'Сделки живой симуляции: исполнения смоделированы по публичным данным, реальных ордеров не было.'
        }{' '}
        Комиссии и результаты указаны в USDT.
      </div>
    </section>
  );
}

import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { ArrowDownToLine, Check, ChevronDown, CircleHelp, X } from 'lucide-react';
import { formatMoney, net, type Trade } from './model';

export function Modal({
  title,
  subtitle,
  children,
  onClose,
}: {
  title: string;
  subtitle?: string;
  children: ReactNode;
  onClose: () => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    const dialog = ref.current;
    dialog?.showModal();
    dialog?.querySelector<HTMLElement>('[data-autofocus]')?.focus();
    return () => {
      dialog?.close();
      previous?.focus();
    };
  }, []);
  return (
    <dialog
      ref={ref}
      className="modal"
      aria-labelledby={titleId}
      onCancel={onClose}
      onClick={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div className="modal-inner">
        <div className="modal-heading">
          <div>
            <h2 id={titleId}>{title}</h2>
            {subtitle && <p>{subtitle}</p>}
          </div>
          <button className="icon-button" aria-label="Закрыть окно" onClick={onClose}>
            <X size={20} />
          </button>
        </div>
        {children}
      </div>
    </dialog>
  );
}
export function Empty({
  icon,
  title,
  text,
  action,
}: {
  icon: ReactNode;
  title: string;
  text: string;
  action?: ReactNode;
}) {
  return (
    <div className="empty">
      <div className="empty-icon">{icon}</div>
      <h3>{title}</h3>
      <p>{text}</p>
      {action}
    </div>
  );
}
export function Tooltip({ text }: { text: string }) {
  return (
    <button type="button" className="help" aria-label={text}>
      <CircleHelp size={14} />
      <span role="tooltip">{text}</span>
    </button>
  );
}
export type ChartMode = 'line' | 'bars';
// Result of closed trades: with fees (solid) and without them (dashed, transparent), so the cost of fees is visible.
// Line: the running total. Bars: each trade's result, or each day's when there are many trades.
export function Chart({ trades, mode = 'line' }: { trades: Trade[]; mode?: ChartMode }) {
  const [active, setActive] = useState<number | null>(null);
  const [width, setWidth] = useState(748);
  const container = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const observer = new ResizeObserver((entries) =>
      setWidth(Math.max(240, entries[0].contentRect.width)),
    );
    if (container.current) observer.observe(container.current);
    return () => observer.disconnect();
  }, []);
  useEffect(() => setActive(null), [mode]);
  const id = useId().replace(/:/g, '');
  const sorted = [...trades].sort((a, b) => Date.parse(a.at) - Date.parse(b.at));
  const day = (at: string) =>
    new Date(at).toLocaleDateString('ru-RU', {
      day: 'numeric',
      month: 'short',
      timeZone: 'Europe/Moscow',
    });
  const time = (at: string) =>
    new Date(at).toLocaleTimeString('ru-RU', {
      hour: '2-digit',
      minute: '2-digit',
      timeZone: 'Europe/Moscow',
    });
  const byDay = mode === 'bars' && sorted.length > 80;
  // Bars: one per trade, or per day; line: the running totals, starting from zero.
  const items: { label: string; net: number; gross: number }[] = [];
  if (mode === 'bars') {
    for (const t of sorted) {
      const label = byDay ? day(t.at) : `${day(t.at)}, ${time(t.at)}`;
      const last = items[items.length - 1];
      if (byDay && last?.label === label) {
        last.net += net(t);
        last.gross += t.gross;
      } else items.push({ label, net: net(t), gross: t.gross });
    }
  } else {
    let n = 0;
    let g = 0;
    items.push({ label: sorted.length ? day(sorted[0].at) : '', net: 0, gross: 0 });
    for (const t of sorted)
      items.push({ label: day(t.at), net: (n += net(t)), gross: (g += t.gross) });
  }
  const all = items.flatMap((p) => [p.net, p.gross]);
  const high = Math.max(...all, 0.1) * 1.16;
  const low = Math.min(...all, 0) * 1.16;
  const plotWidth = width - 62;
  const y = (v: number) => 180 - ((v - low) / (high - low)) * 164;
  const slot = plotWidth / Math.max(1, items.length);
  const x = (i: number) =>
    mode === 'bars'
      ? 12 + slot * i + slot / 2
      : 12 + (i / Math.max(1, items.length - 1)) * plotWidth;
  const path = (key: 'net' | 'gross') =>
    items.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(i)},${y(p[key])}`).join(' ');
  const total =
    mode === 'bars' ? items.reduce((s, p) => s + p.net, 0) : items[items.length - 1].net;
  const current = active === null ? null : items[active];
  const bar = Math.max(1, Math.min(28, slot * 0.7));
  return (
    <div className="chart-wrap">
      <div className="chart-plot" ref={container}>
        <svg
          viewBox={`0 0 ${width} 210`}
          role="img"
          aria-label={
            mode === 'bars'
              ? `Результат ${byDay ? 'по дням' : 'по сделкам'}: ${formatMoney(total)} USDT с комиссиями, ${trades.length} сделок`
              : `Кривая накопленного результата: ${formatMoney(total)} USDT с комиссиями, ${trades.length} сделок`
          }
          onMouseLeave={() => setActive(null)}
        >
          <defs>
            <linearGradient id={id} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor="#55d6b1" stopOpacity=".19" />
              <stop offset="100%" stopColor="#55d6b1" stopOpacity="0" />
            </linearGradient>
          </defs>
          {[16, 57, 98, 139, 180].map((yy, i) => (
            <g key={yy}>
              <line
                x1="12"
                x2={12 + plotWidth}
                y1={yy}
                y2={yy}
                stroke="#232c3c"
                strokeDasharray="3 5"
              />
              <text x={width - 1} y={yy + 4} textAnchor="end" fill="#79869b" fontSize="10">
                {formatMoney(high - (i / 4) * (high - low))}
              </text>
            </g>
          ))}
          {mode === 'line' ? (
            <>
              <path d={`${path('net')} L${12 + plotWidth},180 L12,180 Z`} fill={`url(#${id})`} />
              <path
                className="chart-gross"
                d={path('gross')}
                fill="none"
                stroke="#9fb0c6"
                strokeOpacity=".55"
                strokeWidth="1.6"
                strokeDasharray="5 4"
              />
              <path
                d={path('net')}
                fill="none"
                stroke="#55d6b1"
                strokeWidth="2.5"
                strokeLinejoin="round"
                strokeLinecap="round"
              />
            </>
          ) : (
            <>
              <line x1="12" x2={12 + plotWidth} y1={y(0)} y2={y(0)} stroke="#3a4658" />
              {items.map((p, i) => (
                <g key={i}>
                  <rect
                    className="chart-gross"
                    x={x(i) - bar / 2}
                    width={bar}
                    y={Math.min(y(p.gross), y(0))}
                    height={Math.max(1, Math.abs(y(p.gross) - y(0)))}
                    fill="none"
                    stroke="#9fb0c6"
                    strokeOpacity=".55"
                    strokeDasharray="3 2"
                  />
                  <rect
                    x={x(i) - bar / 2}
                    width={bar}
                    y={Math.min(y(p.net), y(0))}
                    height={Math.max(1, Math.abs(y(p.net) - y(0)))}
                    fill={p.net >= 0 ? '#55d6b1' : '#f0616d'}
                    fillOpacity={active === i ? 1 : 0.8}
                  />
                </g>
              ))}
            </>
          )}
          {current && active !== null && (
            <>
              <line
                x1={x(active)}
                x2={x(active)}
                y1="12"
                y2="180"
                stroke="#718395"
                strokeDasharray="3 4"
              />
              {mode === 'line' && (
                <circle
                  cx={x(active)}
                  cy={y(current.net)}
                  r="5"
                  fill="#55d6b1"
                  stroke="#101723"
                  strokeWidth="3"
                />
              )}
              <g
                transform={`translate(${Math.max(12, Math.min(width - 175, x(active) - 85))},${Math.max(3, Math.min(y(current.net), 120) - 52)})`}
              >
                <rect width="170" height="44" rx="6" fill="#273244" />
                <text x="85" y="18" textAnchor="middle" fill="#eef5fa" fontSize="12">
                  {formatMoney(current.net, true)} USDT с комиссиями
                </text>
                <text x="85" y="35" textAnchor="middle" fill="#9fb0c6" fontSize="11">
                  {formatMoney(current.gross, true)} без комиссий
                </text>
              </g>
            </>
          )}
          {items.map((_, i) => (
            <rect
              key={i}
              x={Math.max(0, x(i) - slot / 2)}
              y="0"
              width={slot + 2}
              height="185"
              fill="transparent"
              onMouseEnter={() => setActive(i)}
              onTouchStart={() => setActive(i)}
            />
          ))}
          {sorted.length > 0 &&
            [0, Math.floor((items.length - 1) / 2), items.length - 1].map((i, n) => (
              <text
                key={n}
                x={[12, 12 + plotWidth / 2, 12 + plotWidth][n]}
                y="205"
                textAnchor={n === 0 ? 'start' : n === 2 ? 'end' : 'middle'}
                fill="#79869b"
                fontSize="10"
              >
                {items[i].label.split(',')[0]}
              </text>
            ))}
        </svg>
      </div>
    </div>
  );
}
export function SelectField({
  label,
  value,
  onChange,
  children,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  children: ReactNode;
}) {
  const id = useId();
  return (
    <label className="field" htmlFor={id}>
      <span>{label}</span>
      <div className="select-wrap">
        <select id={id} value={value} onChange={(e) => onChange(e.target.value)}>
          {children}
        </select>
        <ChevronDown size={16} />
      </div>
    </label>
  );
}
export function DownloadButton({ onClick }: { onClick: () => void }) {
  return (
    <button className="button secondary" onClick={onClick}>
      <ArrowDownToLine size={16} /> Экспорт
    </button>
  );
}
export function Toast({ message }: { message: string }) {
  return (
    <div className="toast" role="status">
      <Check size={18} />
      {message}
    </div>
  );
}
export function download(name: string, text: string, type = 'application/json') {
  const url = URL.createObjectURL(new Blob([text], { type }));
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

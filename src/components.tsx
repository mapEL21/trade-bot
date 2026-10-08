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
export function Chart({ trades }: { trades: Trade[] }) {
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
  const id = useId().replace(/:/g, '');
  let sum = 0;
  const sorted = [...trades].sort((a, b) => Date.parse(a.at) - Date.parse(b.at));
  const values = [0, ...sorted.map((t) => (sum += net(t)))];
  const high = Math.max(...values, 0.1) * 1.16;
  const low = Math.min(...values, 0) * 1.16;
  const plotWidth = width - 62;
  const points = values.map((v, i) => ({
    x: 12 + (i / (values.length - 1)) * plotWidth,
    y: 180 - ((v - low) / (high - low)) * 164,
  }));
  const path = points.map((p, i) => `${i === 0 ? 'M' : 'L'}${p.x},${p.y}`).join(' ');
  const current = active === null ? null : points[active];
  return (
    <div className="chart-wrap">
      <div className="chart-plot" ref={container}>
        <svg
          viewBox={`0 0 ${width} 210`}
          role="img"
          aria-label={`Демонстрационная кривая накопленного результата: ${formatMoney(sum)} USDT, ${trades.length} сделок`}
          onMouseLeave={() => setActive(null)}
        >
          <defs>
            <linearGradient id={id} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor="#55d6b1" stopOpacity=".19" />
              <stop offset="100%" stopColor="#55d6b1" stopOpacity="0" />
            </linearGradient>
          </defs>
          {[16, 57, 98, 139, 180].map((y, i) => (
            <g key={y}>
              <line
                x1="12"
                x2={12 + plotWidth}
                y1={y}
                y2={y}
                stroke="#232c3c"
                strokeDasharray="3 5"
              />
              <text x={width - 1} y={y + 4} textAnchor="end" fill="#79869b" fontSize="10">
                {formatMoney(high - (i / 4) * (high - low))}
              </text>
            </g>
          ))}
          <path d={`${path} L${12 + plotWidth},180 L12,180 Z`} fill={`url(#${id})`} />
          <path
            d={path}
            fill="none"
            stroke="#55d6b1"
            strokeWidth="2.5"
            strokeLinejoin="round"
            strokeLinecap="round"
          />
          {current && (
            <>
              <line
                x1={current.x}
                x2={current.x}
                y1="12"
                y2="180"
                stroke="#718395"
                strokeDasharray="3 4"
              />
              <circle
                cx={current.x}
                cy={current.y}
                r="5"
                fill="#55d6b1"
                stroke="#101723"
                strokeWidth="3"
              />
              <g
                transform={`translate(${Math.max(12, Math.min(width - 115, current.x - 45))},${Math.max(3, current.y - 40)})`}
              >
                <rect width="110" height="27" rx="6" fill="#273244" />
                <text x="55" y="18" textAnchor="middle" fill="#eef5fa" fontSize="12">
                  {formatMoney(values[active!], true)} USDT
                </text>
              </g>
            </>
          )}
          {points.map((p, i) => (
            <rect
              key={i}
              x={Math.max(0, p.x - plotWidth / values.length / 2)}
              y="0"
              width={plotWidth / values.length + 2}
              height="185"
              fill="transparent"
              onMouseEnter={() => setActive(i)}
              onTouchStart={() => setActive(i)}
            />
          ))}
          {[0, Math.floor((sorted.length - 1) / 2), sorted.length - 1].map((i, n) => (
            <text
              key={n}
              x={[12, 12 + plotWidth / 2, 12 + plotWidth][n]}
              y="205"
              textAnchor={n === 0 ? 'start' : n === 2 ? 'end' : 'middle'}
              fill="#79869b"
              fontSize="10"
            >
              {new Date(sorted[i].at).toLocaleDateString('ru-RU', {
                day: 'numeric',
                month: 'short',
                timeZone: 'Europe/Moscow',
              })}
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

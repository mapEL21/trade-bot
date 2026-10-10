import { useEffect, useId, useRef, useState } from 'react';
import { ChevronDown, Search } from 'lucide-react';

interface PairOption {
  coin: string;
  symbol: string;
}
export const marketSymbol = (exchange: string, coin: string) =>
  exchange === 'hyperliquid' ? coin : exchange === 'okx' ? `${coin}-USDT-SWAP` : `${coin}USDT`;

export function MarketPairPicker({
  instruments,
  coin,
  sourceExchange,
  disabled,
  loading,
  onChange,
}: {
  instruments: PairOption[];
  coin: string;
  sourceExchange: string;
  disabled: boolean;
  loading: boolean;
  onChange: (coin: string) => void;
}) {
  const id = useId();
  const input = useRef<HTMLInputElement>(null);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [highlight, setHighlight] = useState(0);
  const selected = instruments.find((item) => item.coin === coin);
  const normalise = (value: string) => value.toUpperCase().replace(/[\s/_.-]/g, '');
  const filtered = instruments.filter((item) =>
    [item.coin, item.symbol, marketSymbol(sourceExchange, item.coin)].some((value) =>
      normalise(value).includes(normalise(query)),
    ),
  );
  const expanded = open && !disabled;
  const activeIndex = Math.min(highlight, filtered.length - 1);
  const label = (item: PairOption) =>
    `${item.coin} · ${marketSymbol(sourceExchange, item.coin)} ↔ ${item.symbol}`;

  useEffect(() => {
    if (disabled) setOpen(false);
  }, [disabled]);
  useEffect(() => {
    if (expanded && activeIndex >= 0)
      document.getElementById(`${id}-option-${activeIndex}`)?.scrollIntoView({ block: 'nearest' });
  }, [expanded, activeIndex, id]);

  function choose(item: PairOption) {
    onChange(item.coin);
    setOpen(false);
    setQuery('');
    input.current?.focus();
  }
  function show() {
    if (disabled) return;
    setOpen(true);
    setQuery('');
    setHighlight(
      Math.max(
        0,
        instruments.findIndex((item) => item.coin === coin),
      ),
    );
  }

  return (
    <div
      className="field market-pair-field"
      onBlur={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
          setOpen(false);
          setQuery('');
        }
      }}
    >
      <label htmlFor={id}>Торговая пара</label>
      <div className="market-pair-input">
        <Search size={16} aria-hidden="true" />
        <input
          ref={input}
          id={id}
          role="combobox"
          aria-autocomplete="list"
          aria-expanded={expanded}
          aria-controls={`${id}-list`}
          aria-activedescendant={
            expanded && activeIndex >= 0 ? `${id}-option-${activeIndex}` : undefined
          }
          aria-describedby={`${id}-hint`}
          autoComplete="off"
          disabled={disabled}
          value={expanded ? query : selected ? label(selected) : disabled && coin ? coin : ''}
          placeholder={loading ? 'Загружаем пары…' : 'Поиск по тикеру или паре'}
          onFocus={show}
          onClick={() => {
            if (!open) show();
          }}
          onChange={(event) => {
            setQuery(event.target.value);
            setHighlight(0);
            setOpen(true);
          }}
          onKeyDown={(event) => {
            if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
              event.preventDefault();
              if (!expanded) show();
              else
                setHighlight((index) =>
                  Math.max(
                    0,
                    Math.min(filtered.length - 1, index + (event.key === 'ArrowDown' ? 1 : -1)),
                  ),
                );
            } else if (event.key === 'Enter') {
              event.preventDefault();
              if (expanded && filtered[activeIndex]) choose(filtered[activeIndex]);
              else if (!expanded) show();
            } else if (event.key === 'Escape') {
              event.preventDefault();
              setOpen(false);
              setQuery('');
            }
          }}
        />
        <ChevronDown size={16} aria-hidden="true" />
      </div>
      {expanded && (
        <div className="market-pair-menu">
          <div id={`${id}-list`} role="listbox" aria-label="Доступные торговые пары">
            {filtered.map((item, index) => (
              <div
                key={item.coin}
                id={`${id}-option-${index}`}
                role="option"
                aria-selected={index === activeIndex}
                className={index === activeIndex ? 'is-highlighted' : ''}
                onMouseDown={(event) => event.preventDefault()}
                onClick={() => choose(item)}
              >
                <strong>{item.coin}</strong>
                <span>
                  {marketSymbol(sourceExchange, item.coin)} ↔ {item.symbol}
                </span>
              </div>
            ))}
          </div>
          <p role="status">
            {filtered.length
              ? `Найдено пар: ${filtered.length}`
              : 'Совпадений нет. Попробуйте другой тикер.'}
          </p>
        </div>
      )}
      <span className="sr-only" id={`${id}-hint`}>
        Введите тикер. Используйте стрелки и Enter для выбора, Escape для закрытия.
      </span>
    </div>
  );
}

import { useEffect, useState } from 'react';
import { api, type InstrumentInfo, type ResearchMarketList } from './api';
import { MarketPairPicker } from './MarketPairPicker';
import { SelectField } from './components';
import {
  maxLeverage,
  paperExchanges,
  type BotConfig,
  type Exchange,
  type LeaderLagSettings,
} from './model';
import {
  defaultFees,
  fromDisplay,
  settingMeta,
  toDisplay,
  unitLabel,
  type NumericSetting,
} from './strategySettings';

const n = (value: number, digits = 8) =>
  value.toLocaleString('ru-RU', { maximumFractionDigits: digits });
const coinOf = (symbol: string) => symbol.replace(/USDT$/, '');
const floorToStep = (value: number, step: number) => Math.floor(value / step + 1e-9) * step;

// Why this order size cannot be placed, using today's exchange rules; null when it fits or the rules are unknown.
export function sizeProblem(
  settings: LeaderLagSettings,
  budget: number,
  info: InstrumentInfo | null,
  leverage = 1,
): string | null {
  if (info?.maxLeverage && leverage > info.maxLeverage)
    return `Биржа допускает для ${info.coin} плечо не больше ${info.maxLeverage}x.`;
  if (!info?.lastPrice) return null;
  const price = info.lastPrice;
  const quantity = floorToStep(
    settings.orderQuantity ?? settings.orderNotional / price,
    info.quantityStep,
  );
  const notional = quantity * price;
  const minimum =
    info.minimumOrderNotional ?? Math.max(info.minimumNotional, info.minimumQuantity * price);
  if (quantity < info.minimumQuantity || notional < info.minimumNotional)
    return `Заявка меньше минимальной: после округления до шага ${n(info.quantityStep)} это ${n(quantity)} ${info.coin} ≈ ${n(notional, 4)} USDT, а биржа принимает от ≈ ${n(minimum, 4)} USDT.`;
  if (info.maximumQuantity && quantity > info.maximumQuantity)
    return `Заявка больше максимальной: ${n(quantity)} при пределе ${n(info.maximumQuantity)} ${info.coin}.`;
  // The margin, not the nominal, has to fit in the allocated balance.
  if (notional > budget * leverage)
    return leverage > 1
      ? `Заявка ≈ ${n(notional, 2)} USDT при плече ${leverage}x требует маржи ≈ ${n(notional / leverage, 2)} USDT — больше выделенного баланса ${n(budget, 2)} USDT.`
      : `Заявка ≈ ${n(notional, 2)} USDT больше выделенного баланса ${n(budget, 2)} USDT. Увеличьте баланс или плечо.`;
  return null;
}

export function LeaderLagFields({
  form,
  setForm,
  onInstrument,
}: {
  form: BotConfig;
  setForm: (update: (f: BotConfig) => BotConfig) => void;
  onInstrument: (info: InstrumentInfo | null) => void;
}) {
  const params = form.parameters!;
  const settings = params.settings;
  const coin = coinOf(form.symbol);
  const [markets, setMarkets] = useState<ResearchMarketList | null>(null);
  const [marketsError, setMarketsError] = useState('');
  const [marketsLoading, setMarketsLoading] = useState(false);
  const [info, setInfo] = useState<InstrumentInfo | null>(null);
  const [infoState, setInfoState] = useState('');
  const [percentUnits, setPercent] = useState(true);
  const exchange = form.exchange.toLowerCase();
  const signal = params.signalExchange.toLowerCase();
  useEffect(() => {
    const controller = new AbortController();
    setMarketsLoading(true);
    setMarketsError('');
    api
      .researchMarkets(exchange, signal, controller.signal)
      .then((value) => {
        if (!controller.signal.aborted) setMarkets(value);
      })
      .catch((e) => {
        if (!controller.signal.aborted) setMarketsError((e as Error).message);
      })
      .finally(() => {
        if (!controller.signal.aborted) setMarketsLoading(false);
      });
    return () => controller.abort();
  }, [exchange, signal]);
  useEffect(() => {
    const controller = new AbortController();
    setInfo(null);
    onInstrument(null);
    if (!coin) return;
    setInfoState('Загружаем правила инструмента…');
    api
      .instrument(exchange, coin, controller.signal)
      .then((value) => {
        if (controller.signal.aborted) return;
        setInfo(value);
        onInstrument(value);
        setInfoState('');
      })
      .catch((e) => {
        if (!controller.signal.aborted) setInfoState((e as Error).message);
      });
    return () => controller.abort();
    // onInstrument is a stable state setter of the parent form.
  }, [exchange, coin]);
  const setSetting = (key: keyof LeaderLagSettings, value: number | string | null) =>
    setForm((f) => ({
      ...f,
      parameters: { ...f.parameters!, settings: { ...f.parameters!.settings, [key]: value } },
    }));
  const setParams = (next: Partial<NonNullable<BotConfig['parameters']>>) =>
    setForm((f) => ({ ...f, parameters: { ...f.parameters!, ...next } }));
  const inCoins = settings.orderQuantity !== null;
  const price = info?.lastPrice ?? null;
  const listed = !markets || markets.instruments.some((x) => x.coin === coin);
  const problem = sizeProblem(settings, form.budget, info, params.leverage);
  const leverage = Number.isNaN(params.leverage) ? 1 : params.leverage;
  // The nominal the form will send, for the margin hint: rounded coins at today's price, or the USDT nominal.
  const nominal =
    price && info
      ? floorToStep(settings.orderQuantity ?? settings.orderNotional / price, info.quantityStep) *
        price
      : settings.orderQuantity === null
        ? settings.orderNotional
        : null;
  // Fees are always shown in percent; other thresholds follow the unit switch.
  function numberField(key: NumericSetting, alwaysPercent = false) {
    const meta = settingMeta.find((m) => m.key === key)!;
    const bps = meta.unit === 'bps';
    const percent = alwaysPercent || percentUnits;
    const value = settings[key];
    return (
      <label className="field" key={key}>
        <span>
          {meta.label}, {unitLabel(meta.unit, bps && percent)}
        </span>
        <input
          type="number"
          min={bps ? toDisplay(meta.min, percent) : meta.min}
          max={bps ? toDisplay(meta.max, percent) : meta.max}
          step={bps ? toDisplay(meta.step, percent) : meta.step}
          value={Number.isNaN(value) ? '' : bps ? toDisplay(value, percent) : value}
          onChange={(e) =>
            setSetting(
              key,
              bps ? fromDisplay(e.target.valueAsNumber, percent) : e.target.valueAsNumber,
            )
          }
          required
        />
        <small className="field-hint">{meta.hint}</small>
      </label>
    );
  }
  return (
    <>
      <div className="form-grid">
        <SelectField
          label="Биржа сигнала"
          value={params.signalExchange}
          onChange={(value) => setParams({ signalExchange: value as Exchange })}
        >
          {paperExchanges
            .filter((x) => x !== form.exchange)
            .map((x) => (
              <option key={x}>{x}</option>
            ))}
        </SelectField>
        <MarketPairPicker
          key={`${signal}:${exchange}`}
          instruments={markets?.instruments ?? []}
          coin={coin}
          sourceExchange={signal}
          disabled={false}
          loading={marketsLoading}
          onChange={(value) => setForm((f) => ({ ...f, symbol: `${value}USDT` }))}
        />
      </div>
      {marketsError && <p className="form-error">{marketsError}</p>}
      {!listed && (
        <p className="form-error">
          {coin} нет среди общих пар {form.exchange} и {params.signalExchange}. Выберите пару из
          списка.
        </p>
      )}
      <div className="form-grid">
        <SelectField
          label="Размер заявки задаётся"
          value={inCoins ? 'coins' : 'usdt'}
          onChange={(mode) => {
            if (mode === 'coins')
              setSetting(
                'orderQuantity',
                price
                  ? floorToStep(settings.orderNotional / price, info!.quantityStep) ||
                      info!.quantityStep
                  : 1,
              );
            else setSetting('orderQuantity', null);
          }}
        >
          <option value="usdt">В USDT (номинал)</option>
          <option value="coins">В монетах {coin || ''}</option>
        </SelectField>
        {inCoins ? (
          <label className="field">
            <span>Размер заявки, {coin || 'монет'}</span>
            <input
              type="number"
              min={info?.quantityStep ?? 0}
              step={info?.quantityStep ?? 'any'}
              value={Number.isNaN(settings.orderQuantity) ? '' : (settings.orderQuantity ?? '')}
              onChange={(e) => setSetting('orderQuantity', e.target.valueAsNumber)}
              required
            />
          </label>
        ) : (
          <label className="field">
            <span>Номинал заявки, USDT</span>
            <input
              type="number"
              min="1"
              max="100000"
              step="0.01"
              value={Number.isNaN(settings.orderNotional) ? '' : settings.orderNotional}
              onChange={(e) => setSetting('orderNotional', e.target.valueAsNumber)}
              required
            />
          </label>
        )}
        <label className="field">
          <span>Плечо, x</span>
          <input
            type="number"
            min="1"
            max={info?.maxLeverage ?? maxLeverage}
            step="1"
            value={Number.isNaN(params.leverage) ? '' : params.leverage}
            onChange={(e) => setParams({ leverage: e.target.valueAsNumber })}
            required
          />
          <small className="field-hint">
            {nominal !== null
              ? `Маржа ≈ ${n(nominal / leverage, 2)} USDT из ${n(form.budget, 2)} USDT баланса.`
              : 'Маржа = номинал заявки ÷ плечо.'}{' '}
            {info?.maxLeverage
              ? `${form.exchange} допускает до ${info.maxLeverage}x.`
              : `Предел биржи не опубликован в открытых данных; допускается до ${maxLeverage}x.`}{' '}
            При убытке, близком к марже, симуляция закроет позицию как риск ликвидации.
          </small>
        </label>
      </div>
      <p className="field-hint" role="status">
        {info
          ? `${form.exchange} ${coin}: шаг ${n(info.quantityStep)}, минимум ${n(info.minimumQuantity)} ${coin}` +
            (info.minimumNotional ? `, минимальный номинал ${n(info.minimumNotional)} USDT` : '') +
            (price
              ? `. Цена ≈ ${n(price)} USDT: ${inCoins ? `заявка ≈ ${n((settings.orderQuantity ?? 0) * price, 2)} USDT` : `≈ ${n(floorToStep(settings.orderNotional / price, info.quantityStep))} ${coin}`}; минимальная заявка ≈ ${n(info.minimumOrderNotional ?? 0, 4)} USDT.`
              : '. Текущая цена недоступна, размер проверит сервер при запуске.') +
            ' Размер округляется вниз до шага.'
          : infoState}
      </p>
      {problem && <p className="form-error">{problem}</p>}
      <div className="form-grid">
        <label className="field">
          <span>Задержка заявки, мс</span>
          <input
            type="number"
            min="1"
            max="1000"
            step="1"
            value={Number.isNaN(params.delayMs) ? '' : params.delayMs}
            onChange={(e) => setParams({ delayMs: e.target.valueAsNumber })}
            required
          />
          <small className="field-hint">
            Время от отправки заявки до исполнения на бирже — предположение, не измерение.
          </small>
        </label>
        <SelectField
          label="Вход в сделку"
          value={settings.entryOrder}
          onChange={(v) => setSetting('entryOrder', v)}
        >
          <option value="taker">Taker: по встречной цене</option>
          <option value="maker">Maker: заявка по своей цене</option>
        </SelectField>
        <SelectField
          label="Модель исполнения"
          value={settings.executionModel}
          onChange={(v) => setSetting('executionModel', v)}
        >
          <option value="depth">По стакану и объёму</option>
          <option value="bbo">Упрощённая: только лучшие цены</option>
        </SelectField>
        <label className="checkbox-field">
          <input
            type="checkbox"
            checked={params.counter}
            onChange={(e) => setParams({ counter: e.target.checked })}
          />
          Контрольный вариант: входить против импульса
        </label>
      </div>
      <div className="form-grid">
        {numberField('feeBps', true)}
        {numberField('makerFeeBps', true)}
      </div>
      <p className="field-hint">
        По умолчанию для {form.exchange}: {defaultFees[form.exchange].note}. Укажите свой
        фактический тариф.
      </p>
      <details className="strategy-details">
        <summary>Остальные параметры стратегии</summary>
        <div className="unit-switch" role="group" aria-label="Единицы для цен и порогов">
          <span>Единицы для порогов:</span>
          <button
            type="button"
            aria-pressed={percentUnits}
            className={percentUnits ? 'active' : ''}
            onClick={() => setPercent(true)}
          >
            %
          </button>
          <button
            type="button"
            aria-pressed={!percentUnits}
            className={!percentUnits ? 'active' : ''}
            onClick={() => setPercent(false)}
          >
            б.п.
          </button>
          <small>1 б.п. = 0,01%</small>
        </div>
        <div className="form-grid">
          {settingMeta
            .filter((m) => m.key !== 'feeBps' && m.key !== 'makerFeeBps')
            .map((m) => numberField(m.key))}
        </div>
      </details>
    </>
  );
}

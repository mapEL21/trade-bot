"""The "buy after a crash" hypothesis of docs/CANDLE_STUDY.md (fixed on 13.11.2026). Reads nothing after the stage's end.

python research/candles/crash.py validation
python research/candles/crash.py control      # once, and only if validation passed
"""
import sys
import numpy as np, pandas as pd
from study import COST, DAY, ROOT, STAGES, klines, trades_of, universe

BARS = 48  # 4 hours of 5-minute candles
NAME = 'Покупка после обвала|4'


def main():
    stage = sys.argv[1]
    if stage == 'research':
        sys.exit('Этап «Исследование» уже просмотрен: гипотеза проверяется на «Проверке» и «Контроле».')
    first, last = STAGES[stage]
    start = int(pd.Timestamp(first + '-01', tz='UTC').timestamp() * 1000)
    end = int((pd.Timestamp(last + '-01', tz='UTC') + pd.offsets.MonthBegin(1)).timestamp() * 1000)
    members = {m: s for m, s in universe().items() if first <= m <= last}
    by_symbol = {}
    for month, symbols in members.items():
        for symbol in symbols:
            by_symbol.setdefault(symbol, []).append(month)
    rows, forward = [], {}
    for symbol, months in sorted(by_symbol.items()):
        data = klines(symbol)
        if data is None:
            continue
        frame, rates = data
        frame = frame[frame.index < end]
        if len(frame) < 300:
            continue
        move = frame.close / frame.close.shift(DAY) - 1
        typical = move.abs().shift(1).rolling(8640, min_periods=8000).mean()
        direction = np.where(move <= -4 * typical, 1, 0)
        rows += trades_of(symbol, frame, rates, NAME, direction, BARS, months, start, end)
        opens = frame.open.ffill()
        forward[symbol] = (opens.shift(-BARS) / opens - 1) * 100
    trades = pd.DataFrame(rows, columns=['name', 'symbol', 't', 'side', 'gross', 'funding'])
    month = pd.to_datetime(trades.t, unit='ms', utc=True).dt.strftime('%Y-%m')
    # What buying every coin of that month at the same moment for the same four hours made, before costs.
    market = []
    for t, m in zip(trades.t, month):
        values = [forward[s].get(t) for s in members[m] if s in forward]
        values = [v for v in values if v is not None and not np.isnan(v)]
        market.append(np.mean(values) if values else np.nan)
    trades['market'] = market
    trades.to_pickle(ROOT / f'crash-{stage}.pkl')
    net = trades.gross + trades.funding - COST
    excess = trades.gross - trades.market
    days = range(start // 86_400_000, end // 86_400_000)
    by_day = net.groupby(trades.t // 86_400_000).sum().reindex(days, fill_value=0)
    t_stat = by_day.mean() / by_day.std() * np.sqrt(len(by_day)) if by_day.std() > 0 else 0
    monthly = net.groupby(month).agg(['count', 'mean'])
    print(f'этап {stage}: сделок {len(trades)}, монет {trades.symbol.nunique()}, дней со сделками {(by_day != 0).sum()}')
    print(f'среднее после расходов {net.mean():.3f}%, до расходов {trades.gross.mean():.3f}%, фондирование {trades.funding.mean():.3f}%')
    print(f'рынок в те же моменты {trades.market.mean():.3f}%, сверх рынка {excess.mean():.3f}%')
    print(f'доля прибыльных {(net > 0).mean() * 100:.1f}%, t дневного результата {t_stat:.2f}')
    print(f'худшая {net.min():.1f}%, 1% худших {net.quantile(.01):.1f}%, медиана {net.median():.3f}%')
    print('по месяцам:', {m: (int(c), round(v, 2)) for m, (c, v) in monthly.iterrows()})
    passed = len(trades) >= 100 and net.mean() > 0 and excess.mean() > 0 and (t_stat >= 2 or stage == 'control')
    print('ИТОГ:', 'пройдено' if passed else 'не пройдено')


if __name__ == '__main__':
    main()

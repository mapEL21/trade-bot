"""The study of docs/CANDLE_STUDY.md. A stage reads no data after its end, so the later parts stay unseen.

python research/candles/study.py research
python research/candles/study.py validation "Импульс по движению|4"
"""
import concurrent.futures as cf, glob, io, json, pathlib, pickle, sys, zipfile
from datetime import datetime, timezone
import numpy as np, pandas as pd

ROOT = pathlib.Path(__file__).resolve().parents[2] / 'data' / 'candles'
PARSED = ROOT / 'parsed'
STAGES = {'research': ('2024-01', '2025-12'), 'validation': ('2026-01', '2026-06'), 'control': ('2026-07', '2026-09')}
COST, CHEAP = .14, .11
HOURS = {1: 12, 4: 48, 24: 288}
MINUTES_5 = 300_000
DAY = 288
NAMES = ['Импульс по движению', 'Импульс против', 'Пробой недели', 'Всплеск объёма', 'Перерастянутость против',
         'Пробой дня', 'Каскад по движению', 'Каскад против']
RANDOM = 'Случайное направление'
MOMENTUM = 'Недельный импульс среди монет'


def read_csv(path):
    with zipfile.ZipFile(path) as z, z.open(z.namelist()[0]) as f:
        return pd.read_csv(io.BytesIO(f.read()), header=None, dtype=str)


def klines(symbol):
    cached = PARSED / f'{symbol}.pkl'
    if cached.exists():
        return pickle.loads(cached.read_bytes())
    frames = []
    for path in sorted(glob.glob(str(ROOT / '5m' / symbol / '*.zip'))):
        raw = read_csv(path)
        raw = raw[raw[0].str.isdigit()]
        frames.append(pd.DataFrame({'t': raw[0].astype('int64'), 'open': raw[1].astype(float), 'high': raw[2].astype(float),
                                    'low': raw[3].astype(float), 'close': raw[4].astype(float), 'quote': raw[7].astype(float), 'taker': raw[10].astype(float)}))
    if not frames:
        return None
    frame = pd.concat(frames).drop_duplicates('t').sort_values('t')
    frame.loc[frame.t > 10 ** 14, 't'] //= 1000
    frame = frame.set_index('t')
    grid = np.arange(frame.index[0], frame.index[-1] + 1, MINUTES_5)
    frame = frame.reindex(grid)
    funding = []
    for path in sorted(glob.glob(str(ROOT / 'fundingRate' / symbol / '*.zip'))):
        raw = read_csv(path)
        raw = raw[raw[0].str.isdigit()]
        funding.append(pd.DataFrame({'t': raw[0].astype('int64'), 'rate': raw[2].astype(float)}))
    rates = pd.concat(funding).drop_duplicates('t').sort_values('t') if funding else pd.DataFrame({'t': [], 'rate': []})
    PARSED.mkdir(exist_ok=True)
    cached.write_bytes(pickle.dumps((frame, rates)))
    return frame, rates


def month_of(ms):
    return datetime.fromtimestamp(ms / 1000, timezone.utc).strftime('%Y-%m')


def signals(f):
    """Direction by bar (0 = none), known only at the close of that bar."""
    r = f.close / f.open - 1
    size = r.abs().shift(1).rolling(DAY, min_periods=200).mean()
    impulse = np.where(r.abs() >= 6 * size, np.sign(r), 0)
    high = f.high.shift(1).rolling(2016, min_periods=1500).max()
    low = f.low.shift(1).rolling(2016, min_periods=1500).min()
    breakout = np.where(f.close > high, 1, np.where(f.close < low, -1, 0))
    median = f.quote.shift(1).rolling(DAY, min_periods=200).median()
    volume = np.where(f.quote >= 10 * median, np.sign(r), 0)
    move = f.close / f.close.shift(DAY) - 1
    typical = move.abs().shift(1).rolling(8640, min_periods=8000).mean()
    stretched = np.where(move.abs() >= 4 * typical, -np.sign(move), 0)
    day = pd.Series(f.index // 86_400_000, index=f.index)
    day_high, day_low = f.high.groupby(day).max().shift(1), f.low.groupby(day).min().shift(1)
    above, below = day.map(day_high), day.map(day_low)
    daily = np.where((f.close > above) & (f.close.shift(1) <= above), 1, np.where((f.close < below) & (f.close.shift(1) >= below), -1, 0))
    buyers = f.taker / f.quote
    one_sided = np.where(r > 0, buyers, 1 - buyers) >= .7
    cascade = np.where((impulse != 0) & (volume != 0) & one_sided, np.sign(r), 0)
    return {NAMES[0]: impulse, NAMES[1]: -impulse, NAMES[2]: breakout, NAMES[3]: volume, NAMES[4]: stretched,
            NAMES[5]: daily, NAMES[6]: cascade, NAMES[7]: -cascade}


def trades_of(symbol, f, rates, name, direction, bars, months, start, end):
    """One open trade at a time on a coin. A position that outlives the coin's data exits at the last known price."""
    times, opens = f.index.values, f.open.ffill().values
    missing = f.open.isna().values
    entries = np.flatnonzero(np.nan_to_num(direction) != 0)
    allowed = set(months)
    rate_t, rate_v = rates.t.values, rates.rate.values
    out, busy = [], -1
    for i in entries:
        k = i + 1
        if k <= busy or k >= len(times) or missing[k]:
            continue
        t = times[k]
        if t >= end or t < start or month_of(t) not in allowed:
            continue
        x = k + bars
        if x >= len(times):
            if times[-1] >= end - MINUTES_5:
                continue  # the exit is after the end of this stage
            x = len(times) - 1
        if times[x] >= end:
            continue
        busy = x
        side = direction[i]
        funding = rate_v[(rate_t > t) & (rate_t <= times[x])].sum() * 100
        gross = side * (opens[x] / opens[k] - 1) * 100
        out.append((name, symbol, int(t), side, gross, -side * funding))
    return out


def universe():
    return json.loads((ROOT / 'universe.json').read_text())


def work(job):
    symbol, months, start, end, seed = job
    data = klines(symbol)
    if data is None:
        return [], None
    frame, rates = data
    frame = frame[frame.index < end]
    if len(frame) < 300:
        return [], None
    found = []
    table = signals(frame)
    for name, direction in table.items():
        for hours, bars in HOURS.items():
            found += trades_of(symbol, frame, rates, f'{name}|{hours}', direction, bars, months, start, end)
    # The harness control: the impulse events with a coin-flip side must lose about the costs.
    rng = np.random.default_rng(seed)
    flips = np.where(table[NAMES[0]] != 0, rng.choice([-1, 1], len(frame)), 0)
    found += trades_of(symbol, frame, rates, f'{RANDOM}|4', flips, HOURS[4], months, start, end)
    # What the daily cross-section needs: the 7-day return known before each 00:00 UTC and that day's open-to-open.
    days = frame[(frame.index % 86_400_000) == 0]
    past = frame.close.shift(1).reindex(days.index) / frame.close.shift(2017).reindex(days.index) - 1
    table = pd.DataFrame({'open': days.open, 'past': past})
    return found, (symbol, table)


def momentum(tables, members, start, end):
    """Each day: long the 10 best, short the 10 worst of that month's coins by the last 7 days; held to the next 00:00 UTC."""
    if not tables:
        return []
    out = []
    frame = {s: t for s, t in tables}
    days = sorted({d for t in frame.values() for d in t.index if start <= d < end - 86_400_000})
    for day in days:
        coins = [(s, frame[s].past.get(day)) for s in members.get(month_of(day), []) if s in frame]
        coins = [(s, p) for s, p in coins if p is not None and not np.isnan(p) and not np.isnan(frame[s].open.get(day, np.nan))]
        if len(coins) < 20:
            continue
        coins.sort(key=lambda c: c[1])
        for side, chosen in ((-1, coins[:10]), (1, coins[-10:])):
            for s, _ in chosen:
                now, then = frame[s].open.get(day), frame[s].open.get(day + 86_400_000, np.nan)
                if np.isnan(then):
                    known = frame[s].open.loc[:day + 86_400_000].dropna()
                    then = known.iloc[-1]
                out.append((f'{MOMENTUM}|24', s, int(day), side, side * (then / now - 1) * 100, 0.0))
    return out


def attach_funding(rows):
    """The cross-section's funding: added at the end, per coin, over the held day."""
    out = []
    for name, symbol, t, side, gross, funding in rows:
        data = klines(symbol)
        rates = data[1]
        paid = rates.rate.values[(rates.t.values > t) & (rates.t.values <= t + 86_400_000)].sum() * 100
        out.append((name, symbol, t, side, gross, -side * paid))
    return out


def report(frame, cost, start, end):
    days = (end - start) / 86_400_000
    lines = []
    for name, g in frame.groupby('name', sort=False):
        net = g.gross + g.funding - cost
        by_day = (net.groupby(g.t // 86_400_000).sum()).reindex(range(start // 86_400_000, end // 86_400_000), fill_value=0)
        t_stat = by_day.mean() / by_day.std() * np.sqrt(len(by_day)) if by_day.std() > 0 else 0
        years = net.groupby(pd.to_datetime(g.t, unit='ms', utc=True).dt.year).mean()
        lines.append(dict(name=name, trades=len(g), mean=net.mean(), win=(net > 0).mean() * 100, t=t_stat, gross=g.gross.mean(),
                          funding=g.funding.mean(), years={int(y): round(v, 3) for y, v in years.items()}))
    return lines


def main():
    stage = sys.argv[1]
    chosen = set(sys.argv[2:])
    if stage != 'research' and not chosen:
        sys.exit('Укажите варианты, прошедшие прошлый этап.')
    first, last = STAGES[stage]
    start = int(pd.Timestamp(first + '-01', tz='UTC').timestamp() * 1000)
    end = int((pd.Timestamp(last + '-01', tz='UTC') + pd.offsets.MonthBegin(1)).timestamp() * 1000)
    members = universe()
    in_stage = {m: s for m, s in members.items() if first <= m <= last}
    by_symbol = {}
    for month, symbols in in_stage.items():
        for symbol in symbols:
            by_symbol.setdefault(symbol, []).append(month)
    jobs = [(s, m, start, end, i) for i, (s, m) in enumerate(sorted(by_symbol.items()))]
    print('монет', len(jobs), 'месяцев', len(in_stage), flush=True)
    rows, tables = [], []
    with cf.ProcessPoolExecutor(10) as pool:
        for found, table in pool.map(work, jobs, chunksize=4):
            rows += found
            if table is not None:
                tables.append(table)
    rows += attach_funding(momentum(tables, in_stage, start, end))
    frame = pd.DataFrame(rows, columns=['name', 'symbol', 't', 'side', 'gross', 'funding'])
    if chosen:
        frame = frame[frame.name.isin(chosen | {f'{RANDOM}|4'})]
    frame.to_pickle(ROOT / f'trades-{stage}.pkl')
    for cost, label in ((COST, f'основные расходы {COST}%'), (CHEAP, f'дешёвые расходы {CHEAP}%')):
        print(f'\n== {stage}: {label}')
        print(f'{"вариант":38}{"сделок":>8}{"среднее%":>10}{"плюс%":>7}{"t":>7}{"до расх%":>10}{"фонд%":>8}  по годам')
        for x in report(frame, cost, start, end):
            print(f'{x["name"]:38}{x["trades"]:8}{x["mean"]:10.3f}{x["win"]:7.1f}{x["t"]:7.2f}{x["gross"]:10.3f}{x["funding"]:8.3f}  {x["years"]}')


if __name__ == '__main__':
    main()

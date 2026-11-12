"""docs/TICK_STUDY.md: does the price step decide whether a break of the book reverts on our horizon?

python research/tick_study.py
"""
import glob, json, pathlib, statistics, urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1] / 'server' / 'TradeBot.Api' / 'data' / 'scans'
TYPES = ['Вынос цены', 'Проедание плотности', 'Пробой уровня']
HORIZONS = [5, 15, 60]
FEE = .1
GROUPS = [('крупный', 2, 1e9), ('средний', .5, 2), ('мелкий', 0, .5)]


def ticks():
    with urllib.request.urlopen('https://fapi.binance.com/fapi/v1/exchangeInfo', timeout=30) as r:
        info = json.loads(r.read())
    return {s['symbol']: float(next(f['tickSize'] for f in s['filters'] if f['filterType'] == 'PRICE_FILTER')) for s in info['symbols']}


def episodes():
    events = []
    for path in glob.glob(str(ROOT / '*' / 'signal-lab-v1.json')):
        data = json.loads(open(path, encoding='utf-8').read())
        if data.get('version') == 5:
            events += [e for e in data['events'] if e['type'] in TYPES]
    events.sort(key=lambda e: e['atMs'])
    last, out = {}, []
    for e in events:
        key = (e['symbol'], e['type'])
        if e['atMs'] - last.get(key, -10 ** 18) >= 60_000:
            out.append(e)
        last[key] = e['atMs']
    return out


def result(e, k, follow):
    """Percent before fees, or None when a price is unknown (stale book, recording ended)."""
    bid, ask = e.get('entryBid'), e.get('entryAsk')
    xb, xa = (e.get('exitBid') or [None] * 4)[k], (e.get('exitAsk') or [None] * 4)[k]
    if not bid or not ask or not xb or not xa:
        return None
    side = e['direction'] if follow else -e['direction']
    return (xb / ask - 1) * 100 if side > 0 else (1 - xa / bid) * 100


def main():
    tick, rows = ticks(), episodes()
    unknown = sum(e['symbol'] not in tick for e in rows)
    print(f'эпизодов {len(rows)}, без шага цены {unknown}')
    print(f'{"событие":22}{"шаг":9}{"сторона":12}{"срок":>5}{"эпиз.":>7}{"до ком.%":>10}{"после%":>9}{"1-я пол.":>10}{"2-я пол.":>10}  кандидат')
    for kind in TYPES:
        for name, low, high in GROUPS:
            group = [e for e in rows if e['type'] == kind and e['symbol'] in tick and low <= tick[e['symbol']] / e['price'] * 1e4 < high]
            for follow in (True, False):
                for k, horizon in enumerate(HORIZONS):
                    values = [v for v in (result(e, k, follow) for e in group) if v is not None]
                    if not values:
                        continue
                    net = [v - FEE for v in values]
                    half = len(net) // 2
                    first, second = (statistics.mean(net[:half]), statistics.mean(net[half:])) if half else (0, 0)
                    ok = len(net) >= 100 and statistics.mean(net) > 0 and first > 0 and second > 0
                    print(f'{kind:22}{name:9}{"по движению" if follow else "против":12}{horizon:5}{len(net):7}{statistics.mean(values):10.3f}'
                          f'{statistics.mean(net):9.3f}{first:10.3f}{second:10.3f}  {"ДА" if ok else ""}')


if __name__ == '__main__':
    main()

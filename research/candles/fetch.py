"""Downloads Binance USDT-M history for docs/CANDLE_STUDY.md into data/candles (not in git)."""
import http.client
import concurrent.futures as cf, csv, io, json, pathlib, re, sys, time, urllib.error, urllib.request, zipfile

ROOT = pathlib.Path(__file__).resolve().parents[2] / 'data' / 'candles'
BASE = 'https://data.binance.vision/data/futures/um/monthly'
LIST = 'https://s3-ap-northeast-1.amazonaws.com/data.binance.vision?delimiter=/&prefix=data/futures/um/monthly/klines/'
START, END = (2023, 12), (2026, 9)
TOP = 60
FAILED = []
# Not crypto risk: stablecoin pairs have no signal to test.
SKIP = re.compile(r'^(USDC|FDUSD|BUSD|TUSD|USDP|DAI|EUR|GBP)USDT$')


def months(start=START, end=END):
    y, m = start
    while (y, m) <= end:
        yield f'{y}-{m:02d}'
        y, m = (y + 1, 1) if m == 12 else (y, m + 1)


def get(url):
    for attempt in range(7):
        try:
            with urllib.request.urlopen(url, timeout=60) as r:
                return r.read()
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            time.sleep(min(30, 2 ** attempt))
        except (OSError, http.client.HTTPException):  # a dropped connection, a response cut short
            time.sleep(min(30, 2 ** attempt))
    raise RuntimeError(f'failed: {url}')


def fallback_symbols():
    """The archive's listing stopped answering on 12.11.2026 (NoSuchBucket). What is known instead: the coins already
    listed before that (their folders exist, 0-9 to REZ, delisted ones included) and today's futures and spot coins from
    public lists. A name that is not a futures coin simply has no files. Coins delisted before today whose names sort after
    REZ are missing from this list."""
    names = {p.name for p in (ROOT / '1d').iterdir()} if (ROOT / '1d').exists() else set()
    for url, pick in (
        ('https://api.coingecko.com/api/v3/derivatives/exchanges/binance_futures?include_tickers=unexpired', lambda d: [t['symbol'] for t in d['tickers']]),
        ('https://data-api.binance.vision/api/v3/exchangeInfo', lambda d: [x['symbol'] for x in d['symbols']]),
    ):
        data = get(url)
        if data:
            names |= set(pick(json.loads(data)))
    return sorted(s for s in names if re.fullmatch(r'[A-Z0-9]+USDT', s) and not SKIP.match(s))


def symbols():
    if get(LIST) is None:
        return fallback_symbols()
    found, marker = [], ''
    while True:
        text = get(LIST + (f'&marker={marker}' if marker else '')).decode()
        found += re.findall(r'<Prefix>data/futures/um/monthly/klines/([^/<]+)/</Prefix>', text)
        next_marker = re.search(r'<NextMarker>([^<]+)</NextMarker>', text)
        if not next_marker:
            # Latin names only: the archive also lists a few coins with other characters, which this downloader cannot ask for.
            return sorted({s for s in found if re.fullmatch(r'[A-Z0-9]+USDT', s) and not SKIP.match(s)})
        marker = next_marker.group(1)


def fetch(kind, symbol, month):
    """Stores the raw zip once; a missing month is remembered so it is not asked again."""
    interval = '' if kind == 'fundingRate' else f'/{kind}'
    name = f'{symbol}-fundingRate-{month}.zip' if kind == 'fundingRate' else f'{symbol}-{kind}-{month}.zip'
    target = ROOT / kind / symbol / name
    marker = target.with_suffix('.none')
    if target.exists() or marker.exists():
        return
    target.parent.mkdir(parents=True, exist_ok=True)
    folder = 'fundingRate' if kind == 'fundingRate' else 'klines'
    # A file the archive would not give is left for the next run instead of stopping the whole download.
    try:
        data = get(f'{BASE}/{folder}/{symbol}{interval}/{name}')
    except RuntimeError as e:
        FAILED.append(str(e))
        return
    if data is None:
        marker.write_bytes(b'')
    else:
        target.write_bytes(data)


def month_volume(symbol, month):
    path = ROOT / '1d' / symbol / f'{symbol}-1d-{month}.zip'
    if not path.exists():
        return 0.0
    with zipfile.ZipFile(path) as z, z.open(z.namelist()[0]) as f:
        rows = [r for r in csv.reader(io.TextIOWrapper(f)) if r and r[0].isdigit()]
    return sum(float(r[7]) for r in rows)


def run(tasks):
    with cf.ThreadPoolExecutor(16) as pool:
        list(pool.map(lambda t: fetch(*t), tasks))


def main():
    names = symbols()
    print('symbols', len(names), flush=True)
    all_months = list(months())
    run([('1d', s, m) for s in names for m in all_months])
    print('daily done', flush=True)
    universe, needed = {}, set()
    for previous, month in zip(all_months, all_months[1:]):
        ranked = sorted(names, key=lambda s: -month_volume(s, previous))[:TOP]
        universe[month] = ranked
        # The month before gives the signals their history.
        needed |= {(s, previous) for s in ranked} | {(s, month) for s in ranked}
    (ROOT / 'universe.json').write_text(json.dumps(universe))
    print('universe symbols', len({s for r in universe.values() for s in r}), 'files', len(needed), flush=True)
    run([('5m', s, m) for s, m in sorted(needed)])
    print('5m done', flush=True)
    run([('fundingRate', s, m) for s, m in sorted(needed)])
    print('funding done', flush=True)
    if FAILED:
        print(f'не скачано файлов: {len(FAILED)} — запустите ещё раз', flush=True)
        for line in FAILED[:5]:
            print(line, flush=True)
        return 1


if __name__ == '__main__':
    sys.exit(main())

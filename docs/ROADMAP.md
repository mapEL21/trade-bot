# First release roadmap

## 0. Repository foundation

- [x] Initial scope and proposed stack documented.
- [x] Architecture draft and initial risk requirements documented.
- [x] Ignore rules for secrets, build outputs, and runtime data prepared.
- [x] Connect local checkout to the user-created GitHub repository `mapEL21/trade-bot`.
- [x] Prepare and review the repository foundation documents for the initial documentation commit.

## 1. User interface prototype

- [x] Bot list with status, instrument, allocated demo balance, and historical PnL.
- [x] Bot detail view with overview, trades, settings, and journal.
- [x] Versioned configuration import/export with validation.
- [x] Clearly labeled demonstration data; live and simulation modes unavailable.
- [x] Distinct pause and stop-and-close controls with demo-only status transitions.
- [x] Local persistence, mobile layout and browser interaction tests.

Position monitoring and actual command execution require the future engine.

Acceptance: a user can create a demo bot, open its statistics, edit settings, and export/import its configuration without credentials.

## 2. Market recording and selection

### Local application server (implemented before market recording)

- [x] ASP.NET Core API with a strategy/version catalog.
- [x] Persistent instances and journal in local SQLite.
- [x] Server validation and revision checks to prevent lost updates.
- [x] UI distinguishes algorithm metadata from configurable instances.
- [x] Demo browser data remains separate from server data; planned algorithms cannot start.

Trading execution and account connections remain pending. A separate public-feed recorder is implemented; see [recording guide](MARKET_RECORDER.md).

- [x] Select Hyperliquid as signal source and X Binance via TigerX as intended execution venue; record displayed fee assumptions in the strategy specification.
- [ ] Confirm custom-bot access to TigerX API, charged fees and cashback eligibility for this execution route.
- [ ] Filter altcoin perpetuals by order constraints and liquidity; exclude BTC/ETH from initial selection.
- [x] Implement bounded raw recording of Hyperliquid and Binance public events with local receipt timestamps and reconnect markers; verify a short live capture.
- [x] Add server-owned recording controls and live BBO monitoring to the research page, with explicit stale/disconnected states and raw price comparison.
- [ ] Collect longer research sessions across market regimes; implement rotation and disk monitoring before unattended collection.
- [ ] Reconstruct books and detect missing updates.
- [ ] Compare spread, depth, estimated execution cost, and DEX → CEX lead–lag opportunities using local receipt timestamps.

Acceptance: recordings support reproducible replay, gaps are flagged, and a candidate fits the intended test balance without relying on excessive leverage.

## 3. First strategy and simulation

- [x] Draft DEX → CEX research rules: DEX signal, CEX-only futures execution; see [strategy specification](STRATEGY_DEX_LEAD_LAG.md).
- [ ] Validate lead–lag for the selected Hyperliquid → X Binance setup and select one asset and concrete entry/exit/risk parameters from recordings. The existing planned impulse catalog entry is not an implementation of this hypothesis.
- [ ] Model latency, fees, partial fills, and conservative limit-order execution.
- [ ] Evaluate on a separate period not used for parameter selection.
- [ ] Show persistent per-bot statistics from simulation.

Acceptance: results are reproducible, costs are explicit, and simulated fills are never presented as real trades. Profitability remains an empirical question.

## 4. Execution readiness

- [ ] Implement and test order lifecycle and account reconciliation.
- [ ] Test disconnects, lost responses, duplicate events, and cancel/fill races.
- [ ] Verify bot/account limits and stale-data handling.
- [ ] Verify reporting of real fills, fees, funding, and transfers.
- [ ] Obtain explicit authorization for a minimal-size live test.

Acceptance: no duplicate orders after ambiguous responses; restart recovers actual state; controls behave correctly under failure scenarios.

## Deferred

Multiple exchange trading, arbitrary code plugins, simultaneous bots on the same account/instrument, multi-user hosting, and infrastructure optimization beyond measured needs.

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

Trading execution and exchange connections remain pending.

- [ ] Confirm broker API access and actual maker/taker fees and cashback.
- [ ] Filter altcoin perpetuals by order constraints and liquidity; exclude BTC/ETH from initial selection.
- [ ] Record market events and local receipt timestamps.
- [ ] Reconstruct books and detect missing updates.
- [ ] Compare spread, depth, estimated execution cost, and impulse opportunities.

Acceptance: recordings support reproducible replay, gaps are flagged, and a candidate fits the intended test balance without relying on excessive leverage.

## 3. First strategy and simulation

- [ ] Define entry, invalidation, exit, time limit, and position sizing for an impulse strategy.
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

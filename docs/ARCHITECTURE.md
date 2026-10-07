# Architecture draft

Status: proposed; implementation has not started.

## Components

1. Web UI: bot list, bot details, configurations, connections, and statistics.
2. Application API: validates UI commands and persists configuration versions.
3. Trading engine: market state, strategy decisions, risk checks, order lifecycle, and position reconciliation.
4. Exchange adapters: normalize instrument metadata, market events, order commands, and execution reports.
5. Persistence: durable orders, fills, command history, and bot attribution; bulk market recordings stored separately.

Browser disconnection must not stop the engine. UI and disk operations must not block the trading event loop. Server-side controls must validate every trading command regardless of its origin.

## Strategies and instances

A strategy defines an algorithm and its parameter schema. A bot instance binds a strategy version to an account, instrument, parameters, risk limits, and execution mode.

Initial imports contain a schema version, strategy identifier/version, and validated parameters. Credentials are excluded. Arbitrary executable plugin loading is deferred; future custom code requires isolation and a restricted order interface.

## Execution modes and controls

- Demo: explicitly synthetic data for interface development.
- Simulation: real market data with modeled execution, clearly distinguished from actual fills.
- Live: real orders; disabled by default and requires explicit activation.
- Pause: prohibit new entries, continue managing existing risk.
- Stop-and-close: cancel entry orders and request closure; do not report success until account state is reconciled.

Initial live scope permits only one bot per account/instrument. External or manual trading on the same instrument must be detected and handled explicitly, rather than silently attributed to the bot.

## Correctness and risk requirements

- Validate quantity steps, price ticks, minimum notional, and margin before orders.
- Detect stale or inconsistent market data and suspend new entries.
- Handle partial fills, duplicates, out-of-order messages, and fills racing with cancellation.
- Treat request timeout as unknown outcome; reconcile before retrying to avoid duplicate orders.
- Reconcile orders and positions on startup and reconnect.
- Enforce bot and account exposure limits, loss limits, and pending-order exposure.
- Separate realized PnL, unrealized PnL, fees, funding, and expected/received cashback.
- Separate deposits/withdrawals from trading performance.
- Keep credentials server-side and redact sensitive logs.
- Deploy locally first; remote access requires authentication and encrypted transport.

## Open decisions

- Exchange and exact broker connection; TigerX access for a custom client is unconfirmed.
- First altcoin perpetual contract, selected through market recordings and account constraints.
- Strategy entry/exit rules and acceptable risk parameters.
- Hosting region based on measured latency and operational cost.
- Runtime versions, connector choice, and deployment packaging.

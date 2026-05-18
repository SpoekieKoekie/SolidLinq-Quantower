# Quantower integration — discovery report

Structured answers to **Prompt/Quantower.md** § “Discovery task”, using **repository evidence only** (SolidLinq-Core + SolidLinq-Dashboard). Canonical narrative and constraints remain in **[`SolidLinq-Dashboard/Prompt/Quantower.md`](../../SolidLinq-Dashboard/Prompt/Quantower.md)**.

## 1. Sample SolidLinq inbound signal payload

**Type:** `InboundSignal` in [`SolidLinq-Core/src/types.ts`](../../SolidLinq-Core/src/types.ts) (`ticker`, `strategyId`, `alertId`, `action`, `lots`, `slPercent` / `tpPercent`, `slPct` / `tpPct`, `orderType`, `leg`, `symbol`, `side`, `pid`, …).

**Ingress:** TradingView-style JSON `POST` to `https://{core}/signal/{linkId}/{webhookToken}` — parsed by `parseAndValidateSignal` in the worker ([`SolidLinq-Core/src/index.ts`](../../SolidLinq-Core/src/index.ts)), forwarded to the link hub as `{ signal, _token }` on `/signal-ingest` ([`SolidLinq-Core/src/do/LinkStateHub.ts`](../../SolidLinq-Core/src/do/LinkStateHub.ts)).

**Normalized command:** `buildNormalizedCommand` in [`SolidLinq-Core/src/handlers/dispatch.ts`](../../SolidLinq-Core/src/handlers/dispatch.ts) → `NormalizedCommand` (targets from `linkedAccounts` + live `accounts`).

**Tests / fixtures:** e.g. [`SolidLinq-Core/tests/dispatch.test.ts`](../../SolidLinq-Core/tests/dispatch.test.ts), [`SolidLinq-Core/tests/ingest.test.ts`](../../SolidLinq-Core/tests/ingest.test.ts).

## 2. Sample execution callback expected by SolidLinq

**Primary:** `POST https://{core}/ack` with JSON body:

- `ack_type` or `type`: `execution_ack`
- `id`: must match **`NormalizedCommand.id`** (dispatch correlation, UUID per ingest when not overridden)
- `ok`: boolean
- Optional: `brokerMs`, `execMs`, `rxMs`, `totalMs`, `pipelineMs`, `status`, `accountId`, `bridgeInstanceId`, `platform`, plus error fields via `ackErrorFieldsFromBody` ([`SolidLinq-Core/src/index.ts`](../../SolidLinq-Core/src/index.ts) `handleHttpAck`).

Handled in Core Worker; updates D1 event row via `updateEventBrokerMs`.

## 3. Sample closed P&L callback payload

**Paths:**

1. **`POST /ack`** with `ack_type` / `type`: `position_closed` or `positionclosed` — `pid`, `pnl`, optional `symbol`, `side`, `currency`, `closedAt`, `accountId` ([`SolidLinq-Core/src/index.ts`](../../SolidLinq-Core/src/index.ts)).
2. **`POST /cbot/execution?instanceId={bridgeInstanceId}`** — JSON object or `{ batch: [...] }`; PID / P&L extracted by [`extractCbotClosePid`](../../SolidLinq-Core/src/handlers/cbotExecutionExtract.ts) / [`extractCbotClosePnl`](../../SolidLinq-Core/src/handlers/cbotExecutionExtract.ts) (`pid`, `pnl`, `netProfit`, `realizedPnl`, `ticket`, …). Bearer: `WORKER_API_TOKEN` or bridge-scoped token ([`SolidLinq-Core/src/handlers/cbotExecution.ts`](../../SolidLinq-Core/src/handlers/cbotExecution.ts)).

**Domain type:** `PnLRecord` in [`SolidLinq-Core/src/types.ts`](../../SolidLinq-Core/src/types.ts).

## 4. Repo path containing the Quantower algo / extension project

**Before this work:** none under `New-SolidLinq-Flow`.

**After scaffold:** [`SolidLinq-Quantower/src/SolidLinq.Quantower/`](../src/SolidLinq.Quantower/) — .NET 8 console host; **`SolidLinq-Quantower/src/SolidLinq.Quantower.Bridge/`** — shared WS/HTTP bridge library; **`SolidLinq-Quantower/src/SolidLinq.Quantower.Algo/`** — Quantower-hosted `SolidLinqBridgeStrategy` (build with `QUANTOWER_ALGO_SDK`). Wire **TradingPlatform.BusinessLayer** via `QUANTOWER_ALGO_SDK` when building the algo project (see Quantower README).

## 5. Broker / account / symbol mapping rules

- **Account / bridge instance:** `linkedAccounts[]` entries: `platform` + `accountId` (for WS bridges, `accountId` is typically the **bridge instance UUID**). Dashboard + `GET /api/admin/bridge/generate` create pending KV + WS URL ([`SolidLinq-Core/src/handlers/adminApi.ts`](../../SolidLinq-Core/src/handlers/adminApi.ts)).
- **Dispatch targeting:** `partitionCommandTargets` ([`SolidLinq-Core/src/util/normalizeDispatchPlatform.ts`](../../SolidLinq-Core/src/util/normalizeDispatchPlatform.ts)) routes platforms to **WS broadcast** vs **API execution**; bridge clients must attach hello with **matching `platform`** and **`bridgeInstanceId` / `accountId`** so `wsShouldReceive` includes them ([`SolidLinq-Core/src/do/LinkStateHub.ts`](../../SolidLinq-Core/src/do/LinkStateHub.ts)).
- **Symbol:** `NormalizedCommand.symbol` (from inbound `symbol` / `ticker`). Quantower-side mapping (e.g. TV `EURUSD` → provider symbol) is **executor / config** responsibility unless extended in `strategyCfg` / copier `symbolMap` (see [`CopierFollower.symbolMap`](../../SolidLinq-Dashboard/lib/types.ts) for the fan-out pattern).

## Confirmed vs inferred

| Item | Status |
|------|--------|
| WebSocket order wire JSON | **Confirmed** — `cbotOrderWire` in `LinkStateHub` (`type`, `id`, `symbol`, `side`, `orderType`, `lots`, …) |
| Hello handshake | **Confirmed** — `type: hello`, `bridgeInstanceId`, `authToken`, `platform`, optional `protocolVersion` |
| Execution ack shape | **Confirmed** — `POST /ack` + `execution_ack` |
| Quantower BusinessLayer order DTOs | **External** — not in repo; implement behind `IHubOrderExecutor` in this scaffold |

## User confirmation only if…

- You need a **new** outbound schema beyond `/ack` and `/cbot/execution`, or
- You require **Quantower-specific** fields on `linkedAccounts` / Core KV (not present today).

Otherwise, complete the integration by implementing **`IHubOrderExecutor`** and symbol/account resolution per **`Prompt/Quantower.md`**.

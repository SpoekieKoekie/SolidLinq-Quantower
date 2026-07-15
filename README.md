---
second-brain-id:
  - 74861748-64f7-413f-b834-8c803260f499
  - 6d25c5aa-16fb-42cc-a637-367bf844c037
  - 7cf8254b-a268-47dd-9bae-a5edbb943e52
second-brain-synced: July 14, 2026 - 8:54 PM GMT+2
---
# SolidLinq-Quantower

C# host and scaffold for a **Quantower** terminal integration with **SolidLinq-Core**: receive normalized orders over the same **WebSocket bridge** protocol as MT5 / cTrader / NinjaTrader, optionally execute via Quantower’s **TradingPlatform.BusinessLayer**, and report back using Core’s existing HTTP endpoints.

## Canonical specification (read first)

All design constraints, layering, and non-goals are defined here — **do not drift from this document**:

- **[`../SolidLinq-Dashboard/Prompt/Quantower.md`](../SolidLinq-Dashboard/Prompt/Quantower.md)**

SolidLinq-Dashboard also ships a Cursor rule that points agents to that file:

- [`SolidLinq-Dashboard/.cursor/rules/quantower-solidlinq.mdc`](../SolidLinq-Dashboard/.cursor/rules/quantower-solidlinq.mdc)

## Discovery (repo evidence → contracts)

See **[`docs/discovery.md`](./docs/discovery.md)** for the five-way mapping (inbound signal, execution ack, closed P&L, project path, symbol/account rules) using **only** SolidLinq-Core / Dashboard source.

## Layout

| Path | Purpose |
|------|---------|
| `src/SolidLinq.Quantower.Bridge/` | **net8.0** shared library — hub WebSocket client (`HubBridgeClient`), `HubDispatchMessage`, `CoreAckClient`, `LoggingStubExecutor` |
| `src/SolidLinq.Quantower/` | .NET 8 **console** host (WS + HTTP; uses Bridge + stub executor for smoke tests) |
| `src/SolidLinq.Quantower.Algo/` | **net8.0** Quantower Algo strategy (`SolidLinqBridgeStrategy`) — runs inside Quantower 1.145+ style SDKs; references Bridge + `TradingPlatform.BusinessLayer` from your install (see below) |
| `docs/discovery.md` | Structured discovery report |

## Build

**Solution (console + Bridge, no Quantower install required):**

```bash
dotnet build SolidLinq.Quantower.sln
```

**Quantower strategy DLL** (requires `TradingPlatform.BusinessLayer.dll` — same major .NET generation as Quantower, typically **.NET 8** for recent builds). Set `QUANTOWER_ALGO_SDK` to that folder, or pass MSBuild property `QuantowerSdkDir`:

```bash
# PowerShell example
$env:QUANTOWER_ALGO_SDK = "D:\AMP Quantower\TradingPlatform\v1.145.3\bin"
dotnet build src/SolidLinq.Quantower.Algo/SolidLinq.Quantower.Algo.csproj -c Release
```

Copy `SolidLinq.Quantower.Algo.dll` **and** `SolidLinq.Quantower.Bridge.dll` into Quantower’s **Strategies** folder, or run **`scripts/Deploy-QuantowerAlgo.ps1`**. **Restart Quantower** (or remove/re-add the strategy) so settings refresh.

**Quantower UI** matches cBot: Symbol, Account, Bridge Instance Id, Auth Token, Lot Multiplier, SL/TP toggles & multipliers, SL Mode, daily/weekly limits & targets, overall max loss/profit, drawdown mode, equity option, stop/close on hit, manual unlock. **Not in UI:** WebSocket URL, Core base URL, Worker API token (hosts are built into the DLL).

Confirm deploy: log line **`build 2026-05-19-cbot-ui`**. If you still see WebSocket URL / Worker API token, an old DLL is loaded.

Run console (example):

```bash
cd src/SolidLinq.Quantower
dotnet run -- \
  --ws wss://your-core.example/ws/bridge/your-bridge-instance-uuid \
  --bridge-instance-id your-bridge-instance-uuid \
  --auth-token from-dashboard-bridge-generate \
  --core https://your-core.example
```

Optional: `--worker-token` overrides the HTTP Bearer for the console smoke test only (advanced); otherwise the console uses `--auth-token` for REST as well.

## Related repos

- **SolidLinq-Core** — hub, `NormalizedCommand` broadcast, `POST /ack`, `POST /cbot/execution`
- **SolidLinq-Dashboard** — `quantower` platform, connections UI, bridge URL display

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

Copy `SolidLinq.Quantower.Algo.dll` **and** `SolidLinq.Quantower.Bridge.dll` to the Quantower **Scripts** / Algo extensions folder per Quantower’s “custom strategy” workflow, then add **SolidLinq Bridge** from the UI and fill the same parameters as in [`Prompt/Quantower.md`](../SolidLinq-Dashboard/Prompt/Quantower.md) (WS URL from dashboard bridge generate, core base URL, instance id, tokens, hub protocol version).

Run console (example):

```bash
cd src/SolidLinq.Quantower
dotnet run -- \
  --ws wss://your-core.example/ws/bridge/your-bridge-instance-uuid \
  --bridge-instance-id your-bridge-instance-uuid \
  --auth-token from-dashboard-bridge-generate \
  --core https://your-core.example \
  --worker-token YOUR_WORKER_API_TOKEN
```

The **in-terminal strategy** is `SolidLinqBridgeStrategy`; the console remains useful for protocol smoke tests with `LoggingStubExecutor`.

## Related repos

- **SolidLinq-Core** — hub, `NormalizedCommand` broadcast, `POST /ack`, `POST /cbot/execution`
- **SolidLinq-Dashboard** — `quantower` platform, connections UI, bridge URL display

# Architecture

Bandwidth is a **Space Engineers dedicated-server plugin** (loaded by
[Magnetar](https://magnetar.se)) that measures per-client network bandwidth, publishes
the measurements as telemetry, and can optionally pace outgoing state-sync traffic to a
client that the link cannot keep up with. It is **observe-only and safe at its defaults**:
out of the box it changes nothing about what the server sends — it only measures and
publishes.

This page is the map. For the *why* behind the numbers and the controller, read the two
design documents that predate the code:

- [`BandwidthEstimator.md`](BandwidthEstimator.md) — the full estimator/limiter design
  (the SE send pipeline, the capacity estimator, the AIMD controller, the limiter options,
  phasing). Section references like "§6.4" throughout the code and these docs point here.
- [`BandwidthTelemetry.md`](BandwidthTelemetry.md) — the exact telemetry contract a
  consumer (e.g. the Quasar agent) reads.

---

## The three projects

The repository is a single Visual Studio solution (`Bandwidth.sln`) with three projects,
the standard layout of the SE server plugin template:

| Project | Output | Role |
| --- | --- | --- |
| **`ServerPlugin`** | `Bandwidth.dll` (server) | The plugin. All of the bandwidth functionality lives here. Loaded by Magnetar on the dedicated server. |
| **`ClientPlugin`** | `Bandwidth.dll` (client) | Unmodified template scaffolding — a settings-dialog framework and a demo config. **Not part of the bandwidth feature**; see [SharedAndClient.md](SharedAndClient.md). |
| **`Shared`** | compiled into both | Shared-source project (`.projitems`): logging, config base types, the Harmony patch scaffolding (`PatchHelpers`, `EnsureCode`) used by both plugins. |

Everything that makes this plugin *Bandwidth* is in `ServerPlugin`. The client and shared
projects are the template's reusable plumbing. See [Build.md](Build.md) for how the
projects are built, referenced and deployed.

---

## Subsystems of the server plugin

```
                        ┌──────────────────────────────────────────────────┐
   game replication     │                  ServerPlugin                     │
   ───────────────►     │                                                    │
                        │  Patches/ ──────────►  Network/ ──────►  Stats/    │
   (Harmony patches on  │  (capture &            (measurement &    (stat     │
   the send/receive and  │   enforcement)         AIMD control)     schemas)  │
   replication paths)    │       │                    │                │      │
                        │       └──── budget ◄────────┘                │      │
                        │                                              ▼      │
                        │                              PluginSdk.Stats.PluginStats
                        │  Config/  ◄── Plugin.cs (lifecycle, config plumbing) │
                        └──────────────────────────────────────────────────┘
                                              │                       │
                              Bandwidth.cfg / Quasar         "bandwidth" provider
                                  (admin config)              (telemetry consumers)
```

The server plugin breaks into five folders, each documented on its own page:

| Folder | Page | What it does |
| --- | --- | --- |
| `Network/` | [Measurement.md](Measurement.md) | The observe-only estimator: `BandwidthMonitor` (registry + publisher), `ConnectionTracker`, `DirectionTracker` (BBR-style windowed-max rate estimate). |
| `Network/` | [Limiter.md](Limiter.md) | The opt-in AIMD limiter: `RateController` (per-client operating rate) and `BandwidthLimiter` (per-tick packet budget façade). |
| `Patches/` | [Patches.md](Patches.md) | The six Harmony patches that feed the measurement hot paths, the lifecycle hooks, the ACK-stall signal, and the packet-budget transpiler. |
| `Stats/` | [Patches.md](Patches.md#stat-schemas) / [BandwidthTelemetry.md](BandwidthTelemetry.md) | `BandwidthServerStats` / `BandwidthClientStats` — the self-describing stat schemas published to consumers. |
| `Config/` | [Configuration.md](Configuration.md) | `BandwidthConfig` and `BandwidthLimiterMode` — every admin-tunable knob. |
| (root) | [ServerLifecycle.md](ServerLifecycle.md) | `Plugin.cs` and `Preloader.cs`: startup, the before-world-load early bootstrap, config plumbing, and the periodic publish on the update loop. |

---

## Data flow

1. **Capture (hot path).** Harmony prefixes on the send (`MyNetworkWriter.SendPacket`) and
   receive (`MyTransportLayer.ProcessMessage`) chokepoints attribute each packet's bytes to
   the recipient/sender Steam ID via `BandwidthMonitor.RecordDown/RecordUp`. These run on
   the network/engine threads and only do an atomic add into a per-direction counter.
2. **Lifecycle.** Postfixes on `MyReplicationServer.AddClient` / `OnClientLeft` create and
   drop the per-client `ConnectionTracker` and bump a reconnect epoch.
3. **Sampling & publish (engine update thread).** `BandwidthMonitor.Update()` runs every
   frame and, once `PublishIntervalMs` has elapsed, folds each direction's accumulated bytes
   into a windowed-max rate estimate, steps the AIMD `RateController`, projects everything
   into a self-describing `StatsSnapshot`, and publishes it under the provider name
   `"bandwidth"` through Magnetar's `PluginSdk.Stats.PluginStats` hub.
4. **Optional enforcement.** When an admin sets `LimiterMode` to a pacing mode, the
   transpiler on `MyReplicationServer.FilterStateSync` calls `BandwidthLimiter.PacketBudget`
   in place of the stock literal `7`, so each client's per-tick unreliable state-sync packet
   budget is derived from its `RateController` target. The ACK-stall postfix
   (`MyClient.IsAckAvailable`) supplies the controller's overuse signal.

## Threading model

| Code | Thread(s) | Synchronisation |
| --- | --- | --- |
| `RecordDown` / `RecordUp`, `DirectionTracker.Add` | network / engine (any) | lock-free: `ConcurrentDictionary` lookup + `Interlocked.Add` |
| `OnClientConnected` / `OnClientDisconnected` | engine update | `ConcurrentDictionary` |
| `DirectionTracker.Sample`, `RateController.Update`, publish | engine update (single) | none needed — single-threaded |
| `RateController.RecordAckObservation` | replication thread | none — counters touched only there |

The design keeps the hot paths to an atomic add and defers all the math to the single
publishing thread, so telemetry never contends with packet I/O.

## Safety by construction

- With `LimiterMode = Off` (the default) the server sends **byte-for-byte stock**.
- When the limiter is on, the per-tick budget is `clamp(round((R_target/60)/MTU), 1, 7)`,
  so it can never exceed the stock `7` ("never worse than stock"), and only **unreliable
  state sync** is capped — reliable and streaming traffic are untouched.
- Every patch and limiter entry point is wrapped so a failure logs and returns stock
  behaviour rather than disturbing replication. The two limiter patches also `Prepare()`
  themselves out (and the transpiler bails to original IL) if the game method or anchor is
  gone on a future SE build.

## Lineage of the build

The git history of the `dev` branch shows how the plugin grew from the template:

1. **Initial commit** — the SE server plugin template (client + server + shared, example patches).
2. **Renamed** — `PluginTemplate` → `Bandwidth`.
3. **Estimator** — the observe-only measurement subsystem and telemetry publishing; the two
   design docs; the before-world-load early bootstrap; example patches removed.
4. **Limiter** — the opt-in AIMD limiter (`RateController`, `BandwidthLimiter`), the
   ACK-stall and packet-budget transpiler patches, and the limiter config + telemetry columns.

The current build corresponds to the design's **Phase 2** with the limiter implemented but
default-off; the delay-gradient Kalman detector, queue-depth/drop accounting and token-bucket
mode described in [`BandwidthEstimator.md`](BandwidthEstimator.md) §6/§10 are **not** in this
build (see [Measurement.md](Measurement.md) and [Limiter.md](Limiter.md) for exactly what is).

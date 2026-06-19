# Bandwidth

A **Space Engineers dedicated-server plugin** ([Magnetar](https://magnetar.se)) that measures
each connected client's network bandwidth, publishes it as telemetry, and can optionally pace a
client's outgoing traffic when the link can't keep up.

It is **observe-only and safe by default**. Out of the box it only *measures and publishes* —
it changes nothing about what the server sends. The pacing limiter is strictly opt-in, only
ever paces a client *down* (never above stock), and only touches unreliable state-sync traffic,
so enabling it cannot make delivery worse than vanilla.

## What it does

- **Per-client measurement** — up/down throughput per connected client, captured at the
  replication send/receive chokepoints from the very first packet (patches applied before world
  load).
- **Capacity estimate** — a BBR-style windowed-max delivery rate per direction (an honest
  *lower bound* while observe-only).
- **Telemetry** — a self-describing snapshot published every second under the `"bandwidth"`
  provider through Magnetar's `PluginSdk.Stats` hub, ready for a consumer such as the
  [Quasar](https://github.com/viktor-ferenczi/Quasar) agent to turn *"player X is lagging"* into
  a measurable per-client Network panel. Optional Steam-ID redaction for shared dashboards.
- **Optional adaptive limiter** — a per-client AIMD controller that derives a per-tick packet
  budget from an operating-rate target, enforced via a transpiler on `FilterStateSync`. Default
  **off**; configurable live through Quasar with no restart.

## Installation

This is a server plugin loaded by **Magnetar** on a Space Engineers dedicated server.

- **From source:** install the prerequisites, run `python setup.py` once (auto-detects your
  Space Engineers / Dedicated Server / Magnetar paths), then build `Bandwidth.sln` in Release.
  The post-build step deploys `Bandwidth.dll` into your Magnetar local plugin folder. Full
  steps, prerequisites and deploy details are in **[Docs/Build.md](Docs/Build.md)**.
- Enable the **Bandwidth** plugin in Magnetar. Measurement starts immediately; configure it
  (and optionally enable the limiter) via [Quasar](https://github.com/viktor-ferenczi/Quasar)
  or the `Bandwidth.cfg` file — see **[Docs/Configuration.md](Docs/Configuration.md)**.

> Distribution via [MagnetarHub](https://github.com/viktor-ferenczi/MagnetarHub) is not yet set
> up: the registration manifests (`BandwidthServer.xml` / `BandwidthClient.xml`) still carry
> template placeholder metadata. The repository also contains an unmodified `ClientPlugin` from
> the plugin template; it is **not** part of the bandwidth feature (see
> [Docs/SharedAndClient.md](Docs/SharedAndClient.md)).

## Documentation

Full documentation is under **[`Docs/`](Docs/TOC.md)**:

| Page | What it covers |
| --- | --- |
| [Architecture](Docs/Architecture.md) | The map: projects, subsystems, data flow, threading, safety. **Start here.** |
| [Server lifecycle](Docs/ServerLifecycle.md) | Startup, the before-world-load bootstrap, config plumbing, the publish loop. |
| [Measurement](Docs/Measurement.md) | The always-on estimator (`BandwidthMonitor` / `ConnectionTracker` / `DirectionTracker`). |
| [Limiter](Docs/Limiter.md) | The opt-in AIMD limiter (`RateController` / `BandwidthLimiter`). |
| [Patches](Docs/Patches.md) | The six Harmony patches and the published stat schemas. |
| [Configuration](Docs/Configuration.md) | Every admin knob and how config is stored / edited live. |
| [Build & deploy](Docs/Build.md) | Prerequisites, setup, building, deploying, versioning. |
| [Shared & client](Docs/SharedAndClient.md) | The `Shared` framework and the template `ClientPlugin`. |
| [Index](Docs/Index.md) | Flat map of every source file. |
| [Estimator design](Docs/BandwidthEstimator.md) · [Telemetry contract](Docs/BandwidthTelemetry.md) | The background design documents. |

## License

See [LICENSE](LICENSE).

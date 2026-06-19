# Bandwidth — documentation

Per-client network bandwidth measurement, telemetry, and optional adaptive pacing for Space
Engineers dedicated servers (loaded by [Magnetar](https://magnetar.se)). The plugin is
**observe-only and safe at its defaults**: out of the box it only measures and publishes — it
changes nothing about what the server sends — and the limiter is strictly opt-in and can never
pace a client above stock.

This is the documentation index. The top-level [`README`](../README.md) is the short overview
and install guide; everything below is the detail.

## Start here

- **[Architecture.md](Architecture.md)** — the map: the three projects, the server-plugin
  subsystems, data flow, threading model, safety guarantees, and how the build grew from the
  template. Read this first.

## Server plugin reference

| Page | Covers |
| --- | --- |
| [ServerLifecycle.md](ServerLifecycle.md) | Startup, the before-world-load early bootstrap (`Preloader` → `MyInitializer.InvokeBeforeRun`), config plumbing, the update/publish loop, shutdown. |
| [Measurement.md](Measurement.md) | The always-on estimator: `BandwidthMonitor`, `ConnectionTracker`, `DirectionTracker` (BBR-style windowed-max capacity *lower bound*). |
| [Limiter.md](Limiter.md) | The opt-in AIMD limiter: `RateController` (operating rate) and `BandwidthLimiter` (per-tick packet budget, "never worse than stock"). |
| [Patches.md](Patches.md) | The six Harmony patches (capture, lifecycle, ACK-stall, budget transpiler) and the published stat schemas. |
| [Configuration.md](Configuration.md) | Every admin knob, where config is stored/edited (Quasar), and runtime re-tuning. |

## Build and the rest of the repo

| Page | Covers |
| --- | --- |
| [Build.md](Build.md) | Prerequisites, `setup.py`, reference-path overrides, multi-targeting, deploy to Magnetar/Pulsar, versioning, distribution. |
| [SharedAndClient.md](SharedAndClient.md) | The `Shared` framework and the `ClientPlugin` template scaffolding (not part of the bandwidth feature). |
| [Index.md](Index.md) | Flat map of **every** source file → one-line description → its page. |

## Design background (predates the code)

| Page | Covers |
| --- | --- |
| [BandwidthEstimator.md](BandwidthEstimator.md) | The full estimator/limiter design: the SE send pipeline, the capacity estimator, the AIMD controller, the limiter options, phasing, risks. The `§N` references throughout the code point here. |
| [BandwidthTelemetry.md](BandwidthTelemetry.md) | The exact telemetry contract: the `PluginStats` transport, the two stat groups, identity/keying/redaction, and how a consumer (e.g. the Quasar agent) reads it. |

## What this build is

The current build is the design's **Phase 2**: the full observe-only estimator plus the
implemented-but-default-off AIMD packet-budget limiter. The delay-gradient Kalman detector,
queue-depth/drop accounting, token-bucket mode, and the other limiter options in
[BandwidthEstimator.md](BandwidthEstimator.md) are **not** in this build — each server page
notes precisely what is and isn't present.

---

*Machine-generated working data (file hashes for incremental doc re-runs) lives in
[`data/`](data/README.md) and can be deleted or git-ignored without affecting these docs.*

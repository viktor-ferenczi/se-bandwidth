# Configuration reference

Every admin-tunable knob, from [`ServerPlugin/Config/BandwidthConfig.cs`](../ServerPlugin/Config/BandwidthConfig.cs).

## Where the config lives and how it is edited

`BandwidthConfig` is a `PluginSdk.Config.PluginConfig` (Magnetar's config base), so it is:

- **Stored** on the server as `Bandwidth.cfg` in the server's user-data directory, in **sparse
  XML** — only non-default values are written, so the file shows at a glance what an admin
  changed.
- **Edited remotely through [Quasar](https://github.com/viktor-ferenczi/Quasar)**, Magnetar's
  control plane, which renders the UI from the `[Section]` / `[*Option]` attributes below.

Any change — local file or remote edit — raises `PropertyChanged`, which the plugin pushes
straight into the running monitor, so **every knob takes effect at runtime with no restart**
(see [ServerLifecycle.md](ServerLifecycle.md#configuration-plumbing)). All options sit in the
`core` section, captioned *"Bandwidth measurement"*.

## Measurement and telemetry

| Key | Type / range | Default | Meaning |
| --- | --- | --- | --- |
| `Enabled` | bool | `true` | Master switch. When off the estimator records nothing, publishing stops, the `"bandwidth"` provider is cleared, **and the limiter is forced off** regardless of `LimiterMode`. |
| `PublishIntervalMs` | int, 100–60000 | `1000` | How often a snapshot is computed and published. Also the rate-sampling interval. |
| `WindowSize` | int, 1–64 | `8` | Length of the windowed-max delivery-rate window, **in publish intervals** (8 × 1 s ⇒ an 8-second look-back by default). |
| `RedactClientId` | bool | `false` | Publish a stable per-process `anon-<hash>` token instead of the Steam ID, for shared/streamed dashboards. |

## Limiter (opt-in pacing)

The limiter is **off by default**. It only ever paces a client *down* (never above the stock
budget) and only **unreliable state sync**, so enabling it cannot make delivery worse than
stock. See [Limiter.md](Limiter.md) for the control law.

| Key | Type / range | Default | Meaning |
| --- | --- | --- | --- |
| `LimiterMode` | enum | `Off` | Pacing strategy — see below. |
| `RateFloorBytesPerSec` | double, 1024–16777216 | `32768` | AIMD lower bound on the operating target (`R_min`). Bounds the worst-case pacing of a stalling client. |
| `RateMaxBytesPerSec` | double, 1024–16777216 | `524288` | AIMD upper bound (`R_max`). At/above it the per-tick budget clamps to the stock 7, i.e. the client behaves as stock. |
| `RatePriorBytesPerSec` | double, 1024–16777216 | `262144` | Initial operating target for a freshly connected client, before the controller adapts. |
| `AimdIncreaseBytesPerSec2` | double, 0–16777216 | `65536` | Additive increase: bytes/sec gained per second of elapsed time while probing upward. |
| `AimdDecreaseFactor` | double, 0.05–0.99 | `0.85` | Multiplicative decrease factor on sustained overuse (0.85 ⇒ back off 15% per stalled window). |
| `StallBackoffFraction` | double, 0.01–1.0 | `0.2` | Fraction of serviced ticks in a window that must stall (ACK window exhausted) before the window counts as overuse. |

`BandwidthMonitor.Configure` clamps and sanity-orders these before applying them: it forces
`Floor ≥ 1`, `Max ≥ Floor`, the prior into `[Floor, Max]`, and keeps `Beta` a true
contraction in (0, 1) — so a misconfiguration cannot invert the bounds or turn the decrease
into a gain.

### `LimiterMode` values

| Value | Caption | Behaviour |
| --- | --- | --- |
| `Off` | *Off (observe-only)* | No pacing. The server sends exactly what the engine produces. The default. |
| `PacketBudget` | *Packet budget (adaptive AIMD)* | The implemented §7.2 limiter: a per-client per-tick packet budget driven by the AIMD controller. |
| `TokenBucket` | *Token bucket (reserved → packet budget)* | **Reserved, not implemented.** Logs a warning and falls back to `PacketBudget`. |

The enum is stored by **member name**, so reordering it never breaks an existing config.

## Notes

- The shared `Shared.Config.IPluginConfig` interface (which `BandwidthConfig` also implements)
  only requires `Enabled`; it is how the template's bootstrap and `Common` read the config
  generically. The full knob set above is specific to this plugin.
- Even with the limiter `Off`, the controller still runs and its `TargetBytesPerSec` /
  `PacketBudget` are **published as a preview** so an admin can see what pacing *would* do
  before enabling it — see [`BandwidthTelemetry.md`](BandwidthTelemetry.md).

# Bandwidth Telemetry Contract

How this plugin surfaces its per-client bandwidth measurements to a consumer
(the **Quasar.Agent**, or any other plugin) so an admin/developer can see *why a
specific player is lagging* — a constrained downlink, a low-confidence estimate,
a long-lived connection on a small pipe.

> Status: **implemented, observe-only.** The plugin only *measures and publishes*;
> it never paces traffic. The numbers reflect what the server actually sent and
> received — see [`BandwidthEstimator.md`](BandwidthEstimator.md) for the estimator
> and its (future) limiter phases.

The earlier `BandwidthTelemetrySdk.md` proposed a bespoke
`PluginSdk.Network.BandwidthStats` façade with bandwidth-specific DTOs. That was
**replaced** by a generic, schema-carrying statistics transport in the Magnetar
PluginSdk — `PluginSdk.Stats` — so the plugin owns its own stat definitions and
the SDK stays agnostic. This document describes that transport and the exact
contract the plugin publishes over it.

---

## 1. Transport: `PluginSdk.Stats.PluginStats`

A process-wide pub/sub hub. A producer publishes a self-describing
`StatsSnapshot` under a **provider name**; a consumer reads the latest snapshot by
name, lists active providers, or subscribes to every publication. It is in-process
(plain objects, no wire format — the consumer serializes however it likes) and
`public` (no `InternalsVisibleTo` coupling).

```csharp
namespace PluginSdk.Stats;

public static class PluginStats
{
    // Producer side (this plugin)
    static void Publish(string provider, StatsSnapshot snapshot);   // replaces the previous snapshot
    static void Clear(string provider);                             // remove it (e.g. plugin disabled)

    // Consumer side (Quasar.Agent, another plugin)
    static bool TryGetSnapshot(string provider, out StatsSnapshot snapshot);
    static IReadOnlyList<string> Providers { get; }                 // who is publishing right now
    static event Action<string, StatsSnapshot> Updated;            // fires on every Publish
}
```

`Updated` subscribers run synchronously on the publishing thread; a subscriber
that throws is logged and isolated, so one consumer cannot break another or the
producer.

### The snapshot shape

A snapshot is **self-describing**: every group carries the schema its instances
were captured against, so a consumer that has never seen this plugin can still
render the numbers.

```
StatsSnapshot
├─ UtcTimestamp : DateTime               // when the producer captured it
└─ Groups : StatGroup[]
   └─ StatGroup
      ├─ Schema : StatsSchemaData         // Name, LabelDescription, Fields[]
      │           Fields[] : { Name, Kind, Description, Unit, Parent,
      │                        AcrossInstances, OverTime }
      └─ Instances : StatInstance[]       // dynamically sized (e.g. one per client)
         └─ StatInstance { Label : string, Values : double[] }  // Values ∥ Schema.Fields
```

Every value is a `double` — booleans capture as `0/1`, enums as their underlying
integer — so a consumer needs only the inline schema to interpret a row. `Kind`
is `gauge` | `discrete` | `counter`; `AcrossInstances` / `OverTime` are enum
member names (e.g. `"Sum"`, `"Mean"`, `"Last"`, `"Max"`) telling a consumer how to
fold the value across the instances of a group and over successive snapshots.

---

## 2. What this plugin publishes

Provider name: **`bandwidth`** (`BandwidthMonitor.ProviderName`). Each publish is
one `StatsSnapshot` carrying **two groups**:

### 2.1 `BandwidthServerStats` — one instance, label `"server"`

| Field | Kind | Unit | Across instances | Over time | Meaning |
| --- | --- | --- | --- | --- | --- |
| *(label)* | — | — | — | — | always `"server"` |
| `ClientCount` | gauge | — | Sum | Mean | connected clients in this snapshot |
| `TickRateHz` | discrete | Hz | None | Last | replication tick rate (~60) |
| `LimiterActive` | discrete | — | None | Last | outgoing pacing enforced — **always `0` in this observe-only build** |

### 2.2 `BandwidthClientStats` — one instance per connected client

The label is the client's identity (§3). A reconnect of the same user is a new
row distinguished by `ConnectionEpoch`, so a consumer keys a time series on
`(label, epoch)`.

| Field | Kind | Unit | Across instances | Over time | Meaning |
| --- | --- | --- | --- | --- | --- |
| *(label)* | — | — | — | — | Steam ID, or an anonymous hash when redaction is on |
| `ConnectionEpoch` | discrete | — | None | Last | reconnect counter within this process |
| `DownBytesPerSec` | gauge | B/s | Sum | Mean | server → client measured throughput last interval |
| `DownEstimateBytesPerSec` | gauge | B/s | Sum | Mean | server → client windowed-max capacity estimate |
| `DownConfidence` | gauge | — | **Mean** | Mean | confidence of the down estimate, 0..1 |
| `UpBytesPerSec` | gauge | B/s | Sum | Mean | client → server measured throughput last interval |
| `UpEstimateBytesPerSec` | gauge | B/s | Sum | Mean | client → server windowed-max capacity estimate |
| `ConnectedSeconds` | gauge | s | **None** | **Max** | seconds since this connection opened |

Per-client rate gauges default to `Sum` across instances, so a consumer that sums
a column gets the server total (e.g. total downlink). `DownConfidence` is averaged
rather than summed; `ConnectedSeconds` is per-connection and neither summed nor
averaged (kept as the max over the bucket). These are the eight scalars the
observe-only build actually measures — the larger design snapshot (target rate,
detector state, queue depth, defer/drop counts) in `BandwidthEstimator.md` §10
arrives with the later limiter phases.

---

## 3. Client identity, keying, redaction

- **Identity** is the connection's Steam ID. Trackers are keyed per Steam ID and
  carry a **connection epoch** that increments on every reconnect, so a consumer
  keys its time series on `(label, epoch)` and a reconnect starts a clean series.
- **Redaction** (`RedactClientId`, default off): when on, the label is a stable
  per-process anonymous token, `anon-<16 hex>`, derived from the Steam ID by a
  64-bit FNV-1a hash. It is stable within a server run — so a consumer can still
  follow one client across snapshots — but does not reveal who it is. Useful for
  shared or streamed dashboards. `ConnectionEpoch` is unaffected.

---

## 4. Cadence, lifecycle, and runtime control

- **Cadence.** `BandwidthMonitor.Update()` runs from the engine update loop and
  republishes once the configured `PublishIntervalMs` (default 1000 ms) has
  elapsed, sampling each direction's windowed-max filter over the real elapsed
  interval. `WindowSize` (default 8) is the windowed-max length in publish
  intervals.
- **Enable/disable at runtime.** Toggling `Enabled` (or changing any tunable) flows
  through `BandwidthMonitor.Configure(...)`. Disabling stops publishing **and**
  calls `PluginStats.Clear("bandwidth")`, so a consumer watching `Providers` /
  `Updated` sees the provider go quiet without a server restart.
- **Shutdown.** `Dispose` → `BandwidthMonitor.Reset()` clears the registry and the
  published snapshot.

### Config knobs

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Master switch; off ⇒ no measurement, provider cleared. |
| `PublishIntervalMs` | `1000` | Publish / rate-sampling interval (ms). |
| `WindowSize` | `8` | Windowed-max length, in publish intervals. |
| `RedactClientId` | `false` | Publish `anon-<hash>` instead of the Steam ID. |
| `LimiterMode` | `Off` | Reserved; every mode is a no-op in this observe-only build. |

---

## 5. Consuming the telemetry (e.g. Quasar.Agent)

The agent references the PluginSdk already, so it gets `PluginSdk.Stats` for free.
Either **poll** on its own cadence or **subscribe** and coalesce:

```csharp
// Poll (recommended): the agent owns its cadence, decoupled from the server tick.
if (PluginStats.TryGetSnapshot("bandwidth", out var snap))
    Forward(snap);   // serialize + ship on the agent's existing channel

// Or event-driven, with the agent's own throttle so a fast cadence can't flood:
PluginStats.Updated += (provider, snap) =>
{
    if (provider == "bandwidth") sampleQueue.OfferLatest(snap);
};
```

Guidance:

- **Feature-detect** with `Providers` / `TryGetSnapshot` — never assume the plugin
  is loaded or enabled.
- **Use the inline schema.** Read `Values` by the position of the matching
  `Schema.Fields` entry rather than hard-coding indices; the field set may grow
  additively.
- **Key on `(label, ConnectionEpoch)`** so a reconnect starts a fresh series.
- **Coalesce latest-wins** if the consumer falls behind — this is a current-state
  gauge, not an event log; dropping a stale sample is fine.
- **Send on a distinct metrics channel**, not the log stream, so a UI routes it to
  a metrics store rather than the log panel.

### The lag-diagnosis angle

A per-server **Network** panel, one row per connected client, refreshed each
sample, turns "player X says they lag" into something measurable:

| Player | Down actual / estimate | Up actual | Conf. | Connected |
| --- | --- | --- | --- | --- |
| SomePlayer | 52 / 61 KB/s | 9 KB/s | ▓▓▓▓░ | 14 m |
| LaggyGuy | 27 / 28 KB/s | 5 KB/s | ▓▓░░░ | 3 m |

Sort by the smallest down estimate to surface who the server is struggling to
feed; expand a row into estimate/achieved/confidence sparklines to tell a steady
cap from transient congestion; correlate with the plugin-log panel for the same
player/time. When the limiter phases land, the same panel gains the target-rate,
queue-depth and drop columns from `BandwidthEstimator.md` §10.

---

## 6. References

- [`BandwidthEstimator.md`](BandwidthEstimator.md) — the estimator that produces
  these numbers (keying §5, the windowed-max capacity estimate §6.7, the full
  telemetry/validation plan §10, phasing §12).
- `ServerPlugin/Network/BandwidthMonitor.cs` — the producer (`Publish`,
  `Configure`, `FormatClientLabel`).
- `ServerPlugin/Stats/BandwidthServerStats.cs`,
  `ServerPlugin/Stats/BandwidthClientStats.cs` — the published stat schemas.
- `PluginSdk/Stats/` (in the Magnetar repo) — the generic transport:
  `PluginStats`, `StatsSnapshot`, `StatsSchema`, and the `Gauge`/`Discrete`/
  `Counter`/`StatLabel` attributes.

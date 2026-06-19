# Measurement subsystem (the observe-only estimator)

The always-on half of the plugin: it measures each client's up/down throughput, estimates a
capacity lower bound, and publishes the result as telemetry — **without ever changing what
the server sends**. Source: [`ServerPlugin/Network/`](../ServerPlugin/Network/)
(`BandwidthMonitor`, `ConnectionTracker`, `DirectionTracker`). The optional pacing half is
documented separately in [Limiter.md](Limiter.md).

Design references: [`BandwidthEstimator.md`](BandwidthEstimator.md) §6.7 (the windowed-max
capacity estimate) and §5 (identity/keying); [`BandwidthTelemetry.md`](BandwidthTelemetry.md)
(the published contract).

---

## `BandwidthMonitor` — registry and publisher

[`BandwidthMonitor.cs`](../ServerPlugin/Network/BandwidthMonitor.cs) is a process-lifetime
`static` class: the registry of live per-client trackers plus the periodic publisher. It is
the single seam between the game (via patches), the config (via `Configure`), and consumers
(via `PluginStats`).

| Member | Thread | Purpose |
| --- | --- | --- |
| `ProviderName = "bandwidth"` | — | The provider name consumers read snapshots under. |
| `Configure(enabled, publishIntervalMs, windowSize, redactClientId, limiterMode, …AIMD…)` | engine | Apply host config. Clamps the values, pushes the AIMD tunables into `RateController`'s statics and the mode into `BandwidthLimiter`, and forces the limiter off when the plugin is disabled. Disabling also clears the published telemetry. |
| `RecordDown(steamId, bytes, reliable)` / `RecordUp(steamId, bytes)` | any (hot path) | Add bytes to a live client's down/up `DirectionTracker`. A no-op when disabled, for unknown clients, or for non-positive byte counts. |
| `OnClientConnected(steamId)` | engine | Bump the client's reconnect **epoch** and start a fresh `ConnectionTracker`. |
| `OnClientDisconnected(steamId)` | engine | Remove the client from the live registry. |
| `TryGetConnection(steamId, out connection)` | any | Look up a live connection — used by the limiter and the ACK-stall postfix. |
| `Update()` | engine (every frame) | Republish a snapshot once `PublishIntervalMs` has elapsed (measured with `Stopwatch`). Cheap to call every frame. |
| `Reset()` | engine | Clear all state and the published snapshot (called on `Dispose`). |

Live trackers are held in a `ConcurrentDictionary<ulong, ConnectionTracker>` keyed by Steam
ID; a separate `ConcurrentDictionary<ulong, uint>` remembers the last epoch handed out per
Steam ID so a reconnect always gets a higher one (and is therefore a fresh time series for
consumers, never a resumed one).

### Publishing

`Update()` measures the *real* elapsed interval and, when due, calls `Publish(dtSeconds)`,
which for each live connection:

1. `connection.Down.Sample(dt)` / `connection.Up.Sample(dt)` — fold accumulated bytes into the
   windowed-max estimate.
2. `connection.Control.Update(Down.AchievedBytesPerSec, dt)` — step the AIMD controller. This
   runs **even when pacing is off**, so the published `TargetBytesPerSec` / `PacketBudget` are
   a live preview of what the limiter *would* do.
3. Build a `BandwidthClientStats` row.

It then builds a one-row `BandwidthServerStats` (client count, ~60 Hz tick rate,
`LimiterActive`), captures both groups against their inline schemas, and calls
`PluginStats.Publish("bandwidth", snapshot)`. The whole publish is wrapped — telemetry must
never disrupt the update loop.

### Client identity and redaction

`FormatClientLabel(steamId)` returns the raw Steam ID by default. With `RedactClientId` on it
returns `anon-<16 hex>`, a stable per-process token derived from the Steam ID by a 64-bit
**FNV-1a** hash: stable within a server run (so a consumer can still follow one client across
snapshots) but not reversible to the Steam ID. Useful for shared or streamed dashboards. The
epoch is unaffected by redaction. See [`BandwidthTelemetry.md`](BandwidthTelemetry.md) §3.

---

## `ConnectionTracker` — one client's state

[`ConnectionTracker.cs`](../ServerPlugin/Network/ConnectionTracker.cs) bundles everything for
one connection:

| Member | Meaning |
| --- | --- |
| `SteamId`, `Epoch` | Identity and reconnect counter (the registry key + series discriminator). |
| `Down` : `DirectionTracker` | Server → client — the direction the limiter paces. |
| `Up` : `DirectionTracker` | Client → server — diagnostic only; never paced. |
| `Control` : `RateController` | The downlink AIMD operating-rate controller (see [Limiter.md](Limiter.md)). |
| `Closed` | Set when the connection closes, just before it is dropped from the registry. |
| `ConnectedSeconds` | Wall-clock seconds since the tracker was created (from `Stopwatch`). |

---

## `DirectionTracker` — per-direction rate estimate

[`DirectionTracker.cs`](../ServerPlugin/Network/DirectionTracker.cs) is the actual estimator
for one direction of one connection. It cleanly separates a lock-free hot path from
single-threaded math:

- **`Add(bytes)`** (hot path, any thread) — `Interlocked.Add` into a running byte accumulator.
  Nothing else.
- **`Sample(dtSeconds)`** (publishing thread only, once per interval) —
  `Interlocked.Exchange` the accumulator to 0, divide by the elapsed time to get this
  interval's rate, push it into a ring buffer of length `windowSize`, and recompute the
  **maximum over the window**. That windowed max is the capacity estimate.

Exposed (read on the publishing thread right after `Sample`):

| Property | Meaning |
| --- | --- |
| `AchievedBytesPerSec` | The most recent interval's measured goodput. |
| `EstimateBytesPerSec` | The windowed-max delivery rate — the capacity estimate (BBR's `BtlBw`). |
| `Confidence` | A 0..1 **warm-up ramp** capped at 0.5 (`0.5 * min(1, samples/windowSize)`). |

### Why the estimate is a *lower bound* (and confidence is capped)

This is a **BBR-style windowed maximum** of the per-interval delivery rate
([`BandwidthEstimator.md`](BandwidthEstimator.md) §6.7). Because the plugin never paces the
link in observe-only mode, every sample may be **application-limited** — the server simply had
little to send — so the windowed max is a *lower bound* on the true capacity, never an
over-estimate. That is honest and the right starting point.

The full design (§6.2/§6.4) calls for a **delay-gradient Kalman filter** that would turn ACK
arrival timing into a variance-based confidence and a tighter capacity estimate. That filter
needs per-packet ACK timing from the internal `MyClient` and is **not in this build**; until
it lands, `Confidence` is the deliberately-modest warm-up ramp above rather than a real
statistical confidence. The uplink (`Up`) uses the same windowed-max estimator; its estimate
is published but, per the telemetry contract, its confidence is not surfaced.

## Memory and cost

Per direction the tracker holds a `double[windowSize]` ring (default 8 entries) plus a few
scalars; per connection that is two trackers and one `RateController`. The hot path is one
interlocked add; all O(window) work happens once per `PublishIntervalMs` on a single thread.

## Related

- [Patches.md](Patches.md) — the patches that call `RecordDown/RecordUp` and the lifecycle hooks.
- [Limiter.md](Limiter.md) — `RateController` / `BandwidthLimiter`, fed from here.
- [Configuration.md](Configuration.md) — `PublishIntervalMs`, `WindowSize`, `RedactClientId`.

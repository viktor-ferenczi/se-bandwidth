# The limiter (opt-in AIMD pacing)

The optional, **default-off** half of the plugin: an adaptive per-client controller that
paces a client's outgoing *unreliable state-sync* traffic when the link cannot keep up — and
**never above stock**. Source: [`ServerPlugin/Network/RateController.cs`](../ServerPlugin/Network/RateController.cs)
and [`ServerPlugin/Network/BandwidthLimiter.cs`](../ServerPlugin/Network/BandwidthLimiter.cs).

Design references: [`BandwidthEstimator.md`](BandwidthEstimator.md) §6.4 (the AIMD
controller), §7.2 (the O2 dynamic packet budget), §7.6 (the ACK-window interaction), §11
(closed-loop blindness). Enable it with `LimiterMode = PacketBudget` — see
[Configuration.md](Configuration.md).

> The numbers it produces (`TargetBytesPerSec`, `PacketBudget`) are computed and **published as
> a preview even when pacing is off**. Enforcement only happens when the mode is on; the
> measurement subsystem ([Measurement.md](Measurement.md)) runs regardless.

---

## `RateController` — per-client operating rate

A pure, game-independent class (no game references, so it is unit-testable in isolation). It
maintains one operating target `R_target` (bytes/sec) per connection and adjusts it once per
publish interval with classic TCP-style **AIMD**.

### Signals

Just two, deliberately — the only ones this build actually has:

- **ACK-window stall** — the overuse brake. The game's `MyClient.IsAckAvailable()` returning
  `false` on a serviced tick means the client's in-flight ACK window is exhausted. The
  `IsAckAvailable` postfix forwards each observation via `RecordAckObservation(stalled)`, which
  bumps a `stallTicks` or `serviceTicks` counter.
- **elapsed time** — the additive-increase probe.

### The control law (`Update(achievedBytesPerSec, dt)`)

```
stalls/total ≥ StallFraction   ⇒  R_target ← max(Floor, Beta · R_target)     (multiplicative decrease)
otherwise                       ⇒  R_target ← R_target + Alpha · dt           (additive increase)
R_target is then clamped to [Floor, Max]; the per-window counters reset.
```

The static tunables (global policy, pushed from config by `BandwidthMonitor.Configure`) are
`Floor` (R_min), `Max` (R_max), `Prior` (initial target), `Alpha` (additive increase,
bytes/s per second), `Beta` (decrease factor in (0,1)), and `StallFraction`. Defaults mirror
[`BandwidthConfig`](Configuration.md) so a controller built before the first `Configure` is
already sane. `TargetBytesPerSec` exposes `R_target`; `Overuse` exposes whether the last step
backed off.

### Why it probes to `R_max`, not to the achieved rate

The additive probe climbs toward `R_max` rather than toward the measured drain rate **on
purpose**. Using achieved throughput as the increase ceiling would self-lock the limiter at
whatever it is currently sending — the "closed-loop blindness" of
[`BandwidthEstimator.md`](BandwidthEstimator.md) §11. With an unconditional upward probe and
the stall as the only brake, `R_target` naturally settles just above the rate the link can
actually drain. (`achievedBytesPerSec` is therefore informational in this build — kept in the
signature for telemetry symmetry and a future rate-coupled variant.)

All of `RecordAckObservation` and `Update` run on the engine replication/update thread, so the
per-window counters need no locking.

---

## `BandwidthLimiter` — the packet-budget façade

A `static` façade between the replication send loop and the per-client `RateController`s, and
the home of the **§7.2 O2 dynamic packet budget**. A transpiler on
`MyReplicationServer.FilterStateSync` replaces the hardcoded per-client budget (`int num2 =
7;`) with a call to `PacketBudget(client)`, so the per-tick packet budget becomes per-client
and adapts to the controller.

| Member | Purpose |
| --- | --- |
| `TickHz = 60`, `MtuBytes = 1190` | Convert a per-second target into a per-tick packet count. MTU = usable state-sync payload per packet ([`BandwidthEstimator.md`](BandwidthEstimator.md) §2.1). |
| `Configure(mode)` | Set the active `BandwidthLimiterMode` (pushed from config). |
| `PacketBudget(object client)` | Called by the transpiler. Returns the per-client budget, or the stock `7` when the mode is `Off`, the client is unknown, or **anything throws**. |
| `BudgetFromTarget(target)` | Pure: `clamp(round((target / 60) / 1190), 1, 7)`. Shared by `PacketBudget` and the telemetry capture. |
| `GetSteamId(object client)` | Read a `MyClient`'s Steam ID via its public `State` field. |

### Safe by construction

- The budget is always `clamp(…, 1, 7)`: it can **never exceed the stock 7**, and a client
  whose `R_target` has ramped to `R_max` clamps back to exactly 7 (= stock). The floor of 1
  keeps the top-priority group (the controlled entity) flowing.
- Only **unreliable state sync** decrements this budget; streaming and reliable traffic take
  other paths in `FilterStateSync`, so reliable delivery is never affected.
- `PacketBudget` is wrapped: any failure logs once and returns 7, so an enabled limiter can
  only ever pace *down*, never break the send loop.

### Reflection note

`MyClient` is `internal` to `VRage.Network` and this plugin **does not publicize** game
assemblies, so the client is passed as `object` and its Steam ID is read through its public
`State` field (a public `MyClientStateBase`) via a once-resolved, cached `FieldInfo` — the
only reflection on the path. The runtime type is the same `MyClient` for every connection, so
one cached accessor serves all.

---

## How the pieces connect at runtime

```
MyClient.IsAckAvailable() ──postfix──► RateController.RecordAckObservation(stalled)   (per tick)
                                              │  accumulates stall/service counts
BandwidthMonitor.Update() (per interval) ─────┼─► RateController.Update(dt)  → R_target
                                              │
MyReplicationServer.FilterStateSync ──transpiler─► BandwidthLimiter.PacketBudget(client)
                                                        └─► BudgetFromTarget(R_target) → 1..7
```

See [Patches.md](Patches.md) for the two patches involved
([`Patch_ClientAckAvailable`](../ServerPlugin/Patches/Patch_ClientAckAvailable.cs) and
[`Patch_FilterStateSyncBudget`](../ServerPlugin/Patches/Patch_FilterStateSyncBudget.cs)), both
of which `Prepare()` themselves out of existence if their game target is missing, so this
default-off feature can never break the server on an SE update.

## What this build does *not* do

Per [`BandwidthEstimator.md`](BandwidthEstimator.md) §7/§10, the design also describes a
token-bucket limiter (O1), send-interval/LOD scaling (O3), explicit defer/drop accounting, and
queue-depth telemetry. This build paces by **deferral only** (the packet budget), **drops
nothing**, has no Kalman delay detector, and the `TokenBucket` mode is reserved — it logs a
warning and falls back to `PacketBudget`.

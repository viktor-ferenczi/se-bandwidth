# Per-Client Bandwidth Estimator

A design for a **per-connection, per-direction bandwidth estimator** for the
Space Engineers Dedicated Server, driven by a **Kalman filter** over the bytes
the replication layer sends/receives and the packet timing it already observes.
The estimate is later used to **pace outgoing replication traffic to each client**
so the server uses as much of the link as is available *without* causing the
latency spikes that come from over-feeding a client's connection.

> Status: **Phase 1 (observe-only) and a first real limiter (Phase 2) are
> implemented.** This document fixes the data-flow, the math, the integration
> points in the SE replication layer, and the candidate throttling mechanisms; the
> shipped limiter is a deliberately reduced subset of the full §6 design.
>
> What ships today: the capture patches under
> [`ServerPlugin/Patches/`](../ServerPlugin/Patches), a per-connection registry
> keyed as in §5, a **BBR-style windowed-max** delivery-rate estimate per
> direction (§6.7 — *not* the Kalman fusion of §6.2–6.4, which remains a design
> alternative), per-client telemetry through the Magnetar PluginSdk Stats API (see
> [`BandwidthTelemetry.md`](BandwidthTelemetry.md)), and an **opt-in adaptive limiter**:
> the §6.4 AIMD operating-rate controller driving the §7.2 O2 dynamic per-client
> packet budget.
>
> **Deltas vs the full design** (intentional, scoped): the controller uses the
> **ACK-window stall** (`MyClient.IsAckAvailable()` returning false) as its overuse
> signal — there is **no delay-gradient Kalman filter** (§6.2), no explicit probe
> state machine (the additive increase *is* the probe), and no Steam-session polling.
> The control cadence equals the publish interval (§4) rather than per-ACK. Enforcement
> is **deferral-only** — no unreliable dropping (§7.7 step 3). `LimiterMode = Off` is
> the default, so the estimator never paces traffic unless an admin opts in.

---

## 1. TL;DR

- **What we estimate:** for every live client connection, two scalars with
  confidence — the sustainable **downlink** (server → client) and **uplink**
  (client → server) capacity in bytes/second. The downlink is the one we act on.
- **How:** SE's replication layer already stamps every state-sync packet with a
  server timestamp, gets per-packet ACKs back, and tracks per-client ping. That
  gives us *send size + send time + ack time* per packet — exactly the inputs a
  **delay-gradient Kalman filter** (the same model Google Congestion Control uses
  for WebRTC) needs. We pair it with a **gated throughput Kalman filter** for the
  magnitude, and fuse them with an AIMD rate controller that also probes for
  headroom.
- **Where the state lives:** a process-lifetime registry keyed by a
  **per-connection id** derived from the replication layer's `EndpointId`
  (Steam ID) plus a connection epoch, so a reconnect of the same Steam user is a
  fresh estimator. Updated on the engine update thread from Harmony hooks.
- **How we limit:** the recommended lever is to replace SE's hard-coded
  *7-state-sync-packets-per-client-per-tick* cap with a **per-client token bucket**
  refilled at the estimated rate, plus a graceful **defer/shed** policy for the
  per-client dirty queue when over budget. Several secondary levers
  (per-client send-interval/LOD scaling, join-stream pacing) are described and
  compared.
- **Important reality check:** SE's 6-deep ACK window already caps in-flight data
  to ~7 KB, so most *internet* clients are already **window-limited** (≈ window ÷
  RTT), well under the ~500 KB/s tick ceiling. This estimator is therefore first
  and foremost a **down-regulator** that prevents the per-client dirty queue from
  ballooning during bursts; raising throughput *above* today's ceiling is a
  separate, riskier change (it needs the ACK window widened — see §7.6).

---

## 2. How SE moves data to a client today

Everything below is in the decompiled DS sources, readable through the
`se-dev-server-code` skill. Paths are relative to its `Data/Decompiled/` root.
Understanding this path is what makes the estimator implementable: every input we
need is already produced here, and every throttle lever attaches here.

### 2.1 The send pipeline (≈ 60 Hz)

```
MyMultiplayerBase.Update()                         // once per server sim tick (~60 Hz)
  └─ MyReplicationServer.SendUpdate()              // VRage/VRage/Network/MyReplicationServer.cs:412
       └─ for each MyClient:
            FilterStateSync(client)                // MyReplicationServer.cs:850
              ├─ if !client.IsAckAvailable() → skip this client this tick   (ACK window full)
              ├─ dequeue up to 7 state groups from client.DirtyQueue (priority queue)
              │     num2 = 7  = MAX_NUM_STATE_SYNC_PACKETS_PER_CLIENT       (MyReplicationServer.cs:30,862)
              ├─ client.SendStateSync(entry, mtu, ref data, serverTimeStamp) // MyClient.cs:574
              │     WritePacketHeader(...) stamps serverTimeStamp + echoes client ts // MyClient.cs:497
              └─ m_callback.SendStateSync(data, endpointId, reliable:false)
                   └─ MyTransportLayer.SendMessage(...)   // Sandbox.Game/.../MyTransportLayer.cs:103
                        └─ MyNetworkWriter.SendAll()      // .../Networking/MyNetworkWriter.cs:236
                             └─ MyGameService.Peer2Peer.SendPacket(...)
                                  └─ MySteamPeer2Peer.SendPacket(...)        // VRage.Steam/.../MySteamPeer2Peer.cs:93
                                       └─ SteamGameServerNetworking.SendP2PPacket(...)   // UDP / Steam relay
```

Key constants and budgets, all per *client*:

| Quantity | Value | Source |
| --- | --- | --- |
| Sim/replication tick rate | ~60 Hz | `MyMultiplayerBase.Update → ReplicationLayer.SendUpdate` |
| Max state-sync packets / client / tick | **7** | `MAX_NUM_STATE_SYNC_PACKETS_PER_CLIENT` (`MyReplicationServer.cs:30`) |
| MTU | **1200 B**; payload `GetMTUSize()` ≈ **1190 B** | `MySteamPeer2Peer.MTUSize = 1200`; `FilterStateSync` uses `MTU-10` |
| In-flight ACK window | **6 packets** | `IsAckAvailable`: `m_lastReceivedAckId - 6` (`MyClient.cs:377`) |
| Per-client tick ceiling (LAN/instant-ack) | ~7 × 1190 × 60 ≈ **500 KB/s** (~4 Mbit/s) | derived |
| Fragmentation threshold (streaming) | **1,000,000 B** (`SIZE_MTR`) | `MyNetworkWriter` |

State updates are delivered on an **unreliable** channel as last-value state
groups: a newer dirty value supersedes an older one, so *deferring* a queued
group is cheap and lossless — this matters for the over-budget policy in §7.7.
Bulk/initial data (a joining client's world, large replicables) goes through a
separate **streaming** packet id path with its own ACKs and 1 MB fragmentation.

### 2.2 The window/BDP constraint (why this is mostly a down-regulator)

The ACK window allows **6 unacked packets** in flight ≈ `6 × 1190 ≈ 7.1 KB`.
Classic bandwidth-delay product says deliverable throughput ≈ `window ÷ RTT`:

| Client RTT | `window ÷ RTT` | Binding limit |
| --- | --- | --- |
| 3 ms (LAN) | ~2.3 MB/s | the **7-packet/tick** cap (~500 KB/s) |
| 50 ms | ~140 KB/s | the **ACK window** |
| 150 ms | ~47 KB/s | the **ACK window** |

So a typical internet client *cannot* receive the full 500 KB/s no matter what —
it is throttled by the window, not by our budget. The practical failure mode
today is not "we never use the link"; it is: **a burst of dirty state (big battle,
PCU spike, mass block changes) floods `DirtyQueue` faster than the window drains,
`IsAckAvailable` stalls, and updates pile up and arrive late and bursty** — i.e.
a lag spike. The estimator's first job is to *measure the rate the link actually
drains at* and pace/shed against it so the queue stays bounded and latency stays
flat. Genuinely *raising* the ceiling for fat low-RTT pipes is possible but
requires widening the ACK window (§7.6) and is out of scope for phase 1.

### 2.3 Reliable vs. unreliable traffic (what is safe to drop)

The transport carries two reliability classes, chosen per send call and mapped
onto Steam's `EP2PSend` type in `MySteamPeer2Peer.SendP2PPacket`:

| Class | What | Where it's sent | Loss recovery | Limiter may… |
| --- | --- | --- | --- | --- |
| **Unreliable** | the per-tick **state sync** — the bulk traffic | `FilterStateSync` → `SendStateSync(…, reliable: false)` (`MyReplicationServer.cs:893`) | **last-value**: a newer dirty supersedes; the ACK window resends the *latest* state, not the lost datagram | pace, defer, **and drop** |
| **Reliable** | **streaming** (join / world download, large replicables) and reliable **events**/RPCs | `SendStreamingEntry` → `SendStateSync(…, reliable: true)` (`:825`); `DispatchEvent(…, site.IsReliable)` (`:1439`) | guaranteed delivery + retransmit | pace only — **never drop** |

This split is what makes shedding safe. Bulk state sync is unreliable *and*
last-value, so skipping an update never corrupts state — the entity's next dirty
(or an ACK-driven resend) carries the current value. Reliable traffic, by
contrast, must arrive: it can only be paced (O4) or allowed to back-pressure,
never dropped. The over-budget policy (§7.7) leans on exactly this distinction.

---

## 3. Goals and non-goals

**Goals**
1. Per-connection, per-direction capacity estimate (bytes/s) with a confidence/variance.
2. Track it for the life of each connection; **reconnect = new estimate**.
3. Hold all estimates in memory for the Magnetar process lifetime, keyed by a
   unique client/connection id available in the replication layer.
4. Use only signals SE already produces (sizes, send/ack timestamps, ping, Steam
   session state) — no protocol or wire-format changes, no client-side changes.
5. Be cheap: O(1) work per packet/ack, a few dozen bytes of state per client.

**Non-goals (phase 1)**
- Changing the SE wire protocol or adding new packet types.
- Widening the ACK window / raising the absolute per-client ceiling (separate effort, §7.6).
- Estimating fairness across clients or total server uplink (a future aggregate layer can sum per-client estimates).

**The identifiability caveat (the crux).** We only ever observe *achieved*
throughput, which is `min(offered load, capacity)`. When we deliberately send
less than capacity, throughput is a **lower bound**, not a measurement of the
ceiling. Therefore the estimator must **gate** capacity measurements on a
saturation signal (queue building, ACK window stalling, or delay gradient rising)
and must **probe** upward to rediscover headroom once the limiter is throttling.
This single fact shapes the whole filter design (§6).

---

## 4. Signals available from the replication layer

All of these already exist; the estimator only has to *observe* them.

| # | Signal | Source (decompiled) | Dir. | Meaning for estimation |
| --- | --- | --- | --- | --- |
| S1 | Bytes of each state-sync packet + send time | `MyClient.SendStateSync` / `WritePacketHeader` stamps `serverTimeStamp` (`MyClient.cs:497,574`) | down | per-packet `(L_i, S_i)` |
| S2 | Per-packet ACK + arrival time | `MyClient.OnClientAcks`/`OnAck` (`MyClient.cs:389,435`) → ack time `A_i` on server clock | down | delivery confirmation + timing |
| S3 | ACK-window stall (backpressure) | `IsAckAvailable()` returns false at 6 in-flight (`MyClient.cs:377`) | down | hard saturation / loss-ish signal |
| S4 | Round-trip / ping | timestamp echo in header (`serverTimeStamp` + `m_lastClientTimestamp`, `MyClient.cs:517–518`) digested to `MyClientStateBase.Ping` ms (`MyClientStateBase.cs:65`) | both | RTT level + trend |
| S5 | Steam P2P send-queue backlog | `MySteamPeer2Peer.GetSessionState` → `BytesQueuedForSend`, `PacketsQueuedForSend`, `UsingRelay`, `RemoteIP`, `RemotePort` (`MySteamPeer2Peer.cs:124`) | down | direct socket backlog; **populated but never read by SE today** |
| S6 | Inbound packet size + arrival time | `MyClient.ProcessIncomingPacket` / `m_lastReceivedTimeStamp` (`MyClient.cs:262,267`) | up | per-packet `(L_i, recv_i)` for uplink filter |
| S7 | Existing aggregate counters | `MyReplicationServer.UpdateStatisticsData(outgoing, incoming, …)` (`MyReplicationServer.cs:959`); `MyTransportLayer` 120-frame sliding windows | both | sanity cross-check / telemetry |

Notes:
- **S2 vs one-way delay.** True GCC uses client-side *arrival* timestamps; SE's
  ACKs are batched and arrive back at the server, so our timing folds in the
  (small, ~constant) return path of a tiny ACK packet. We use ACK **spacing**
  (a difference), so the constant return delay cancels and the *variation* is
  dominated by the forward path. The adaptive measurement-noise term (§6.2)
  absorbs the residual jitter. S4 is a coarser cross-check.
- **S5 is gold for gating.** A rising `BytesQueuedForSend` is an unambiguous
  "you are feeding the socket faster than it drains" signal and its drain rate
  while backlogged is a near-direct capacity reading. It costs one Steam call per
  client per sampling interval (poll every N ticks, not every tick).
- **`UsingRelay`** (S5) flags clients routed through Valve relays — typically
  lower and more variable capacity. The estimator handles this automatically
  (it's measurement-driven) but it's worth surfacing in telemetry to explain why
  two clients differ wildly.

---

## 5. Client identity, keying, and lifecycle

**Replication's own key.** `MyReplicationServer` stores clients in
`ConcurrentDictionary<Endpoint, MyClient>` (`MyReplicationServer.cs:71`).
`Endpoint` wraps an `EndpointId` whose `Value` is the **Steam ID** (ulong). Its
own doc warns: *"EndpointId is not guid and can change when client reconnects"*
(`EndpointId.cs:6–9`). Steam session state additionally exposes the literal
`RemoteIP`/`RemotePort` (S5) if a raw IP:port key is preferred.

**Connection key (recommended).**

```
ClientConnectionKey = (ulong SteamId /* EndpointId.Value */, uint ConnectionEpoch)
```

- `ConnectionEpoch` is a process-global counter incremented every time the
  replication layer creates a `MyClient` (hook `AddClient`, `MyReplicationServer.cs:237`).
- Consequence: **reconnection of the same Steam user yields a new key → a fresh
  estimator**, exactly as required. The live `MyClient` instance is the source of
  truth for "which epoch is current"; we attach the epoch to the `MyClient`
  (ConditionalWeakTable) so every hook can resolve `MyClient → key` in O(1).
- Optionally also record `RemoteIP:RemotePort` from S5 as a human-readable label
  for logs/telemetry.

**Storage.**

```
ConcurrentDictionary<ClientConnectionKey, ClientBandwidthEstimator>  Registry   // process-lifetime
```

- Entries are **not evicted on disconnect** (the user wants estimates kept for
  the process lifetime). On disconnect (hook `RemoveClient`) we mark the entry
  `Closed` and freeze its last estimate for later inspection; a soft cap (e.g.
  keep the most recent K=512 closed connections, LRU) bounds memory if a server
  churns thousands of joins. Live entries are unbounded but tiny.
- A reconnecting user simply gets a new live entry; the old one stays as history.

**Threading.** The send path (`SendUpdate`/`FilterStateSync`/`SendStateSync`),
the ACK path (`OnClientAcks`), and the inbound path (`ProcessIncomingPacket`) all
run on the **engine update (main) thread** — the thread handed to
`MyReplicationServer`'s ctor. So all *mutation* of an estimator is single-threaded;
no locks needed on the hot path. Only **telemetry readers** (Quasar/log exporters
on another thread) need synchronization — publish an immutable snapshot
(`volatile` reference swap or a tiny lock) per estimator per export interval.

---

## 6. The estimator

Per direction we run a small **Kalman core** plus a controller. The downlink is
the full design; the uplink reuses the timing filter for diagnostics (§6.5).

### 6.1 State and the two filters at a glance

```
              per-packet (L_i, S_i, A_i)          windowed goodput g
                         │                                │
              ┌──────────▼───────────┐         ┌──────────▼───────────┐
              │ Filter 1 (timing)    │  gate   │ Filter 2 (throughput)│
              │ 2-state delay Kalman │────────▶│ scalar capacity      │
              │ x=[1/C , m]          │         │ Kalman, measurement  │
              │ → Ĉ_delay, m̂        │         │ valid only when      │
              └──────────┬───────────┘         │ saturated → Ĉ_rate   │
                 overuse │ detector            └──────────┬───────────┘
                  (m̂ vs ±γ adaptive)                      │
                         └──────────────┬─────────────────┘
                                        ▼
                         AIMD operating-rate controller + probe
                                        ▼
                    R_target (bytes/s), Ĉ_fused, confidence
```

- **Filter 1** turns *packet timing* into (a) a direct capacity reading and
  (b) a queuing-delay signal `m̂` for the overuse detector. This is the
  "Kalman filter over packet timings" the requirement calls for, modeled on
  Google Congestion Control (Holmer et al., the WebRTC delay-based controller).
- **Filter 2** turns *bytes delivered* into a capacity reading, but only trusts
  the measurement when we're actually saturating the link.
- The **controller** fuses them into the single number the limiter consumes,
  `R_target`, and periodically probes upward to keep the estimate honest under
  throttling.

### 6.2 Filter 1 — delay-gradient (arrival-time) Kalman filter

For each acked packet (or each per-tick *burst* of packets, treated as one
sample to cut cost), with send size `L_i`, send time `S_i`, ack time `A_i`:

Measurement (one-way delay variation, via ACK spacing):

```
d_i = (A_i − A_{i−1}) − (S_i − S_{i−1})
```

Model — delay variation = transmission-time delta (frame-size change ÷ capacity)
+ network queuing offset + noise:

```
d_i = (L_i − L_{i−1})/C_i + m_i + v_i
state    x_i = [ 1/C_i , m_i ]ᵀ        (inverse capacity, queuing-delay offset)
H_i = [ (L_i − L_{i−1}) , 1 ]
process  x_i = x_{i−1} + w_i ,  w_i ~ N(0, Q),  Q = diag(q_c, q_m)
measure  d_i = H_i x_i + v_i ,  v_i ~ N(0, R_i)
```

Standard Kalman update:

```
predict   x⁻ = x_{i−1};            P⁻ = P_{i−1} + Q
innovate  y_i = d_i − H_i x⁻
          s_i = H_i P⁻ H_iᵀ + R_i
gain      K_i = P⁻ H_iᵀ / s_i
update    x_i = x⁻ + K_i y_i
          P_i = (I − K_i H_i) P⁻
readouts  Ĉ_delay = 1 / max(x_i[0], ε)        // direct capacity estimate
          m̂_i     = x_i[1]                     // queuing-delay signal
```

- `q_c` (capacity drift) small — capacity moves slowly; `q_m` larger — queuing
  moves fast. `R_i` is **adapted online** from an EWMA of the residual variance
  of `d_i` (GCC's `var_v`), so jittery links automatically get a looser filter
  and don't false-trigger.
- Guard `x_i[0] > ε > 0` so capacity stays positive and finite.

**Overuse detector (adaptive threshold γ).** Drive a 3-state signal from `m̂_i`:

```
if  m̂_i >  γ_i  sustained (time/count) →  OVERUSE     // queue building: above capacity
elif m̂_i < −γ_i                        →  UNDERUSE    // queue draining: headroom exists
else                                    →  NORMAL

γ_i = clamp( γ_{i−1} + Δt·k_γ(|m̂_i| − γ_{i−1}),  γ_min, γ_max )
      with k_γ larger when |m̂_i| > γ (grow fast) than when shrinking (decay slow)
```

This is exactly GCC's adaptive-threshold trick: γ widens under jitter to suppress
false positives, narrows on quiet links to stay sensitive.

### 6.3 Filter 2 — gated throughput Kalman filter (scalar)

> **Under spiky game traffic, prefer the BBR-style max/min capacity filter
> (§6.7).** This mean-tracking Kalman is retained as an *alternative* for when a
> uniform Kalman framework is wanted; on bursty, intermittently-observable load a
> windowed max-filter tracks capacity better (see §6.7 and Appendix A).

Over a window `W` (e.g. 250–500 ms or N ticks) compute achieved goodput
`g = bytes_acked_in_W / duration_W`. Capacity `C` as a random walk:

```
predict   C⁻ = C_prev;   P⁻ = P + Q_C
measurement validity = SATURATED, defined as any of:
    • overuse detector ∈ {OVERUSE, NORMAL} during W, AND
    • Steam BytesQueuedForSend > 0 at some sample in W  (S5), OR
    • IsAckAvailable() was false at some tick in W       (S3, window stalled), OR
    • offered_load_in_W ≥ 0.95 · R_target               (we actually pushed the budget)
if SATURATED:   K = P⁻/(P⁻+R_small);  C = C⁻ + K(g − C⁻);  P = (1−K)P⁻
else:           // g is only a LOWER bound — never let an under-send drag C down
                if g > C:  C unchanged (we learned nothing about the ceiling)
                else:      C unchanged; rely on controller's probe to re-test
readout   Ĉ_rate = C
```

The asymmetric handling is the whole point: **throughput can confirm capacity
only when we were trying to use it.**

### 6.4 Fusion + AIMD operating-rate controller (with probing)

Maintain `R_target` (bytes/s) — the value the limiter (§7) consumes. Blend the
two capacity readings by inverse-variance:

```
Ĉ_fused = (Ĉ_delay/Pδ + Ĉ_rate/Pr) / (1/Pδ + 1/Pr)
confidence = 1 / (1/Pδ + 1/Pr)       // exported alongside the estimate
```

Each control interval (e.g. every 100–250 ms):

```
if  OVERUSE  or  ACK-window stalled  or  BytesQueuedForSend rising:
        R_target ← β · min(R_target, Ĉ_fused)        // multiplicative decrease, β≈0.85
elif NORMAL:
        R_target ← min(R_target + α·Δt,  η · Ĉ_fused) // additive increase, α≈few KB/s²,
                                                       // η≈1.0–1.05 (gentle over-probe)
elif UNDERUSE:
        hold (or very gentle increase)                // queue draining; don't chase

R_target ← clamp(R_target, R_floor, R_max)

// Periodic headroom probe (defeats the closed-loop blind spot):
every T_probe seconds, for one short interval raise the bucket ceiling to
(1+p)·R_target (p≈0.08). If delay/queue responds (→ OVERUSE) back off and record
Ĉ_fused; if not, ratchet R_target up. Suspend probing under known stress.
```

Initialization (per direction): `R_target₀` = conservative prior (config, e.g.
256 KB/s); `Ĉ` priors with **large P** so the filters converge within a few
seconds; γ at `γ_min`.

### 6.5 Uplink (client → server)

Same Filter-1 math but fed from inbound packets (S6): server-clock arrival times
`recv_i` and inbound sizes give `d_i = (recv_i − recv_{i−1}) − (sent-by-client
spacing)`. We don't have the client's send timestamps precisely, but inbound
inter-arrival jitter + throughput still yields a usable `Ĉ_up` and an overuse
signal. **No AIMD controller** on this side — the server doesn't pace the client.
`Ĉ_up` is exported for diagnostics and future use (e.g. asking a client to lower
its position-update cadence, or detecting an uplink-starved client). Keep it
simple: Filter 1 + throughput, no probe.

### 6.6 Per-estimator state (memory footprint)

```
struct DirectionState {           // ~ 80–120 bytes
    double invC, m;               // x
    double P00,P01,P10,P11;       // P
    double varV, gamma;           // adaptive R, adaptive threshold
    double C_rate, P_rate;        // Filter 2
    double R_target;              // controller output (down only)
    long   lastSendTs, lastAckTs; // timing bookkeeping
    int    lastSize;
    detector state, counters, probe phase…
}
class ClientBandwidthEstimator {
    ClientConnectionKey Key;  bool Closed;  bool UsingRelay;  string RemoteEndpoint;
    DirectionState Down, Up;
    Snapshot PublishedSnapshot;   // immutable, for telemetry readers
}
```

A few hundred bytes per connection; trivial even for hundreds of clients.

### 6.7 Suitability for spiky game traffic (capacity-estimator choice)

GCC's Kalman was tuned for steady, paced media (WebRTC). SE state-sync is the
opposite: **demand is spiky** (idle → battle / PCU storm) while the client's
physical **capacity is comparatively stable**, so the hard part is *intermittent
observability* — we learn the ceiling only during organic bursts — not tracking a
fast-moving target. That reshapes the capacity estimator:

**Recommended capacity estimator — BBR-style max/min, not a mean Kalman.** Replace
§6.3's mean-tracking throughput Kalman (kept as an alternative) with windowed
extreme-filters, which fit bursty/intermittent load far better:

```
BtlBw  = max over a sliding window (≈6–10 RTTs) of delivery-rate samples,
         delivery_rate = bytes_acked / ack_interval  (per ack/burst),
         EXCLUDING app-limited samples   (= our "we under-sent" gate, §6.3)
RTprop = min over a longer window (≈10 s) of RTT samples
Ĉ_rate = BtlBw                  // feeds §6.4 fusion in place of the Kalman C
BDP    = BtlBw × RTprop          // the per-client window/rate ceiling (§7.6)
```

A max-filter *captures* the peak rate seen during a battle and *holds* it through
the following calm (low calm-period samples never beat the max), so it does not
regress toward idle throughput the way a mean-tracker does. BBR's "app-limited"
exclusion is exactly our under-send gate, so saturation gating falls out for free.

**Keep the Kalman where it shines — Filter 1 (delay gradient).** Smoothing a
noisy, continuous-ish gradient is its strength, and its output drives the
**burst-robust AIMD loop** (§6.4) — which, like any congestion-control law,
handles bursty workloads by construction. Harden it for non-stationary load:

- **Innovation-gated outlier rejection** (Mahalanobis / Huber): a single GC pause,
  scheduler hiccup, or relay glitch must not yank `m̂`.
- **Change-point detection**: on a sustained residual shift (a real link change —
  Wi-Fi degrading, the client's line newly saturated by other apps) inflate
  process noise `Q` / partially reset, so the filter re-converges instead of
  lagging the step.
- **Disambiguate the window stall** (§2.2): count *only* "delay rising while
  `IsAckAvailable` and the window is **not** full" as a capacity signal; a
  window-full stall is the 6-packet in-flight cap, not the bottleneck.

**Reframe for the limiter's goal.** Avoiding lag spikes rides on the **fast
delay/queue → AIMD back-off**, not on a precise capacity number. We do not need an
accurate `Ĉ` to stay out of trouble — we need to *react fast* when the gradient or
`BytesQueuedForSend` starts rising. The capacity estimate is mostly for telemetry,
the BDP ceiling, and headroom probing; treat its absolute accuracy as secondary.

See **Appendix A** for the full rationale.

---

## 7. Limiting outgoing bandwidth to each client

The estimator outputs `R_target` (bytes/s, downlink). Below are the candidate
mechanisms to *enforce* it, mapped to real SE knobs, with a recommendation and
how they compose. All attach to the §2.1 pipeline.

### 7.1 O1 — Per-client token bucket at `FilterStateSync` (recommended)

Replace the fixed `num2 = 7` packet counter with a **byte token bucket** per
client, refilled each tick at `R_target/60` bytes (capped to a burst ceiling),
and dequeue state groups until the bucket can't afford the next packet.

```
// patched FilterStateSync loop:
bucket.Refill(R_target / TICK_HZ, burstCeiling);     // tokens in bytes
while (queue has entries && bucket.tokens ≥ MTU && client.IsAckAvailable()) {
    entry = DirtyQueue.Dequeue();
    sent  = client.SendStateSync(entry, mtu, ref data, ts);   // returns bytes written
    bucket.Spend(sent);
}
```

- **Pros:** smooth pacing, burst control, single attach point, composes with the
  existing ACK window (which remains the hard in-flight cap), no protocol change.
  Directly satisfies "use as much bandwidth as available without spiking."
- **Cons:** must measure actual bytes per `SendStateSync` (already known there);
  needs care so a bucket can still always afford at least one packet occasionally
  (anti-starvation floor) and so high-priority/controlled-entity groups bypass the
  bucket (they already get priority 1 in `ScheduleStateGroupSync`).
- **Burst ceiling** = small multiple of `R_target/60` (e.g. 3–5 ticks' worth) so a
  momentarily idle client can catch up without a spike.

### 7.2 O2 — Dynamic per-client packet budget

Keep SE's packet-counting structure but make the cap per-client and dynamic:
`num2 = clamp(round(R_target/60 / MTU), 1, hardMax)`. The minimal-diff version of
O1 (integer packets instead of bytes). Coarser (granularity = one ~1190 B
packet), but a one-line change to the existing loop. Good **phase-1 stopgap**;
upgrade to O1's byte bucket once validated.

### 7.3 O3 — Per-client send-interval / LOD scaling

Replication already spreads updates over distance **layers** with `SendInterval`
(4, 8, 16, …) built by `MyLayers.SetSyncDistance`, and schedules each group
`SendInterval` frames out in `ScheduleStateGroupSync` (`MyReplicationServer.cs:934`).
Introduce a **per-client interval multiplier** `k_client ≥ 1` driven by how far
`offered demand` exceeds `R_target`: when a client is over budget, stretch its far
-layer intervals (`SendInterval ← SendInterval · k_client`) so distant entities
update less often. Reduces *sustained* load by lowering update frequency rather
than truncating a tick.

- **Pros:** degrades gracefully and semantically (far things update slower);
  reduces work, not just bytes.
- **Cons:** coarse, affects perceived smoothness of distant entities; interacts
  with the global `MyLayers` config. Best as a **secondary** lever layered under
  O1 for clients that are persistently over budget.

### 7.4 O4 — Join / streaming pacing

The initial world download and large replicables use the **streaming** path
(separate packet ids, 1 MB fragmentation in `MyNetworkWriter`). A fresh join can
dump a lot fast. Gate the streaming feeder on the *same* `R_target` (a second,
larger token bucket, since streaming is reliable and less latency-sensitive) so
the join doesn't saturate the link and spike latency for already-connected play.
Largely independent of O1 and worth doing for join-time smoothness.

### 7.5 O5 — Sync distance / PCU (coarse, last resort)

`MyLayers.SetSyncDistance(distance)` and per-client `PCULimit` reduce *what* is
replicated at all. Heavy-handed and player-visible; only consider as an automatic
"emergency brake" for a chronically starved client (e.g. a satellite link), and
even then prefer O3 first. Mentioned for completeness.

### 7.6 The ACK-window interaction (how to *raise* throughput)

Because in-flight data is capped at 6 packets (~7 KB), O1–O3 can freely
**reduce** a client's rate, but they **cannot push past `window ÷ RTT`** for a
high-RTT client (§2.2). To actually use spare capacity on a fat low-RTT pipe you
must widen the ACK window (`IsAckAvailable`'s `- 6`, `m_pendingStateSyncAcks`
sizing in `MyClient`) *per client, driven by the estimate*. That is a deeper,
riskier change (more in-flight = more retransmit/reorder exposure on the
unreliable channel) and is explicitly **out of scope for phase 1**. The estimator
is built to support it later: a high `Ĉ_fused` with `NORMAL`/`UNDERUSE` and low
RTT is precisely the green light to grow the window.

### 7.7 Over-budget policy: defer first, drop unreliable only to stop a spike

When `R_target` is below offered demand, *something* must give. The lever depends
on the traffic's **reliability class** (§2.3): reliable traffic is never dropped;
only unreliable state sync is. Escalate in this order:

1. **Defer low-priority unreliable groups (primary, lossless).** Leave them in
   `DirtyQueue`, rescheduled to a later frame — the queue orders by `Priority`,
   and `FilterStateSync` already reschedules anything still `IsStillDirty`. They
   coalesce with future dirties, so the client gets the *latest* value, not a
   backlog of stale ones. This is the everyday pacing tool.
2. **Protect reliable + controlled traffic.** The controlled entity and
   high-priority groups (priority 1) and the whole **reliable** class — streaming
   and reliable events (§2.3) — bypass the bucket and are never dropped; they may
   only be paced (O4) or allowed to back-pressure.
3. **Drop unreliable state sync (last resort, transient only).** If the link is
   *genuinely* backed up for a short period — Steam's `BytesQueuedForSend` is
   climbing and/or the delay gradient (Filter 1) is rising, i.e. deferral alone
   isn't keeping the queue bounded — stop carrying the lowest-priority unreliable
   groups forward: drop the update outright instead of rescheduling it. This is
   safe *only because* the class is unreliable and last-value (§2.3) — the
   entity's next dirty, or an ACK-window resend, restores the current value. It is
   a deliberate **compromise**: a one-shot change to an otherwise-quiet entity can
   stay stale until its next change. So it is gated hard — **only** the unreliable
   class, **only** while the spike signal is present, bounded in duration, and
   never for controlled/high-priority/reliable groups. The control point is
   **before enqueue**: simply don't serialize/reschedule the group in
   `FilterStateSync`. Once bytes are handed to Steam they sit in
   `BytesQueuedForSend` and cannot be selectively pruned — so shedding must happen
   at the `DirtyQueue`, not the socket.
4. **Stretch far-layer intervals (O3)** if a client is *persistently* over budget
   rather than spiking — a steadier, less lossy way to cut sustained load than
   repeated dropping.
5. **Emergency (O5)** only if chronically starved.

This converts "queue grows unbounded → latency spike" into "distant/low-priority
state updates a little less often — and, briefly, a few unreliable updates are
skipped — → latency stays flat," which is the entire goal. **Defer is the everyday
tool; dropping is reserved for actually averting a spike**, exactly as it should
be: a compromise spent only when required.

### 7.8 Recommendation

- **Phase 1 limiter:** O2 (dynamic packet budget) to validate the loop with a
  minimal diff, then O1 (token bucket) for smooth pacing — both gated by the §7.7
  defer policy. O4 for join smoothness.
- **Phase 2:** add O3 per-client interval scaling for persistently constrained
  clients.
- **Later / optional:** O5 emergency brake; window-widening (§7.6) to raise the
  ceiling for fat pipes.

---

## 8. Integration with Magnetar

This estimator ships as a **Magnetar server plugin**: Magnetar loads it and it
applies **Harmony** patches against SE types early, before world load, so capture
covers the whole client session. The estimator is a small set of patches feeding
a singleton registry (`BandwidthMonitor`), plus (for the limiter, future phases)
patches on the send loop. Nothing here needs client-side changes.

**Hook points.**

| Purpose | Target (decompiled) | Patch kind |
| --- | --- | --- |
| Connection open → new epoch/estimator | `MyReplicationServer.AddClient` (`:237`) | Postfix |
| Connection close → mark Closed | `MyReplicationServer.RemoveClient` | Postfix |
| Capture send size + send time (S1) | `MyClient.SendStateSync` (`:574`) | Postfix (read bytes written + `serverTimeStamp`) |
| Capture ACK + ack time (S2/S3) | `MyClient.OnClientAcks`/`OnAck` (`:389/:435`) | Postfix |
| RTT/ping cross-check (S4) | read `MyClientStateBase.Ping` each tick | read-only |
| Steam queue backlog (S5) | call `Peer2Peer.GetSessionState` every N ticks | read-only |
| Uplink samples (S6) | `MyClient.ProcessIncomingPacket` (`:262`) | Postfix |
| **Limiter** (O1/O2) | `MyReplicationServer.FilterStateSync` (`:850`) | Transpiler/Prefix replacing the `num2`/loop |
| **Join pacing** (O4) | streaming feeder (`SendStreamingEntry` path) | Prefix gate |

**Cadence.** All capture hooks fire on the engine update thread inside the normal
60 Hz `SendUpdate`. The controller (§6.4) runs on a coarser sub-cadence
(every ~6–15 ticks) to keep cost negligible. S5 polling is throttled (e.g. once
per 0.5 s per client).

**Failure isolation.** Every hook wraps its body in try/catch and logs via the
plugin's `Common.Logger`, so a bug in estimation or pacing can never break
replication — on any exception the capture is skipped, and the limiter's packet-budget
hook returns the stock 7 on any error (and is a no-op transpiler when `LimiterMode = Off`),
so SE's stock behavior is unaffected.

---

## 9. Configuration

Expose through a `PluginConfig`-derived class (Magnetar's standard config
mechanism, remotely manageable via Quasar). The table below is the **full design's**
suggested knob set; the **as-built** config (the subset this build implements, with
`LimiterMode` defaulting to `Off`) is documented in
[`BandwidthTelemetry.md`](BandwidthTelemetry.md) §4. Suggested knobs with defaults:

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Master switch (off → stock SE behavior). |
| `LimiterMode` | `TokenBucket` | `Off` / `PacketBudget` (O2) / `TokenBucket` (O1). |
| `RatePriorMin..Max` | 32 KB/s .. 512 KB/s | `R_floor`, `R_max` clamps. |
| `RatePrior0` | 256 KB/s | Initial `R_target`. |
| `BurstCeilingTicks` | 4 | Token-bucket burst depth in ticks. |
| `DropUnreliableUnderSpike` | `true` | Allow §7.7 step 3: shed unreliable state sync to avert a queue-driven spike (defer still tried first). |
| `DropBacklogTrigger` | tuned | `BytesQueuedForSend` / delay-gradient level at which the policy escalates from defer to drop. |
| `AimdBeta` / `AimdAlpha` | 0.85 / 64 KB/s² | Decrease/increase gains. |
| `ProbePeriod` / `ProbeGain` | 5 s / 0.08 | Headroom probe cadence/magnitude. |
| `Kalman.qC` `qM` | tuned | Process noise (capacity drift / queuing). |
| `Gamma.Min/Max` | tuned | Overuse-threshold bounds. |
| `SteamPollTicks` | 30 | S5 sampling interval. |
| `KeepClosedConnections` | 512 | History cap for disconnected clients. |
| `TelemetryEnabled` | `true` | Emit per-client estimates to logs/Quasar. |

Ship conservative defaults; the limiter should be **safe at default** (never
worse than stock), and aggressive tuning opt-in.

---

## 10. Telemetry and validation

- **Per-client snapshot** (published each interval). The full design snapshot is
  `R_target`, `Ĉ_down`, `Ĉ_up`, confidence, current RTT/ping, `BytesQueuedForSend`,
  `UsingRelay`, detector state, bytes sent/acked last second, `DirtyQueue` depth,
  defer count, unreliable-drop count (§7.7 step 3). **This build publishes the subset
  it actually produces** — per-direction achieved throughput, the windowed-max capacity
  estimate, confidence, connection facts, and (Phase 2) the limiter's operating target
  and packet budget — through the **Magnetar PluginSdk Stats API** under the `bandwidth`
  provider. The exact
  contract (stat groups, fields, units) and how a consumer such as the Quasar Agent
  reads it are in **[BandwidthTelemetry.md](BandwidthTelemetry.md)**. Magnetar's
  structured logging (`Logger`/`QuasarLogSink`) remains a secondary, human-readable
  path.
- **Cross-checks:** compare summed per-client bytes against
  `UpdateStatisticsData(outgoing,…)` (S7) and `MyTransportLayer`'s sliding windows
  to confirm the estimator's accounting matches reality.
- **Validation experiments:**
  1. **Shaped link** (e.g. `clumsy`/`tc netem` on a test client): cap to a known
     rate + added delay; verify `Ĉ_down` converges near the cap and `R_target`
     tracks it without oscillation.
  2. **Burst test:** trigger a large dirty-state burst (mass block change / battle);
     confirm `DirtyQueue` depth and p99 update latency stay **bounded** with the
     limiter on vs. unbounded growth with it off.
  3. **A/B:** stock vs. estimator-paced on the same world/clients; compare ping
     stability (variance/spikes) and perceived smoothness. Success = **flat
     latency under load** at equal or better throughput.
  4. **Reconnect:** verify a reconnect creates a new key/epoch and a fresh estimate.

---

## 11. Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| **Closed-loop blindness** — throttling hides true capacity | Saturation gating (§6.3) + periodic probe (§6.4); never lower `C` on an under-send. |
| **ACK-spacing ≠ one-way delay** (return path folded in) | Use spacing (constant cancels) + adaptive `R`/`γ`; S4 cross-check. |
| **Relay/Wi-Fi jitter → false overuse** | Adaptive threshold γ widens under jitter; require *sustained* overuse before decrease. |
| **Limiter starves a client / hurts gameplay** | Anti-starvation floor; protect controlled-entity & high-priority groups; safe defaults; master switch. |
| **Dropping unreliable updates → stale state** | Only the unreliable, last-value class (§2.3); deferral tried first; drops gated to transient spikes and bounded; next dirty / ACK resend reconverges; reliable & controlled traffic never dropped. |
| **Can't exceed window/RTT ceiling** | Acknowledged as out of scope; estimator is a down-regulator in phase 1 (§2.2, §7.6). |
| **Hook breaks replication** | try/catch + fallback to stock 7-packet budget; per-patch kill switch. |
| **Thread-safety** | Mutation confined to update thread; telemetry via published snapshots. |
| **Memory growth on churn** | `KeepClosedConnections` LRU cap; live state is tiny. |
| **SE version drift** (offsets/names change) | Patches target stable method names; verify against `se-dev-server-code` on each SE update; the doc cites types, not raw offsets. |

---

## 12. Phased implementation plan

1. **Observe-only.** ✅ *Implemented.* Registry + keying (§5) + capture hooks,
   windowed-max delivery-rate estimate (§6.7). Telemetry shipped (§10). *No behavior
   change.*
2. **Limiter (minimal).** ✅ *Implemented (opt-in, default off).* O2 dynamic packet
   budget driven by the §6.4 AIMD controller, with the ACK-window stall as the overuse
   signal (in place of the §6.2 Kalman delay filter), gated by §7.7 deferral. Behind
   `LimiterMode = PacketBudget`. Deferral-only — no unreliable dropping.
3. **Limiter (smooth).** O1 token bucket + O4 join pacing.
4. **Adaptive LOD.** O3 per-client interval scaling for persistent over-budget.
5. **(Optional) Raise ceiling.** Estimate-driven ACK-window widening (§7.6) for
   fat low-RTT pipes — separate review, separate risk budget.

Each phase is independently shippable and reversible via config.

---

## 13. References

- **SE replication internals** (read via the `se-dev-server-code` skill, paths
  under `Data/Decompiled/`): `VRage/VRage/Network/MyReplicationServer.cs`,
  `MyClient.cs`, `MyClientStateBase.cs`, `EndpointId.cs`;
  `Sandbox.Game/Sandbox/Engine/Multiplayer/MyTransportLayer.cs`,
  `MyMultiplayerBase.cs`; `Sandbox.Game/Sandbox/Engine/Networking/MyNetworkWriter.cs`;
  `VRage.Steam/VRage/Steam/MySteamPeer2Peer.cs`.
- **Plugin capture patches:** [`ServerPlugin/Patches/`](../ServerPlugin/Patches)
  — `Patch_NetworkWriterSendPacket` (down), `Patch_TransportLayerProcessMessage`
  (up), `Patch_ReplicationAddClient` / `Patch_ReplicationClientLeft` (lifecycle).
- **Plugin limiter (Phase 2):** `Patch_FilterStateSyncBudget` (O2 budget transpiler
  on `MyReplicationServer.FilterStateSync`), `Patch_ClientAckAvailable` (ACK-stall
  signal postfix), `ServerPlugin/Network/RateController.cs` (AIMD controller),
  `ServerPlugin/Network/BandwidthLimiter.cs` (budget façade).
- **Google Congestion Control** (delay-based Kalman estimator + adaptive overuse
  detector + AIMD), Holmer et al. — the model Filter 1 + §6.2/§6.4 are adapted
  from. The newer libwebrtc "trendline" variant is a drop-in alternative to the
  Kalman delay filter if the 2-state filter proves noisy in practice.
- **BBR** (probe-for-bandwidth, delivery-rate sampling) — rationale for the
  saturation-gated throughput filter and the periodic headroom probe.

---

## Appendix A — Does the Kalman filter suit spiky game traffic?

GCC/Kalman was tuned for WebRTC: a codec emitting a **continuous,
smoothly-throttleable, near-stationary** stream, so the estimator always has a
packet train to measure and a knob (bitrate) to turn. Two of those properties
fail for SE state-sync.

**Why game traffic breaks the steady-media assumption**

1. **It is not capacity that is spiky — it is *demand*, and therefore
   *observability*.** A client's physical link is fairly stable second to second;
   what spikes is offered load (idle → battle / PCU storm). Capacity information
   arrives only in bursts, and during calm periods the signal is near zero. A
   mean-tracking Kalman run continuously will **regress toward the low calm-period
   throughput** unless perfectly gated, and its delay-gradient samples (from
   sparse, irregular bursts) turn noisy and uninformative.
2. **Tick-quantized bursts + the 6-packet window confound the gradient.** SE sends
   in 60 Hz lumps, not a paced stream, and much of the "delay rising" signal during
   a burst is the **ACK window stalling** (the in-flight cap), not the bottleneck
   link queuing. A filter that cannot separate "window-full stall" from "bytes
   queuing while the window is not full" misreads self-inflicted, window-limited
   stalls as capacity overuse.

**What still holds (so it is not fatal)**

- The quantity we estimate is **slow and stable** — the *easy* regime for a
  filter. The trick is holding the estimate through calm, not chasing a fast
  target.
- **Spikiness is useful**: every battle is a free, organic capacity probe, so we
  need *less* synthetic probing than WebRTC, not more.
- For the limiter's real goal — *do not grow the queue* — the workhorse is the
  fast delay/queue → AIMD loop, and AIMD on a back-off signal is exactly how
  TCP/BBR survive bursty workloads. An *accurate* capacity number is not required
  to avoid spikes; a *fast back-off* when delay/queue rises is.

**The adaptations that make it work**

1. **Event-driven, not always-on.** Update capacity only from saturated intervals
   (§6.3 gate); during calm, freeze and slowly widen variance rather than
   integrate noise.
2. **Prefer max/min filters over a mean-tracker for capacity** (§6.7): a BBR-style
   windowed *max* delivery-rate (BtlBw) + *min* RTprop captures organic-burst peaks
   and holds them through calm, with BBR's app-limited flag mapping onto our
   under-send gate. `BDP = BtlBw × RTprop` feeds the window/rate ceiling.
3. **Keep the Kalman for Filter 1 (delay gradient)** — its strength — and add
   innovation-gated outlier rejection (Mahalanobis / Huber) and change-point
   detection (inflate `Q` / reset) for genuine link changes.
4. **Disambiguate the window stall**: only "delay rising while `IsAckAvailable`
   and the window not full" is a true capacity signal.

**Bottom line.** Keep the Kalman for the delay-gradient detector — it is
well-suited there and feeds the burst-robust controller — but for the *capacity*
estimate under spiky load, lean on a gated BBR-style max/min filter rather than a
mean-tracking Kalman, and remember that spike-avoidance rides on the fast AIMD
loop, not on a precise capacity figure.

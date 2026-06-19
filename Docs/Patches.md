# Harmony patches and stat schemas

The plugin touches the game through six Harmony patches in
[`ServerPlugin/Patches/`](../ServerPlugin/Patches/) and publishes through two stat schemas in
[`ServerPlugin/Stats/`](../ServerPlugin/Stats/). All patches are applied **before world load**
from the early bootstrap ([ServerLifecycle.md](ServerLifecycle.md)); each one is wrapped so a
failure logs and leaves the game's behaviour unchanged.

## Patch overview

| Patch | Target | Kind | Feature | Always on? |
| --- | --- | --- | --- | --- |
| [`Patch_NetworkWriterSendPacket`](../ServerPlugin/Patches/Patch_NetworkWriterSendPacket.cs) | `MyNetworkWriter.SendPacket` | Prefix | DOWN byte capture | yes (measurement) |
| [`Patch_TransportLayerProcessMessage`](../ServerPlugin/Patches/Patch_TransportLayerProcessMessage.cs) | `MyTransportLayer.ProcessMessage` | Prefix | UP byte capture | yes (measurement) |
| [`Patch_ReplicationAddClient`](../ServerPlugin/Patches/Patch_ReplicationAddClient.cs) | `MyReplicationServer.AddClient` | Postfix | connection open | yes (lifecycle) |
| [`Patch_ReplicationClientLeft`](../ServerPlugin/Patches/Patch_ReplicationClientLeft.cs) | `MyReplicationServer.OnClientLeft` | Postfix | connection close | yes (lifecycle) |
| [`Patch_ClientAckAvailable`](../ServerPlugin/Patches/Patch_ClientAckAvailable.cs) | `MyClient.IsAckAvailable` | Postfix | ACK-stall signal | feeds limiter |
| [`Patch_FilterStateSyncBudget`](../ServerPlugin/Patches/Patch_FilterStateSyncBudget.cs) | `MyReplicationServer.FilterStateSync` | Transpiler | packet-budget enforcement | enforces only when limiter on |

The last two enable the limiter ([Limiter.md](Limiter.md)); the first four power the
always-on measurement ([Measurement.md](Measurement.md)).

---

## Byte-capture patches

### DOWN — `Patch_NetworkWriterSendPacket`

A **prefix** on `MyNetworkWriter.SendPacket`. Every outgoing packet — state sync, streaming,
reliable events, voice — is enqueued here as a `MyPacketDescriptor` carrying its recipients,
reliability class and payload, so one prefix sees *all* server→client traffic. It computes an
approximate on-wire size (`PACKET_HEADER_SIZE + header.Position + Data.Size`) and attributes
it to each recipient's downlink via `RecordDown(steamId, bytes, reliable)`. Reliability is
`MsgType >= MyP2PMessageEnum.Reliable`. It reads the descriptor synchronously in the prefix —
before `SendAll` serializes and recycles it on the network thread. The few-byte framing
inexactness is fine for rate estimation.

### UP — `Patch_TransportLayerProcessMessage`

A **prefix** on the internal `MyTransportLayer.ProcessMessage(MyPacket)` (the inbound
demultiplex point, so it sees all client→server traffic). The type is internal and the method
private, so the target is given as an assembly-qualified string. It records
`p.Sender` + `BitStream.ByteLength`. Reading `ByteLength` returns the buffer length **without
advancing the read cursor**, so the game's own processing of the same stream is unaffected.

## Lifecycle patches

### `Patch_ReplicationAddClient`

A **postfix** on `MyReplicationServer.AddClient(Endpoint, MyClientStateBase)` — called once per
new client. It calls `OnClientConnected(endpoint.Id.Value)`, which bumps the reconnect epoch
and starts a fresh tracker, so a reconnect of the same Steam user is a new estimate
([`BandwidthEstimator.md`](BandwidthEstimator.md) §5). Applied before world load, so trackers
exist before any traffic.

### `Patch_ReplicationClientLeft`

A **postfix** on the public `MyReplicationServer.OnClientLeft` — calls
`OnClientDisconnected(endpointId.Value)` to drop the client from the live registry so it stops
appearing in snapshots. The per-Steam-ID epoch counter is retained so a later reconnect gets a
higher epoch.

## Limiter patches

### `Patch_ClientAckAvailable` — the overuse signal

A **postfix** on `MyClient.IsAckAvailable()`, called once per client at the top of
`FilterStateSync`. `__result == false` ⇒ the in-flight ACK window is exhausted (a stalled
serviced tick), forwarded to the client's `RateController` via `RecordAckObservation`.

Two important safeguards:

- `IsAckAvailable()` is **not side-effect free** — it sets the client's `m_waitingForReset`
  flag — so the plugin must *never call it itself*. The postfix only **observes** the game's own
  call through `__result`.
- `MyClient` is internal, so the target is resolved by name
  (`AccessTools.Method("VRage.Network.MyClient:IsAckAvailable")`) and `Prepare()` returns
  false (skipping the patch cleanly) if the method is gone on a future SE build — so this
  default-off feature never breaks the always-on telemetry.

### `Patch_FilterStateSyncBudget` — the enforcement point

A **transpiler** on the private `MyReplicationServer.FilterStateSync`. It finds the only
literal `7` in the method (`ldc.i4.7`, which initialises the per-client packet budget `int num2
= 7;`, verified against the decompiled DS) and replaces it with `ldarg.1` (the `MyClient`
argument) + `call BandwidthLimiter.PacketBudget(object)`, so the budget becomes per-client.

Defensive on two levels so a default-off feature can never break the server on an SE update:
`Prepare()` skips the whole patch if `FilterStateSync` is gone, and the transpiler returns the
**original IL unchanged** (with a critical log) if the `ldc.i4.7` anchor is absent. In both
cases replication stays at stock behaviour. The anchor is straight-line init code with no
labels targeting it, so mutating it in place is safe.

---

<a id="stat-schemas"></a>
## Stat schemas (`Stats/`)

The producers publish two POCO schemas, decorated with `PluginSdk.Stats` attributes
(`[Gauge]`, `[Discrete]`, `[StatLabel]`, with `Unit` / `AcrossInstances` / `OverTime`
aggregation hints). `BandwidthMonitor` captures instances of these against their inline schema
into each `StatsSnapshot`. The full field-by-field contract — meaning, units, and how a
consumer should fold each column — is in [`BandwidthTelemetry.md`](BandwidthTelemetry.md) §2;
this is just the code map.

### [`BandwidthServerStats`](../ServerPlugin/Stats/BandwidthServerStats.cs) — one `"server"` row

`Scope` (label), `ClientCount` (gauge), `TickRateHz` (discrete, Hz), `LimiterActive`
(discrete bool — outgoing pacing currently enforced).

### [`BandwidthClientStats`](../ServerPlugin/Stats/BandwidthClientStats.cs) — one row per client

`Client` (label: Steam ID or `anon-…` hash), `ConnectionEpoch`, the down/up
throughput + windowed-max estimate gauges, `DownConfidence`, `ConnectedSeconds`, and the two
limiter previews `TargetBytesPerSec` and `PacketBudget`. Per-client rate gauges default to
`Sum` across instances (so summing a column gives the server total); confidence is `Mean`;
`ConnectedSeconds` is per-connection (`None` across instances, `Max` over time).

These flow over Magnetar's generic `PluginSdk.Stats.PluginStats` hub under the provider name
`"bandwidth"` — see [`BandwidthTelemetry.md`](BandwidthTelemetry.md) for the transport and how
a consumer (e.g. the Quasar agent) reads it.

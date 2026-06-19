# Index — every source file

A flat map of every tracked source file to a one-line description and the page that documents
it. For a guided reading order, start at [`TOC.md`](TOC.md).

## ServerPlugin — the bandwidth plugin

### Lifecycle ([ServerLifecycle.md](ServerLifecycle.md))

| File | Description |
| --- | --- |
| [`ServerPlugin/Plugin.cs`](../ServerPlugin/Plugin.cs) | `IPlugin` entry point: early bootstrap, config load/plumbing, periodic publish, shutdown. |
| [`ServerPlugin/Preloader.cs`](../ServerPlugin/Preloader.cs) | Namespace-less preloader; its `Finish()` installs the before-world-load Harmony bootstrap. |

### Network — measurement ([Measurement.md](Measurement.md)) and limiter ([Limiter.md](Limiter.md))

| File | Description |
| --- | --- |
| [`ServerPlugin/Network/BandwidthMonitor.cs`](../ServerPlugin/Network/BandwidthMonitor.cs) | Process-lifetime registry of per-client trackers + the periodic snapshot publisher. |
| [`ServerPlugin/Network/ConnectionTracker.cs`](../ServerPlugin/Network/ConnectionTracker.cs) | One connection's state: the two directional trackers, the controller, identity/epoch. |
| [`ServerPlugin/Network/DirectionTracker.cs`](../ServerPlugin/Network/DirectionTracker.cs) | Per-direction BBR-style windowed-max rate estimator; lock-free hot path. |
| [`ServerPlugin/Network/RateController.cs`](../ServerPlugin/Network/RateController.cs) | Pure per-client AIMD operating-rate controller (ACK-stall brake, time probe). |
| [`ServerPlugin/Network/BandwidthLimiter.cs`](../ServerPlugin/Network/BandwidthLimiter.cs) | Packet-budget façade; `clamp(round((R_target/60)/MTU),1,7)`; reflection to read the Steam ID. |

### Patches ([Patches.md](Patches.md))

| File | Description |
| --- | --- |
| [`ServerPlugin/Patches/Patch_NetworkWriterSendPacket.cs`](../ServerPlugin/Patches/Patch_NetworkWriterSendPacket.cs) | Prefix on `MyNetworkWriter.SendPacket` — DOWN byte capture. |
| [`ServerPlugin/Patches/Patch_TransportLayerProcessMessage.cs`](../ServerPlugin/Patches/Patch_TransportLayerProcessMessage.cs) | Prefix on `MyTransportLayer.ProcessMessage` — UP byte capture. |
| [`ServerPlugin/Patches/Patch_ReplicationAddClient.cs`](../ServerPlugin/Patches/Patch_ReplicationAddClient.cs) | Postfix on `MyReplicationServer.AddClient` — connection open + epoch bump. |
| [`ServerPlugin/Patches/Patch_ReplicationClientLeft.cs`](../ServerPlugin/Patches/Patch_ReplicationClientLeft.cs) | Postfix on `MyReplicationServer.OnClientLeft` — connection close. |
| [`ServerPlugin/Patches/Patch_ClientAckAvailable.cs`](../ServerPlugin/Patches/Patch_ClientAckAvailable.cs) | Postfix on `MyClient.IsAckAvailable` — the AIMD overuse (stall) signal. |
| [`ServerPlugin/Patches/Patch_FilterStateSyncBudget.cs`](../ServerPlugin/Patches/Patch_FilterStateSyncBudget.cs) | Transpiler on `MyReplicationServer.FilterStateSync` — per-client packet budget. |

### Stats ([Patches.md#stat-schemas](Patches.md#stat-schemas) / [BandwidthTelemetry.md](BandwidthTelemetry.md))

| File | Description |
| --- | --- |
| [`ServerPlugin/Stats/BandwidthServerStats.cs`](../ServerPlugin/Stats/BandwidthServerStats.cs) | Single `"server"` stat row schema (client count, tick rate, limiter active). |
| [`ServerPlugin/Stats/BandwidthClientStats.cs`](../ServerPlugin/Stats/BandwidthClientStats.cs) | Per-client stat row schema (throughput, estimate, confidence, target, budget). |

### Config ([Configuration.md](Configuration.md))

| File | Description |
| --- | --- |
| [`ServerPlugin/Config/BandwidthConfig.cs`](../ServerPlugin/Config/BandwidthConfig.cs) | All admin knobs + the `BandwidthLimiterMode` enum; PluginSdk-managed, runtime-applied. |

## Shared and Client ([SharedAndClient.md](SharedAndClient.md))

| File | Description |
| --- | --- |
| [`Shared/Plugin/Common.cs`](../Shared/Plugin/Common.cs) | Holds the active plugin/logger/config/game-version/data-dir. |
| `Shared/Plugin/ICommonPlugin.cs` | The contract the plugin satisfies (`Log`, `Config`, `Tick`). |
| `Shared/Logging/*` | `IPluginLogger` + `PluginLogger` + `LogFormatter` — the logging façade. |
| `Shared/Config/IPluginConfig.cs` | Minimal shared config contract (`Enabled`). |
| `Shared/Config/PersistentConfig.cs`, `PluginConfig.cs` | Template config base + XML persistence (used by the client plugin). |
| [`Shared/Patches/PatchHelpers.cs`](../Shared/Patches/PatchHelpers.cs) | Verify-then-apply Harmony scaffolding; `Configure`/`PatchUpdates` are no-ops here. |
| `Shared/Tools/EnsureCode.cs`, `CodeChange.cs`, `Hashing.cs` | Game-method change detection after updates. |
| `Shared/Tools/TranspilerHelpers.cs`, `PreloaderHelpers.cs` | IL/transpiler and Cecil preloader helpers. |
| `Shared/Tools/GameAssembliesToPublicize.cs`, `IgnoresAccessChecksToAttribute.cs` | Krafs publicizer support (not enabled for this plugin). |
| `ClientPlugin/Plugin.cs` | Template client `IPlugin`; loads config, opens the settings dialog. Not bandwidth-related. |
| `ClientPlugin/Config.cs` | Demo config showing every settings control type. |
| `ClientPlugin/Settings/**` | The settings-dialog UI framework (Elements, Layouts, generator, tools). |

## Build and tooling ([Build.md](Build.md))

| File | Description |
| --- | --- |
| [`Bandwidth.sln`](../Bandwidth.sln) | The three-project solution. |
| [`Version.Build.props`](../Version.Build.props) | Single source of truth for the plugin version (committed). |
| [`Directory.Build.props.template`](../Directory.Build.props.template) | Template for the local, un-committed reference-path overrides + auto-detection. |
| `Directory.Build.targets` | Resolves `PulsarBin` (framework-dependent) after props import. |
| [`ServerPlugin/ServerPlugin.csproj`](../ServerPlugin/ServerPlugin.csproj) | Server build: multi-target net48/net10.0, DS + PluginSdk references, deploy hooks. |
| `ClientPlugin/ClientPlugin.csproj` | Client build. |
| [`setup.py`](../setup.py) | One-time setup: props copy, optional rename, Steam path auto-detection. |
| `ServerPlugin/Deploy.bat` / `Deploy.sh`, `ClientPlugin/Deploy.*` | Post-build copy into the Magnetar/Pulsar local plugin folders. |
| `verify_props.bat` / `verify_props.sh` | Pre-build sanity check of the resolved reference paths. |
| `Clean.bat` / `clean.sh` | Remove build outputs. |
| `BandwidthServer.xml` / `BandwidthClient.xml` | MagnetarHub/PluginHub registration manifests (still template `TODO`s). |
| `app.config` / `App.config` | Assembly binding redirects for the server/client. |
| `AGENTS.md`, `.clinerules`, `.github/copilot-instructions.md`, `.vscode/AGENTS.md` | AI-assistant guidance pointing at the SE dev skills. |

## Design documents (background, not code)

| File | Description |
| --- | --- |
| [`Docs/BandwidthEstimator.md`](BandwidthEstimator.md) | The full estimator/limiter design and rationale (13 sections + appendix). |
| [`Docs/BandwidthTelemetry.md`](BandwidthTelemetry.md) | The telemetry contract published to consumers. |

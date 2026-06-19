# Server plugin lifecycle

How the plugin loads, configures itself, applies its patches **before world load**, and
drives the periodic telemetry publish. Source: [`ServerPlugin/Plugin.cs`](../ServerPlugin/Plugin.cs)
and [`ServerPlugin/Preloader.cs`](../ServerPlugin/Preloader.cs).

## Why "before world load" matters

On a dedicated server the world finishes loading **before** `IPlugin.Init` runs. If the
network-capture patches were applied from `Init`, every packet exchanged during world load
and the first clients' joins would go uncounted. To cover all client traffic *from the very
first packet*, the plugin applies its patches as early as it safely can — before the game
even starts running.

## Startup sequence

```
Magnetar Preloader.Finish()              ← Preloader.cs (no namespace; found by name)
        └─ InstallEarlyBootstrap()       ← Harmony postfix on MyInitializer.InvokeBeforeRun
                                            (earliest point where filesystem + logging are ready)
MyInitializer.InvokeBeforeRun  ──postfix─► OnGameInitialized()
        └─ EarlyStartup()                ← one-shot, before world load
              ├─ LoadConfig()            ← Bandwidth.cfg (sparse XML) + PropertyChanged hook
              ├─ Common.SetPlugin(...)   ← shared state via a stand-in EarlyPlugin
              ├─ PushConfigToMonitor()   ← config → BandwidthMonitor.Configure(...)
              └─ HarmonyPatchUncategorized(...)   ← all capture/limiter patches applied here

IPlugin.Init(gameInstance)               ← runs later, after world load
        ├─ EarlyStartup()  (idempotent fallback if the preloader path didn't run)
        └─ Common.AttachPlugin(this)     ← swap the stand-in for the live instance (real Tick)

IPlugin.Update()  (every frame)
        └─ BandwidthMonitor.Update()     ← republishes once PublishIntervalMs has elapsed

IPlugin.Dispose()
        └─ BandwidthMonitor.Reset()      ← drop trackers, clear the published snapshot
```

### `Preloader.cs`

A top-level type with **no namespace** (Magnetar locates it via
`assembly.GetType("Preloader")`, which only works for a namespace-less type). It does no
Mono.Cecil pre-patching — it declares neither `TargetDLLs` nor a `Patch` method — and uses
only the `Finish()` post-hook, which Magnetar still runs because it counts post-hooks. From
`Finish()` it calls `Plugin.InstallEarlyBootstrap()`.

### `InstallEarlyBootstrap()` / `OnGameInitialized()`

`Finish()` runs before the game has initialised, so it cannot touch the filesystem or logger
yet. Instead it installs a Harmony postfix on `MyInitializer.InvokeBeforeRun` — the earliest
point that is *safe*, because that is where the game calls `MyFileSystem.Init` (so
`UserDataPath` exists) and assigns `MyLog.Default` (so the game logger works). When
`InvokeBeforeRun` later runs, the postfix `OnGameInitialized()` calls `EarlyStartup()`. The
postfix is deliberately **not** decorated with `[HarmonyPatch]`, so the normal patch scan
never re-applies it.

`OnGameInitialized` is wrapped: a failure sets the static `failed` flag and logs critically,
so the rest of the plugin degrades to inert rather than crashing the server.

### `EarlyStartup()`

One-shot (guarded by `earlyStarted`, both call sites are on the main thread). It:

1. `LoadConfig()` — see below.
2. `Common.SetPlugin(EarlyPlugin.Instance, gameVersion, UserDataPath)` — wires the shared
   `Common` accessors to a lightweight **`EarlyPlugin`** stand-in (its `Tick` is always 0,
   because there are no simulation ticks during world load).
3. `PushConfigToMonitor()` — pushes the loaded config into `BandwidthMonitor.Configure(...)`
   **before** the patches start feeding it, so the enable flag and tunables are honoured from
   the first captured packet.
4. `HarmonyPatchUncategorized(...)` — applies every patch in the assembly except the unused
   `"Late"` category. All bandwidth patches target assemblies loaded before world load
   (`Sandbox.Game`, `VRage`, `VRage.Network`), so there is nothing to defer to `Init`.

### `Init()`

Runs after world load. Calls `EarlyStartup()` again as an **idempotent fallback** (covers the
case where the preloader path did not run, e.g. Magnetar safe mode — coverage then starts from
`Init` rather than before world load, but the plugin still works), then
`Common.AttachPlugin(this)` swaps the live plugin instance in for the stand-in so per-tick code
reads the real `Tick` counter.

## Configuration plumbing

`LoadConfig()`:

- Resolves the path `…/<UserData>/Bandwidth.cfg`, normalised case-insensitively via
  `PathResolver.Normalize` (a no-op on Windows, the LinuxCompat resolver on Linux — one code
  path for both OSes).
- Loads it with `ConfigStorage.LoadXml<BandwidthConfig>` (a default-constructed instance when
  absent), then immediately re-saves so a fresh install leaves a **sparse** on-disk file (only
  non-default values).
- Subscribes `OnConfigChanged` to the config's `PropertyChanged`.

`BandwidthConfig` is a `PluginSdk.Config.PluginConfig`, so admins can also edit it **remotely
through Quasar** without a server restart. Any change (local or remote) raises
`PropertyChanged` → `OnConfigChanged` → `PushConfigToMonitor()` (re-applies to the running
monitor) + `TrySaveConfig()`. This is what makes enable/disable and re-tuning take effect
live. See [Configuration.md](Configuration.md) for the knobs.

`PushConfigToMonitor()` forwards every tunable to `BandwidthMonitor.Configure(...)` and logs a
warning if `LimiterMode == TokenBucket` (reserved; it falls back to the `PacketBudget`
limiter).

## Update and shutdown

- **`Update()`** runs `CustomUpdate()` each frame, which calls `BandwidthMonitor.Update()`
  (cheap; only republishes once the interval elapses) and `PatchHelpers.PatchUpdates()` (a
  no-op for this plugin — see [SharedAndClient.md](SharedAndClient.md)). In a release build the
  body is wrapped so one exception sets `failed` and disables further updates instead of
  spamming; in debug it is left to throw.
- **`Dispose()`** unsubscribes the config handler and calls `BandwidthMonitor.Reset()`, which
  drops all trackers and clears the published snapshot so a consumer sees the `"bandwidth"`
  provider go away cleanly.

## Two loggers

- `Shared.Logging.PluginLogger` (`Plugin.Log`) — used by the Harmony patch scaffolding via
  `Common.Logger`.
- `PluginSdk.Logging.Logger` (`SdkLog`) — writes to the Magnetar game log when standalone, or
  structured JSON when managed by Quasar; used for config and lifecycle logging. Its Magnetar
  sink is a no-op until the game log is ready, which is why the very-early bootstrap can log
  through it safely.

## Related

- [Patches.md](Patches.md) — the patches `EarlyStartup` applies.
- [Measurement.md](Measurement.md) — `BandwidthMonitor.Configure/Update/Reset`.
- [Build.md](Build.md) — how `Bandwidth.dll` is built and deployed to Magnetar.

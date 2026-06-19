# Shared framework and the client plugin

This page covers the parts of the repository that are **template scaffolding** rather than
bandwidth functionality: the `Shared` project and the `ClientPlugin`. They are documented for
completeness and to make clear what is and isn't part of the plugin's feature set.

## The bandwidth feature is server-only

All of the bandwidth measurement, telemetry and limiting lives in `ServerPlugin`
([Architecture.md](Architecture.md)). The **`ClientPlugin` is the unmodified template** — it
does not participate in bandwidth measurement and ships only the example settings UI. If you
only care about what the plugin *does*, you can stop at the server pages.

---

## `Shared` project — reusable plumbing

A shared-**source** project (`Shared.shproj` + `Shared.projitems`) compiled directly into both
plugin assemblies. It is the template's common infrastructure; Bandwidth uses a thin slice of
it.

| Area | Files | Role | Used by Bandwidth? |
| --- | --- | --- | --- |
| Plugin glue | `Plugin/Common.cs`, `Plugin/ICommonPlugin.cs` | `Common` holds the active plugin, logger, config, game version and data dir; `ICommonPlugin` is the contract the plugin satisfies. | **Yes** — `Common.Logger` is used across the patches; `Common.SetPlugin/AttachPlugin` wire up early/late startup. |
| Logging | `Logging/IPluginLogger.cs`, `PluginLogger.cs`, `LogFormatter.cs` | A small logging façade writing to the game log. | **Yes** — the patch scaffolding logs through it. |
| Config base | `Config/IPluginConfig.cs`, `PersistentConfig.cs`, `PluginConfig.cs` | Template config base + XML persistence. | **Partly** — `IPluginConfig` (just `Enabled`) is implemented by the server's `BandwidthConfig`; the server otherwise uses Magnetar's `PluginSdk.Config.PluginConfig`. `PersistentConfig`/`PluginConfig` are used by the **client** plugin. |
| Patch scaffolding | `Patches/PatchHelpers.cs` | Verify-then-apply Harmony helpers with category support and applied-patch logging. | **Yes** — `HarmonyPatchUncategorized` (server) / `HarmonyPatchAll` (client). |
| Tooling | `Tools/EnsureCode.cs`, `CodeChange.cs`, `Hashing.cs`, `TranspilerHelpers.cs`, `PreloaderHelpers.cs`, `GameAssembliesToPublicize.cs`, `IgnoresAccessChecksToAttribute.cs` | The `EnsureCode` game-method verification system, transpiler/preloader helpers, publicizer support. | **Available** — see below. |

### `PatchHelpers`

The verify-then-apply scaffold. `VerifyAndApply` first runs `EnsureCode` verification (to catch
a game update that changed a patched method), logs every conflicting change, and either throws
or returns false (controlled by the `SE_PLUGIN_THROW_ON_FAILED_METHOD_VERIFICATION` env var);
then it applies the patches and, in debug, logs each patched game method with the plugin patch
class targeting it and a running count. Variants: `HarmonyPatchAll` (client),
`HarmonyPatchUncategorized` (server early bootstrap), `HarmonyPatchCategory` (the unused
`"Late"` category — kept so a future late-loaded target can opt in).

`PatchHelpers.Configure()` and `PatchHelpers.PatchUpdates()` are intentionally **empty** for
this plugin: the bandwidth monitor lives in `ServerPlugin` and cannot be referenced from the
shared code (which also compiles into the client), so it is configured and ticked **directly**
from `ServerPlugin.Plugin` instead. The methods are kept for parity with the scaffolding and
the client plugin's call sites.

### `EnsureCode`

The template's defence against silent breakage after a game update: a patch method can carry an
`[EnsureCode]` attribute with a hash of the target IL, and verification fails loudly (skipping
the plugin load with a logged error) if the game's code changed. Bandwidth's limiter patches
take a lighter-weight equivalent approach — `Prepare()` guards and transpiler anchor checks
(see [Patches.md](Patches.md)) — so they degrade to stock behaviour rather than failing the
load. On Proton/Wine the hash check is auto-skipped (it tends to misfire there).

---

## `ClientPlugin` — template scaffolding only

The client plugin is the template's example, **not customised for Bandwidth**:

- [`ClientPlugin/Plugin.cs`](../ClientPlugin/Plugin.cs) — a standard `IPlugin` that loads a
  `PersistentConfig<PluginConfig>`, applies `HarmonyPatchAll` (the client has no patches of its
  own, so this is a no-op set), and exposes `OpenConfigDialog()`. The `Update`/`Dispose` bodies
  are the template's TODO stubs.
- [`ClientPlugin/Config.cs`](../ClientPlugin/Config.cs) — the demo config (`"Config Demo"`)
  showing every settings control type (checkbox, sliders, textbox, dropdown, colors, keybind,
  button). Pure example data.
- `ClientPlugin/Settings/` — a self-contained settings-dialog framework: typed UI **Elements**
  (Button, Checkbox, Color, Dropdown, Keybind, Separator, Slider, Textbox), **Layouts**
  (Simple/None), a `SettingsGenerator`/`SettingsScreen`, and binding tools. This is the
  reusable config-UI library the template advertises; the screenshot in the repo README/`Docs/`
  (`ConfigDialogExample.png`) shows what it renders.

Because the bandwidth feature is configured server-side through Magnetar/Quasar
([Configuration.md](Configuration.md)), this client UI is **not wired to anything bandwidth-
related**. It is kept so the repository remains a complete client+server template and so a
future client-side companion (e.g. a player-facing bandwidth HUD) has the scaffolding ready.

## If you are extending the plugin

- A **server** feature goes in `ServerPlugin` (add a patch under `Patches/`, register it via the
  early bootstrap automatically — `HarmonyPatchUncategorized` scans the assembly).
- A **client** feature would start by replacing the demo `Config.cs` with real options and
  adding client patches; see the SE plugin dev skills referenced in [`AGENTS.md`](../AGENTS.md).
- Anything shared between the two goes in `Shared` — but note it **cannot** reference
  server-only types like `BandwidthMonitor`.

# Building, deploying and project layout

How the solution is structured, built, and deployed to the game loaders. This is the SE
server plugin template's build system, lightly specific to Bandwidth.

## Prerequisites

- [Space Engineers](https://store.steampowered.com/app/244850/Space_Engineers/) and/or the
  [Dedicated Server](https://steamdb.info/app/298740/) installed (the build references their
  assemblies).
- [.NET Framework 4.8.1 Developer Pack](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net481)
  **and** the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) — the
  server plugin multi-targets both (see below).
- [Magnetar](https://magnetar.se) (server loader) and/or [Pulsar](https://github.com/SpaceGT/Pulsar)
  (client loader) installed — their `PluginSdk.dll` is referenced by the build and provides the
  runtime.
- [Python 3.12+](https://python.org) to run `setup.py`.

## One-time setup — `setup.py`

`setup.py` ([source](../setup.py)) does the per-machine setup:

1. Copies `Directory.Build.props.template` → `Directory.Build.props` if missing.
2. (On a fresh template clone only) offers to rename the `PluginTemplate` identifiers — already
   done for this repo, which is named **Bandwidth**.
3. Auto-detects the Steam install locations of Space Engineers (app `244850`) and the
   Dedicated Server (app `298740`) by parsing Steam's `libraryfolders.vdf` / `appmanifest_*.acf`,
   and writes `Bin64` / `Dedicated64` into `Directory.Build.props`.

Run it once after cloning:

```bash
python setup.py
```

## Reference-path overrides — `Directory.Build.props`

[`Directory.Build.props.template`](../Directory.Build.props.template) is the committed
template; `Directory.Build.props` is the **local, un-committed** copy each contributor gets, so
machine-specific paths never enter version control. It resolves four roots, each either set
explicitly or auto-detected per-OS (Windows registry / standard Steam paths; Linux `~/.steam`,
`~/.local/share/Steam`, Flatpak):

| Property | Points at |
| --- | --- |
| `Bin64` | Space Engineers client `Bin64` (client plugin references). |
| `Dedicated64` | Dedicated Server `DedicatedServer64` (server plugin references — see [`ServerPlugin.csproj`](../ServerPlugin/ServerPlugin.csproj)). |
| `Pulsar` | Pulsar install; `PulsarBin` (the folder with `PluginSdk.dll`) is resolved in `Directory.Build.targets`. |
| `Magnetar` | Magnetar install; `MagnetarBin` is resolved here, framework-dependently: `Libraries/MagnetarLegacy` for net4x, `Libraries/MagnetarInterim` for .NET (Core), flat `Bin/` on Linux. |

Leaving a path empty falls back to auto-detection, so a fresh clone + `setup.py` builds on both
Windows and Linux. A `verify_props` pre-build step checks that `Dedicated64` / `MagnetarBin`
resolve before compiling.

## Versioning — `Version.Build.props`

The single source of truth for the plugin version is
[`Version.Build.props`](../Version.Build.props) (`<Version>1.0.0</Version>`), which **is**
committed and imported by `Directory.Build.props`, so the version is shared by all
contributors. Bump it there.

## Building

Open `Bandwidth.sln` in Visual Studio or Rider and build, or use the CLI. The **server plugin
multi-targets** `net48;net10.0` on Windows (Magnetar runs both the Legacy/.NET-Framework and
Interim/.NET editions) and `net10.0` only on Linux:

```bash
dotnet build Bandwidth.sln -c Release
```

Both Debug and Release define `DEV_BUILD` (so the in-source `[assembly: AssemblyVersion]` is
used during local builds; when Magnetar/Pulsar compile from source it is not defined and the
attribute applies). A **Debug** build defines `DEBUG` for `#if DEBUG` blocks and lets
exceptions throw; always release from a **Release** build.

## Deploying

Each plugin project has a `PostBuildEvent` that runs its `Deploy.bat` (Windows) / `Deploy.sh`
(Linux) to copy `Bandwidth.dll` into the loader's local plugin folder:

- **Server** → `%AppData%\Magnetar\Legacy\Local` (net4x) or `…\Interim\Local` (.NET); the .NET
  build is skipped when the Interim edition is not installed. See
  [`ServerPlugin/Deploy.bat`](../ServerPlugin/Deploy.bat).
- **Client** → the Pulsar local plugin folder.

The copy retries up to 10 times to ride out the DLL being briefly locked by a running
server/game. `Clean.bat` / `clean.sh` remove build outputs.

## Project structure

| Project | File | Output |
| --- | --- | --- |
| `ServerPlugin` | [`ServerPlugin.csproj`](../ServerPlugin/ServerPlugin.csproj) | `Bandwidth.dll` for Magnetar. References `PluginSdk` (from `MagnetarBin`, `Private=False`) + the DS assemblies; `Lib.Harmony` and `Mono.Cecil` via NuGet. |
| `ClientPlugin` | `ClientPlugin.csproj` | `Bandwidth.dll` for Pulsar. (Template scaffolding — see [SharedAndClient.md](SharedAndClient.md).) |
| `Shared` | `Shared.shproj` / `Shared.projitems` | Shared **source** compiled into both plugins. |

The Krafs publicizer is **not** enabled for this plugin (the relevant `csproj` sections are
left commented out) — the server plugin reaches the one internal member it needs (`MyClient`)
by reflection instead. See [`Publicizer.md`](https://github.com/viktor-ferenczi/se-dev-skills)
in the dev skills if you ever need it.

## Distribution

This is a **server plugin**, so it is meant to be registered on
[MagnetarHub](https://github.com/viktor-ferenczi/MagnetarHub) (client plugins go on
[PluginHub](https://github.com/StarCpt/PluginHub/) via Pulsar). The registration manifests
[`BandwidthServer.xml`](../BandwidthServer.xml) / [`BandwidthClient.xml`](../BandwidthClient.xml)
still carry **template placeholder metadata** (`TODO` author, repo id, description, commit) —
fill these in before publishing.

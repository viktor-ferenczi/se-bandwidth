using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using HarmonyLib;
using PluginSdk.Config;
using PluginSdk.Paths;
using Sandbox;
using ServerPlugin.Config;
using ServerPlugin.Network;
using Shared.Config;
using Shared.Logging;
using Shared.Patches;
using Shared.Plugin;
using VRage.FileSystem;
using VRage.Game;
using VRage.Plugins;
using SdkLogger = PluginSdk.Logging.Logger;

// Define assembly version when compiled by Magnetar
#if !DEV_BUILD
using System.Reflection;

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif

namespace ServerPlugin;

// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin, ICommonPlugin
{
    public const string Name = "Bandwidth";
    public static Plugin Instance { get; private set; }

    public long Tick { get; private set; }
    private static bool failed;

    // Shared logger, used by the Harmony patch scaffolding via Plugin.Common.Logger.
    public IPluginLogger Log => Logger;
    private static readonly IPluginLogger Logger = new PluginLogger(Name);

    // PluginSdk logger: writes to the Magnetar game log when standalone, or structured JSON when
    // managed by Quasar. Used for config and lifecycle logging.
    private static readonly SdkLogger SdkLog = SdkLogger.Create(Name);

    // The PluginSdk-managed configuration is the single source of truth on the server. It also
    // implements the shared IPluginConfig so the early bootstrap (and Common) can read it.
    public IPluginConfig Config => config;
    private static BandwidthConfig config;

    // Expose the configuration for Quasar to discover via its agent.
    // ReSharper disable once UnusedMember.Global
    public BandwidthConfig PluginConfig => config;

    private static string configPath;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
#if DEBUG
        // Allow the debugger some time to connect once the plugin assembly is loaded
        Thread.Sleep(100);
#endif

        Instance = this;

        SdkLog.Info("Loading");

        // On the dedicated server the world is loaded before IPlugin.Init runs, so the network
        // capture patches are normally applied much earlier, bootstrapped from the Preloader's
        // Finish() hook (see InstallEarlyBootstrap). Run it here as a fallback for the case the
        // preloader path did not execute (e.g. Magnetar safe mode): coverage then starts from
        // Init rather than from before world load, but the plugin is still functional. Idempotent.
        EarlyStartup();
        if (failed)
            return;

        // Early startup ran against a stand-in plugin; point Common at the live instance so
        // per-tick code reaches the real Tick counter.
        Common.AttachPlugin(this);

        // All bandwidth patches target assemblies that are loaded before world load (Sandbox.Game,
        // VRage, VRage.Network), so they are applied early as uncategorized patches in
        // EarlyStartup. There is no deferred "Late" category to apply here.

        SdkLog.Info("Successfully loaded");
    }

    // Loads the PluginSdk configuration. Called once, from EarlyStartup.
    private static void LoadConfig()
    {
        // Resolve the config path case-insensitively: a no-op on Windows, the LinuxCompat resolver
        // on Linux. One code path works on both.
        configPath = PathResolver.Normalize(Path.Combine(MyFileSystem.UserDataPath, $"{Name}.cfg"));

        // Load existing values, or a default-constructed instance when absent.
        config = ConfigStorage.LoadXml<BandwidthConfig>(configPath);

        // Persist immediately so a fresh install leaves a sparse on-disk file (only non-default
        // values) to inspect.
        TrySaveConfig();

        // Re-persist and re-push to the monitor whenever the live config changes (e.g. an admin
        // toggles the plugin through Quasar) — so enable/disable takes effect without a restart.
        config.PropertyChanged += OnConfigChanged;
    }

    private static string GetGameVersion()
    {
        try
        {
            var serverBuildNumber = Sandbox.Game.MyPerGameSettings.BasicGameInfo.ServerBuildNumber.GetValueOrDefault();
            return $"{MyFinalBuildConstants.APP_VERSION_STRING_DOTS} b{serverBuildNumber}";
        }
        catch
        {
            // BasicGameInfo not populated yet (should not happen this late); degrade gracefully.
            return MyFinalBuildConstants.APP_VERSION_STRING_DOTS.ToString();
        }
    }

    // Called from the Preloader's Finish() hook (before the game starts). Installs a Harmony
    // postfix on MyInitializer.InvokeBeforeRun so the rest of the patching runs as soon as the
    // game has finished its core initialization — still well before world load, so the network
    // capture covers all client traffic from the very first packet.
    //
    // InvokeBeforeRun is the earliest safe trigger: it is where the game calls MyFileSystem.Init
    // (so UserDataPath becomes available) AND assigns MyLog.Default (so the game logger works).
    // This method itself runs even earlier, before MyLog.Default exists, so it logs via SdkLog,
    // whose Magnetar sink is a no-op until the game log is ready.
    // ReSharper disable once UnusedMember.Global
    public static void InstallEarlyBootstrap()
    {
        try
        {
            var target = AccessTools.Method(typeof(MyInitializer), nameof(MyInitializer.InvokeBeforeRun));
            if (target == null)
            {
                SdkLog.Critical("Early bootstrap: MyInitializer.InvokeBeforeRun not found; bandwidth capture will start later, from Init");
                return;
            }

            var postfix = new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(OnGameInitialized)));
            new Harmony($"{Name}.Bootstrap").Patch(target, postfix: postfix);
        }
        catch (Exception ex)
        {
            SdkLog.Critical("Early bootstrap: failed to install the MyInitializer.InvokeBeforeRun hook", ex);
        }
    }

    // Harmony postfix on MyInitializer.InvokeBeforeRun. Public so Harmony can resolve it;
    // intentionally NOT decorated with [HarmonyPatch], so it is never re-applied by the patch
    // scan. Runs once the game's filesystem, logging and config are ready, but before any world
    // is loaded.
    // ReSharper disable once UnusedMember.Global
    public static void OnGameInitialized()
    {
        try
        {
            EarlyStartup();
        }
        catch (Exception ex)
        {
            failed = true;
            SdkLog.Critical("Early startup failed", ex);
        }
    }

    private static bool earlyStarted;

    // One-shot early initialization: load config, configure the monitor and apply the network
    // capture patches — all before world load. Called from the InvokeBeforeRun postfix (normal
    // dedicated-server path) and from Init (fallback); both run on the main thread, so a plain
    // flag keeps it one-shot.
    private static void EarlyStartup()
    {
        if (earlyStarted)
            return;
        earlyStarted = true;

        LoadConfig();

        Common.SetPlugin(EarlyPlugin.Instance, GetGameVersion(), MyFileSystem.UserDataPath);

        // Push the loaded configuration into the monitor before the patches start feeding it, so
        // the enable flag and tuning parameters are honored from the first captured packet.
        PushConfigToMonitor();

        // Apply the capture patches now, before world load. They are all uncategorized; the
        // deferred "Late" category is unused by this plugin.
        if (!PatchHelpers.HarmonyPatchUncategorized(Logger, new Harmony(Name)))
        {
            failed = true;
            return;
        }

        SdkLog.Info("Bandwidth capture patches applied, before world load");
    }

    // Pushes the current configuration into the monitor. Called once at early startup and again
    // whenever the live config changes.
    private static void PushConfigToMonitor()
    {
        if (config == null)
            return;

        BandwidthMonitor.Configure(config.Enabled, config.PublishIntervalMs, config.WindowSize, config.RedactClientId);

        if (config.LimiterMode != BandwidthLimiterMode.Off)
            SdkLog.Warning($"LimiterMode={config.LimiterMode} is reserved; this build is observe-only and never paces outgoing traffic.");
    }

    // Lightweight ICommonPlugin used before the real plugin instance is available, so the shared
    // Common state is valid during the early world-load window on the dedicated server. Init swaps
    // in the live instance via Common.AttachPlugin.
    private sealed class EarlyPlugin : ICommonPlugin
    {
        public static readonly EarlyPlugin Instance = new EarlyPlugin();
        public IPluginLogger Log => Logger;
        public IPluginConfig Config => config;
        public long Tick => 0; // No simulation ticks during world load
    }

    private static void OnConfigChanged(object sender, PropertyChangedEventArgs e)
    {
        SdkLog.Info($"Config changed: {e.PropertyName}");
        PushConfigToMonitor();
        TrySaveConfig();
    }

    internal static void TrySaveConfig()
    {
        if (config == null || configPath == null)
            return;

        try
        {
            ConfigStorage.SaveXml(config, configPath);
        }
        catch (Exception ex)
        {
            SdkLog.Error("Failed to save config", ex);
        }
    }

    public void Dispose()
    {
        try
        {
            if (config != null)
                config.PropertyChanged -= OnConfigChanged;

            // Drop all live trackers and clear the published stats so a consumer sees the provider
            // go away cleanly.
            BandwidthMonitor.Reset();
        }
        catch (Exception ex)
        {
            SdkLog.Critical("Dispose failed", ex);
        }

        Instance = null;
    }

    public void Update()
    {
        if (failed)
            return;

#if DEBUG
        CustomUpdate();
        Tick++;
#else
        try
        {
            CustomUpdate();
            Tick++;
        }
        catch (Exception e)
        {
            SdkLog.Critical("Update failed", e);
            failed = true;
        }
#endif
    }

    private void CustomUpdate()
    {
        // Republish the per-client bandwidth snapshot if the publish interval has elapsed.
        BandwidthMonitor.Update();
        PatchHelpers.PatchUpdates();
    }
}

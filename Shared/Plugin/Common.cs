using System;
using System.IO;
using Shared.Config;
using Shared.Logging;
using Shared.Patches;

namespace Shared.Plugin;

public static class Common
{
    public static ICommonPlugin Plugin { get; private set; }
    public static IPluginLogger Logger { get; private set; }
    public static IPluginConfig Config { get; private set; }

    public static string GameVersion;
    public static string DataDir;

    public static void SetPlugin(ICommonPlugin plugin, string gameVersion, string storageDir)
    {
        AttachPlugin(plugin);

        GameVersion = gameVersion;
        DataDir = Path.Combine(storageDir, "Bandwidth");

        PatchHelpers.Configure();
    }

    // Points the shared accessors at the given plugin instance. On the dedicated server the early
    // bootstrap runs against a stand-in plugin; Init swaps in the live instance via this method
    // once it exists, so per-tick code reaches the real Tick counter.
    public static void AttachPlugin(ICommonPlugin plugin)
    {
        Plugin = plugin;
        Logger = plugin.Log;
        Config = plugin.Config;
    }
}
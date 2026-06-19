using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Shared.Logging;
using Shared.Tools;

namespace Shared.Patches;

// ReSharper disable once UnusedType.Global
public static class PatchHelpers
{
    // Harmony patch category for patches that must be deferred to IPlugin.Init because their target
    // type lives in an assembly that is not loaded yet at the dedicated server's early bootstrap
    // point (e.g. VRage.EOS). Every patch without this category is applied early. The bandwidth
    // plugin's patches all target assemblies loaded before world load, so none currently use it;
    // the category-aware scaffolding is kept so a future late-loaded target can opt in without
    // reworking the bootstrap.
    public const string LateCategory = "Late";

    // Applies every patch in the executing assembly. Used by the client plugin, where there is no
    // early-bootstrap split.
    public static bool HarmonyPatchAll(IPluginLogger log, Harmony harmony, bool handleExceptions = true)
    {
        return VerifyAndApply(log, harmony, handleExceptions,
            EnsureCode.Verify,
            () => harmony.PatchAll(Assembly.GetExecutingAssembly()),
            "all patches");
    }

    // Applies the uncategorized patches: everything except the deferred "Late" category. Used by
    // the dedicated server's early bootstrap, before world-load compilation. Patches whose target
    // assembly is not loaded yet carry a category and are applied later from Init.
    public static bool HarmonyPatchUncategorized(IPluginLogger log, Harmony harmony, bool handleExceptions = true)
    {
        return VerifyAndApply(log, harmony, handleExceptions,
            EnsureCode.VerifyUncategorized,
            () => harmony.PatchAllUncategorized(Assembly.GetExecutingAssembly()),
            "uncategorized patches");
    }

    // Applies only the patches in the given category, verifying only those first.
    public static bool HarmonyPatchCategory(IPluginLogger log, Harmony harmony, string category, bool handleExceptions = true)
    {
        return VerifyAndApply(log, harmony, handleExceptions,
            () => EnsureCode.VerifyCategory(category),
            () => harmony.PatchCategory(Assembly.GetExecutingAssembly(), category),
            $"category '{category}'");
    }

    // Shared scaffold: verify the targeted game methods still match, then apply the patches.
    private static bool VerifyAndApply(IPluginLogger log, Harmony harmony, bool handleExceptions, Func<IEnumerable<CodeChange>> verify, Action apply, string what)
    {
        log.Debug($"Scanning for conflicting code changes ({what})");
        var throwOnFailedVerification = !handleExceptions || Environment.GetEnvironmentVariable("SE_PLUGIN_THROW_ON_FAILED_METHOD_VERIFICATION") != null;
        try
        {
            var codeChanges = verify().ToList();
            if (codeChanges.Count != 0)
            {
                log.Critical("Detected conflicting code changes:");
                foreach (var codeChange in codeChanges)
                    log.Info(codeChange.ToString());

                if (throwOnFailedVerification)
                {
                    throw new Exception("Detected conflicting code changes");
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to scan for conflicting code changes");

            if (throwOnFailedVerification)
            {
                throw;
            }

            return false;
        }

        log.Debug($"Applying Harmony patches ({what})");

        // Snapshot the methods this Harmony id has already patched so that, after applying, we can
        // report exactly the ones this phase added. GetPatchedMethods() is scoped to harmony.Id,
        // but the dedicated server applies patches in two phases under the same id (uncategorized
        // early, then the "Late" category from Init), so a before/after delta isolates the phase.
        var before = new HashSet<MethodBase>(harmony.GetPatchedMethods());
        try
        {
            apply();
        }
        catch (Exception ex)
        {
            log.Critical(ex, "Failed to apply Harmony patches");
            return false;
        }

        if (log.IsDebugEnabled)
        {
            LogAppliedPatches(log, harmony, before, what);
        }

        return true;
    }

    // Proof that the patches were applied: debug-log every game method this phase patched (each
    // with the plugin patch class targeting it) with a running count, then a line with the total.
    private static void LogAppliedPatches(IPluginLogger log, Harmony harmony, HashSet<MethodBase> before, string what)
    {
        var applied = harmony.GetPatchedMethods()
            .Where(method => !before.Contains(method))
            .OrderBy(method => method.DeclaringType?.FullName, StringComparer.Ordinal)
            .ThenBy(method => method.ToString(), StringComparer.Ordinal)
            .ToList();

        var count = 0;
        foreach (var method in applied)
            log.Debug($"Patch applied #{++count}: {DescribePatchedMethod(harmony, method)}");

        log.Debug($"Applied {count} {(count == 1 ? "patch" : "patches")} ({what})");
    }

    // Renders a patched game method as "Namespace.Type.Method(argTypes) <- PatchClass[, ...]",
    // naming the plugin patch classes (filtered to this Harmony id) whose prefix/postfix/
    // transpiler/finalizer targets it.
    private static string DescribePatchedMethod(Harmony harmony, MethodBase method)
    {
        var parameters = string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name));
        var target = $"{method.DeclaringType?.FullName}.{method.Name}({parameters})";

        var info = Harmony.GetPatchInfo(method);
        if (info == null)
            return target;

        var patchClasses = info.Prefixes
            .Concat(info.Postfixes)
            .Concat(info.Transpilers)
            .Concat(info.Finalizers)
            .Where(patch => patch.owner == harmony.Id)
            .Select(patch => patch.PatchMethod.DeclaringType?.Name)
            .Distinct()
            .ToList();

        return patchClasses.Count == 0 ? target : $"{target} <- {string.Join(", ", patchClasses)}";
    }

    // Called after loading configuration, but before patching. The bandwidth patches need no
    // one-time configuration here — the monitor is configured directly from the server plugin
    // (BandwidthMonitor lives in the ServerPlugin project and cannot be referenced from this
    // shared code, which also compiles into the client plugin). Kept for parity with the
    // scaffolding and the client plugin's call site.
    public static void Configure()
    {
    }

    // Called on every update. The bandwidth monitor's periodic publish is driven directly from
    // ServerPlugin.Plugin.CustomUpdate for the same shared-project reason as Configure above, so
    // there is nothing to do here.
    public static void PatchUpdates()
    {
    }
}

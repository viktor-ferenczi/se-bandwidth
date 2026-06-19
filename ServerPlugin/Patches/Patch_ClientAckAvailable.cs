using System;
using System.Reflection;
using HarmonyLib;
using ServerPlugin.Network;
using Shared.Plugin;

namespace ServerPlugin.Patches;

/// <summary>
/// Feeds the AIMD controller's overuse signal. <c>MyClient.IsAckAvailable()</c> is called once per
/// client at the top of <c>FilterStateSync</c>; returning <c>false</c> means the client's in-flight
/// ACK window is exhausted (a stalled serviced tick). A postfix reads that result and forwards it to
/// the client's <see cref="RateController"/>.
///
/// <para>
/// The method is read-only-observed here: <c>IsAckAvailable()</c> is <b>not</b> side-effect free (it
/// sets the client's <c>m_waitingForReset</c> flag), so the plugin must never call it itself — it
/// only observes the game's own call through <c>__result</c>.
/// </para>
///
/// <para>
/// <c>MyClient</c> is internal to VRage.Network and the plugin does not publicize game assemblies, so
/// the target is resolved by name and the instance is taken as <see cref="object"/>.
/// <see cref="Prepare"/> skips the patch gracefully if the method is gone on a future SE build, so
/// this default-off feature never breaks the observe-only telemetry.
/// </para>
/// </summary>
[HarmonyPatch]
internal static class Patch_ClientAckAvailable
{
    private static MethodBase ResolveTarget()
        => AccessTools.Method("VRage.Network.MyClient:IsAckAvailable");

    public static bool Prepare() => ResolveTarget() != null;

    public static MethodBase TargetMethod() => ResolveTarget();

    public static void Postfix(object __instance, bool __result)
    {
        try
        {
            ulong steamId = BandwidthLimiter.GetSteamId(__instance);
            if (steamId == 0UL || !BandwidthMonitor.TryGetConnection(steamId, out var connection))
                return;

            // __result == false ⇒ the ACK window is exhausted on this serviced tick (overuse).
            connection.Control.RecordAckObservation(!__result);
        }
        catch (Exception e)
        {
            Common.Logger?.Error(e, "Error in bandwidth ack-stall hook");
        }
    }
}

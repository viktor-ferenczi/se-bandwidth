using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ServerPlugin.Network;
using Shared.Plugin;
using VRage.Network;

namespace ServerPlugin.Patches;

/// <summary>
/// The §7.2 <b>O2 dynamic packet budget</b> enforcement point. A transpiler on the private
/// <see cref="MyReplicationServer"/>.<c>FilterStateSync</c> replaces the hardcoded per-client
/// packet budget (<c>int num2 = 7;</c>) with a call to
/// <see cref="BandwidthLimiter.PacketBudget"/>, so the budget becomes per-client and adapts to the
/// AIMD controller. Only unreliable state-sync packets decrement this budget (streaming and
/// reliable traffic take other paths in the same method), so reliable delivery is unaffected.
///
/// <para>
/// Defensive on two levels so a default-off feature can never break the server on an SE update:
/// <see cref="Prepare"/> skips the whole patch if the target method is gone, and the transpiler
/// returns the original IL unchanged (with a critical log) if the <c>ldc.i4.7</c> anchor is
/// absent. In both cases replication is left at stock behavior.
/// </para>
/// </summary>
[HarmonyPatch(typeof(MyReplicationServer), "FilterStateSync")]
internal static class Patch_FilterStateSyncBudget
{
    // Skip the patch entirely if the target method cannot be resolved (e.g. renamed by an SE
    // update), instead of letting Harmony throw and fail the whole plugin load.
    public static bool Prepare()
        => AccessTools.Method(typeof(MyReplicationServer), "FilterStateSync") != null;

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var matcher = new CodeMatcher(instructions);

        // Anchor: `int num2 = 7;` is the only literal 7 in the method and initializes the per-client
        // packet budget (verified against the decompiled DS). It is straight-line init code with no
        // labels targeting it, so mutating the instruction in place is safe.
        matcher.MatchStartForward(new CodeMatch(OpCodes.Ldc_I4_7));
        if (matcher.IsInvalid)
        {
            Common.Logger?.Critical(
                "Bandwidth limiter: packet-budget anchor (ldc.i4.7) not found in MyReplicationServer.FilterStateSync; " +
                "limiter disabled, replication left at stock behavior.");
            return instructions;
        }

        MethodInfo packetBudget = AccessTools.Method(typeof(BandwidthLimiter), nameof(BandwidthLimiter.PacketBudget));

        // Replace `ldc.i4.7` with `ldarg.1` (the MyClient parameter; ldarg.0 is the
        // MyReplicationServer `this`) followed by `call int BandwidthLimiter.PacketBudget(object)`.
        // A MyClient reference is assignable to object, so the call is verifiable.
        matcher.Set(OpCodes.Ldarg_1, null);
        matcher.Advance(1);
        matcher.Insert(new CodeInstruction(OpCodes.Call, packetBudget));

        return matcher.InstructionEnumeration();
    }
}

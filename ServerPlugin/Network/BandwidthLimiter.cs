using System;
using System.Reflection;
using HarmonyLib;
using ServerPlugin.Config;
using Shared.Plugin;
using VRage.Network;

namespace ServerPlugin.Network;

/// <summary>
/// Static façade between the replication send loop and the per-client
/// <see cref="RateController"/>s. The §7.2 <b>O2 dynamic packet budget</b> lives here:
/// the transpiler on <c>MyReplicationServer.FilterStateSync</c> calls
/// <see cref="PacketBudget"/> in place of the hardcoded literal <c>7</c>, so the
/// per-tick packet budget becomes a per-client value derived from <c>R_target</c>.
///
/// <para>
/// Safe by construction: the budget is always <c>clamp(round((R_target/60)/MTU), 1, 7)</c>,
/// so it can never exceed the stock <c>7</c> ("never worse than stock"), and an unconstrained
/// client whose <c>R_target</c> has ramped to <see cref="RateController.Max"/> clamps back to 7
/// (exactly stock). Every public entry point is wrapped so a failure returns the stock budget and
/// never disturbs replication.
/// </para>
///
/// <para>
/// The replication client type (<c>MyClient</c>) is <c>internal</c> to VRage.Network and this
/// plugin does not publicize game assemblies, so the client is passed as <see cref="object"/>
/// and its Steam ID is read through its public <c>State</c> field (a public
/// <see cref="MyClientStateBase"/>), the only step that needs reflection.
/// </para>
/// </summary>
internal static class BandwidthLimiter
{
    /// <summary>Replication tick rate (~60 Hz); the budget converts a per-second target into a
    /// per-tick byte allowance.</summary>
    public const int TickHz = 60;

    /// <summary>Usable state-sync payload per packet (MTU minus framing), <c>Docs/BandwidthEstimator.md</c>
    /// §2.1. Used to convert the per-tick byte allowance into a packet count.</summary>
    public const int MtuBytes = 1190;

    // The active enforcement mode. Off (and the disabled plugin) make PacketBudget return the stock
    // 7 unconditionally, so replication is byte-for-byte stock.
    private static volatile BandwidthLimiterMode mode = BandwidthLimiterMode.Off;

    // Cached accessor for MyClient.State (a public field on the internal MyClient type). Resolved
    // once on first use; the runtime type is the same MyClient for every connection.
    private static FieldInfo stateField;

    /// <summary>Set the enforcement mode. Pushed from config through
    /// <see cref="BandwidthMonitor.Configure"/>.</summary>
    public static void Configure(BandwidthLimiterMode mode)
    {
        BandwidthLimiter.mode = mode;
    }

    /// <summary>
    /// Per-client packet budget for one <c>FilterStateSync</c> pass. Called from the transpiler
    /// with the <c>MyClient</c> instance (typed as <see cref="object"/> to avoid referencing the
    /// internal type). Returns the stock <c>7</c> when the limiter is off, the client is unknown,
    /// or anything throws — so an enabled limiter can only ever pace down, never break the loop.
    /// </summary>
    public static int PacketBudget(object client)
    {
        try
        {
            if (mode == BandwidthLimiterMode.Off)
                return 7;

            ulong steamId = GetSteamId(client);
            if (steamId == 0UL || !BandwidthMonitor.TryGetConnection(steamId, out var connection))
                return 7;

            return BudgetFromTarget(connection.Control.TargetBytesPerSec);
        }
        catch (Exception e)
        {
            Common.Logger?.Error(e, "Bandwidth limiter packet-budget failed; using stock budget");
            return 7;
        }
    }

    /// <summary>
    /// Convert an operating target (bytes/sec) into a per-tick packet budget,
    /// <c>clamp(round((target/TickHz)/MtuBytes), 1, 7)</c>. The floor of 1 keeps the top-priority
    /// group (the controlled entity) flowing; the ceiling of 7 is the stock budget. Pure, so it is
    /// shared by <see cref="PacketBudget"/> and the telemetry capture.
    /// </summary>
    public static int BudgetFromTarget(double targetBytesPerSec)
    {
        double bytesPerTick = targetBytesPerSec / TickHz;
        int budget = (int)Math.Round(bytesPerTick / MtuBytes, MidpointRounding.AwayFromZero);
        if (budget < 1)
            budget = 1;
        if (budget > 7)
            budget = 7;
        return budget;
    }

    /// <summary>
    /// Read the Steam ID from a <c>MyClient</c> instance via its public <c>State</c> field. The
    /// declaring type is internal, so the field is fetched reflectively once and cached; the field
    /// value is a public <see cref="MyClientStateBase"/>, from which the endpoint is read directly.
    /// Returns 0 when the client or state is unavailable.
    /// </summary>
    public static ulong GetSteamId(object client)
    {
        if (client == null)
            return 0UL;

        var field = stateField;
        if (field == null)
        {
            field = AccessTools.Field(client.GetType(), "State");
            stateField = field;
            if (field == null)
                return 0UL;
        }

        if (field.GetValue(client) is MyClientStateBase state)
            return state.EndpointId.Id.Value;

        return 0UL;
    }
}

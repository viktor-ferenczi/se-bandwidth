using System;
using HarmonyLib;
using ServerPlugin.Network;
using Shared.Plugin;
using VRage.Network;

namespace ServerPlugin.Patches;

/// <summary>
/// Connection close. <see cref="MyReplicationServer.OnClientLeft"/> is the public
/// "client left" entry point; a postfix drops the client from the live registry
/// so it stops appearing in published snapshots. Applied before world load to
/// match the connection-open hook. The epoch counter is retained so a later
/// reconnect of the same user gets a higher epoch.
/// </summary>
[HarmonyPatch(typeof(MyReplicationServer), nameof(MyReplicationServer.OnClientLeft))]
internal static class Patch_ReplicationClientLeft
{
    public static void Postfix(EndpointId endpointId)
    {
        try
        {
            BandwidthMonitor.OnClientDisconnected(endpointId.Value);
        }
        catch (Exception e)
        {
            Common.Logger?.Error(e, "Error in bandwidth client-left hook");
        }
    }
}

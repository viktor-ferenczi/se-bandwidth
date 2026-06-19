using System;
using HarmonyLib;
using ServerPlugin.Network;
using Shared.Plugin;
using VRage.Network;

namespace ServerPlugin.Patches;

/// <summary>
/// Connection open. <see cref="MyReplicationServer.AddClient"/> is called once per
/// new client at the replication layer; a postfix bumps the connection epoch and
/// starts a fresh bandwidth tracker, so a reconnect of the same Steam user is a
/// new estimate (<c>Docs/BandwidthEstimator.md</c> §5). Applied before world load
/// so trackers exist before any traffic is exchanged.
/// </summary>
[HarmonyPatch(typeof(MyReplicationServer), nameof(MyReplicationServer.AddClient))]
[HarmonyPatch([typeof(Endpoint), typeof(MyClientStateBase)])]
internal static class Patch_ReplicationAddClient
{
    public static void Postfix(Endpoint endpoint)
    {
        try
        {
            BandwidthMonitor.OnClientConnected(endpoint.Id.Value);
        }
        catch (Exception e)
        {
            Common.Logger?.Error(e, "Error in bandwidth client-connected hook");
        }
    }
}

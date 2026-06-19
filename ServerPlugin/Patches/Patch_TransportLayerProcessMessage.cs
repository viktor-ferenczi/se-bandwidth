using System;
using HarmonyLib;
using ServerPlugin.Network;
using Shared.Plugin;
using VRage;

namespace ServerPlugin.Patches;

/// <summary>
/// UP (client → server) byte capture. Every inbound message is demultiplexed
/// through the internal <c>MyTransportLayer.ProcessMessage(MyPacket)</c>, so a
/// prefix there observes all client→server traffic. The type is internal and the
/// method private, so the target is resolved by assembly-qualified name (Harmony
/// supports this) and applied before world load.
///
/// <para>
/// We only read the packet's sender and total byte length. Reading
/// <c>BitStream.ByteLength</c> returns the buffer length and does not advance the
/// read cursor, so the original processing — which reads from the same stream — is
/// unaffected.
/// </para>
/// </summary>
[HarmonyPatch("Sandbox.Engine.Multiplayer.MyTransportLayer, Sandbox.Game", "ProcessMessage")]
internal static class Patch_TransportLayerProcessMessage
{
    public static void Prefix(MyPacket p)
    {
        try
        {
            var bitStream = p?.BitStream;
            if (bitStream == null)
                return;
            BandwidthMonitor.RecordUp(p.Sender.Id.Value, bitStream.ByteLength);
        }
        catch (Exception e)
        {
            Common.Logger?.Error(e, "Error in bandwidth up-capture");
        }
    }
}

using System;
using HarmonyLib;
using Sandbox.Engine.Networking;
using ServerPlugin.Network;
using Shared.Plugin;
using VRage.GameServices;

namespace ServerPlugin.Patches;

/// <summary>
/// DOWN (server → client) byte capture. Every outgoing packet — state sync,
/// streaming, reliable events, voice — is enqueued through
/// <see cref="MyNetworkWriter.SendPacket"/> as a
/// <see cref="MyNetworkWriter.MyPacketDescriptor"/> carrying its recipients,
/// reliability class and payload, so a prefix here observes all server→client
/// traffic.
///
/// <para>
/// Applied before world load (an uncategorized patch, bootstrapped from the
/// preloader) so coverage starts with the very first client packet. We read the
/// descriptor's fields synchronously in a <em>prefix</em> — before it is enqueued
/// and later serialized and recycled by <c>SendAll</c> on the network thread —
/// attributing the approximate on-wire size to each recipient's downlink.
/// </para>
/// </summary>
[HarmonyPatch(typeof(MyNetworkWriter), nameof(MyNetworkWriter.SendPacket))]
internal static class Patch_NetworkWriterSendPacket
{
    public static void Prefix(MyNetworkWriter.MyPacketDescriptor packet)
    {
        try
        {
            var recipients = packet?.Recipients;
            if (recipients == null || recipients.Count == 0)
                return;

            // Approximate on-wire size: SendAll frames each packet with the fixed
            // header (PACKET_HEADER_SIZE) plus the descriptor's own header and
            // payload. Exact framing varies by a few bytes; close enough for rate
            // estimation.
            int bytes = MyNetworkWriter.PACKET_HEADER_SIZE
                        + (int)packet.Header.Position
                        + (packet.Data?.Size ?? 0);
            if (bytes <= 0)
                return;

            // MyP2PMessageEnum: Unreliable=0, UnreliableNoDelay=1, Reliable=2,
            // ReliableWithBuffering=3 — so >= Reliable captures both reliable classes.
            bool reliable = packet.MsgType >= MyP2PMessageEnum.Reliable;

            for (int i = 0; i < recipients.Count; i++)
                BandwidthMonitor.RecordDown(recipients[i].Value, bytes, reliable);
        }
        catch (Exception e)
        {
            Common.Logger?.Error(e, "Error in bandwidth down-capture");
        }
    }
}

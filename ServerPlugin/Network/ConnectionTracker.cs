using System.Diagnostics;

namespace ServerPlugin.Network;

/// <summary>
/// One client connection's bandwidth state: the two directional trackers plus
/// identity and lifecycle. Keyed in the registry by Steam ID; <see cref="Epoch"/>
/// distinguishes reconnects of the same user (<c>Docs/BandwidthEstimator.md</c>
/// §5), so a reconnect is always a fresh tracker, never a resumed one.
/// </summary>
internal sealed class ConnectionTracker
{
    private readonly long connectedAtTicks;

    public ConnectionTracker(ulong steamId, uint epoch, int windowSize)
    {
        SteamId = steamId;
        Epoch = epoch;
        Down = new DirectionTracker(windowSize);
        Up = new DirectionTracker(windowSize);
        Control = new RateController();
        connectedAtTicks = Stopwatch.GetTimestamp();
    }

    public ulong SteamId { get; }
    public uint Epoch { get; }

    /// <summary>Server → client (the direction the limiter paces).</summary>
    public DirectionTracker Down { get; }

    /// <summary>Client → server (diagnostic only; never paced).</summary>
    public DirectionTracker Up { get; }

    /// <summary>The downlink AIMD operating-rate controller for this connection. Fed the ACK-stall
    /// signal by the <c>IsAckAvailable</c> postfix and stepped once per publish interval; its
    /// <see cref="RateController.TargetBytesPerSec"/> drives the per-tick packet budget.</summary>
    public RateController Control { get; }

    /// <summary>Set when the connection closes; the entry is then dropped from the
    /// live registry.</summary>
    public bool Closed { get; set; }

    /// <summary>Wall-clock seconds since this connection was registered.</summary>
    public double ConnectedSeconds =>
        (Stopwatch.GetTimestamp() - connectedAtTicks) / (double)Stopwatch.Frequency;
}

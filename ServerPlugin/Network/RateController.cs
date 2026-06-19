using System;

namespace ServerPlugin.Network;

/// <summary>
/// Per-connection (downlink) AIMD operating-rate controller — the §6.4 controller of
/// <c>Docs/BandwidthEstimator.md</c>, reduced to the signals this build actually has.
/// It is pure and game-independent (no game references), so it can be reasoned about
/// and unit-tested in isolation; the game plumbing lives in <see cref="BandwidthLimiter"/>
/// and the two patches that feed it.
///
/// <para>
/// The controller tracks an operating target <c>R_target</c> (bytes/sec) per client and
/// adjusts it once per publish interval from two signals:
/// </para>
/// <list type="bullet">
/// <item>the <b>ACK-window stall</b> — <c>MyClient.IsAckAvailable()</c> returning false on a
/// serviced tick, meaning the client's in-flight window is exhausted (the overuse signal,
/// accumulated through <see cref="RecordAckObservation"/>); and</item>
/// <item>the elapsed time, used as the additive-increase probe.</item>
/// </list>
///
/// <para>
/// The law is classic TCP-style AIMD: on sustained stalls (a fraction of serviced ticks at
/// or above <see cref="StallFraction"/>) back off multiplicatively toward <see cref="Floor"/>;
/// otherwise probe upward additively toward <see cref="Max"/>. Additive-increase-to-<c>R_max</c>
/// (rather than to the measured drain rate) is deliberate: using the achieved throughput as the
/// increase ceiling would self-lock the limiter at whatever it is currently sending (the
/// "closed-loop blindness" of §11). With an unconditional upward probe and the stall as the only
/// brake, <c>R_target</c> naturally settles just above the rate the link can actually drain.
/// </para>
///
/// <para>
/// All of <see cref="RecordAckObservation"/> and <see cref="Update"/> run on the engine
/// replication/update thread (the ACK postfix and the publish loop are both on it), so the
/// per-window counters need no locking.
/// </para>
/// </summary>
internal sealed class RateController
{
    // Tunables, pushed from config via BandwidthMonitor.Configure. Static because they are global
    // policy, not per-connection state; the defaults mirror BandwidthConfig so a controller built
    // before the first Configure call is already sane.
    public static double Floor = 32768.0;     // R_min, bytes/sec — anti-starvation lower bound
    public static double Max = 524288.0;      // R_max, bytes/sec — probe ceiling (budget clamps to stock 7 here)
    public static double Prior = 262144.0;    // initial R_target for a fresh connection, bytes/sec
    public static double Alpha = 65536.0;     // additive increase, bytes/sec per second of elapsed time
    public static double Beta = 0.85;         // multiplicative decrease factor in (0, 1)
    public static double StallFraction = 0.2; // fraction of serviced ticks that must stall to back off

    // Per-window observation counters, drained each Update. Touched only on the replication thread.
    private long stallTicks;
    private long serviceTicks;

    private double rTarget = Prior;

    public RateController()
    {
        rTarget = Prior;
    }

    /// <summary>Current operating target in bytes/sec. The limiter derives the per-tick packet
    /// budget from this (see <see cref="BandwidthLimiter.BudgetFromTarget"/>).</summary>
    public double TargetBytesPerSec => rTarget;

    /// <summary>True when the most recent <see cref="Update"/> detected overuse and backed off.
    /// Surfaced for telemetry/diagnostics.</summary>
    public bool Overuse { get; private set; }

    /// <summary>
    /// Record one serviced-tick ACK observation, called from the <c>IsAckAvailable</c> postfix.
    /// <paramref name="stalled"/> is true when the window was exhausted (the game returned false).
    /// </summary>
    public void RecordAckObservation(bool stalled)
    {
        if (stalled)
            stallTicks++;
        else
            serviceTicks++;
    }

    /// <summary>
    /// Apply one AIMD step over the elapsed publish interval, then reset the observation window.
    /// Called once per publish interval from <see cref="BandwidthMonitor"/>.
    /// <paramref name="achievedBytesPerSec"/> is currently informational (the law probes to
    /// <see cref="Max"/>, not to the achieved rate — see the type remarks); it is kept in the
    /// signature for telemetry symmetry and a future rate-coupled variant.
    /// </summary>
    public void Update(double achievedBytesPerSec, double dtSeconds)
    {
        long stalls = stallTicks;
        long total = stalls + serviceTicks;

        bool overuse = total > 0L && (double)stalls / total >= StallFraction;
        Overuse = overuse;

        if (overuse)
            rTarget = Math.Max(Floor, Beta * rTarget);
        else
            rTarget += Alpha * Math.Max(0.0, dtSeconds);

        // Clamp defensively: the additive probe must not exceed Max, and a runtime change to the
        // Floor/Max bounds must take effect immediately on the next step.
        if (rTarget < Floor)
            rTarget = Floor;
        if (rTarget > Max)
            rTarget = Max;

        stallTicks = 0L;
        serviceTicks = 0L;
    }
}

using System.Threading;

namespace ServerPlugin.Network;

/// <summary>
/// Per-connection, per-direction bandwidth tracker. The hot path
/// (<see cref="Add"/>) only performs an atomic add, so it is safe to call from
/// the network or engine threads with no locking. The heavier math runs once per
/// publish interval in <see cref="Sample"/>, only ever on the single publishing
/// thread (the engine update loop), after which the publisher reads the exposed
/// estimate properties on that same thread.
///
/// <para>
/// This build is observe-only (see <c>Docs/BandwidthEstimator.md</c> §12): capacity
/// is estimated as a BBR-style windowed maximum of the per-interval delivery rate
/// (§6.7). Because we never pace the link, every sample may be app-limited, so the
/// windowed max is a <em>lower bound</em> on the real capacity — honest, and the
/// right starting point. The delay-gradient Kalman filter and the AIMD controller
/// (§6.2/§6.4) need per-packet ACK timing from the internal <c>MyClient</c> and
/// are deferred to a later phase; until then <see cref="Confidence"/> is a simple
/// warm-up ramp and no operating target is produced.
/// </para>
/// </summary>
internal sealed class DirectionTracker
{
    // Hot path: bytes accumulated since the last Sample. Mutated via Interlocked.
    private long bytesAccum;

    // Math state — touched only by Sample, on the publishing thread.
    private readonly double[] rateWindow; // recent per-interval rates (BtlBw window)
    private int windowCount;
    private int windowHead;
    private double estimateBytesPerSec; // windowed-max delivery rate (Ĉ_rate)
    private double achievedBytesPerSec; // most recent interval's measured rate
    private int samples;

    public DirectionTracker(int windowSize)
    {
        if (windowSize < 1)
            windowSize = 1;
        rateWindow = new double[windowSize];
    }

    /// <summary>Hot path: record bytes moved in this direction. Thread-safe.</summary>
    public void Add(int bytes) => Interlocked.Add(ref bytesAccum, bytes);

    /// <summary>
    /// Fold the bytes accumulated since the last call into the estimate. Call once
    /// per publish interval on the publishing thread.
    /// </summary>
    public void Sample(double dtSeconds)
    {
        long bytes = Interlocked.Exchange(ref bytesAccum, 0L);
        double rate = dtSeconds > 0.0 ? bytes / dtSeconds : 0.0;
        achievedBytesPerSec = rate;

        rateWindow[windowHead] = rate;
        windowHead = (windowHead + 1) % rateWindow.Length;
        if (windowCount < rateWindow.Length)
            windowCount++;

        double max = 0.0;
        for (int i = 0; i < windowCount; i++)
        {
            if (rateWindow[i] > max)
                max = rateWindow[i];
        }
        estimateBytesPerSec = max;

        if (samples < int.MaxValue)
            samples++;
    }

    /// <summary>Measured goodput over the last publish interval (bytes/sec). Read on
    /// the publishing thread right after <see cref="Sample"/>.</summary>
    public double AchievedBytesPerSec => achievedBytesPerSec;

    /// <summary>Windowed-max delivery-rate capacity estimate (bytes/sec). Read on the
    /// publishing thread right after <see cref="Sample"/>.</summary>
    public double EstimateBytesPerSec => estimateBytesPerSec;

    /// <summary>
    /// Observe-only confidence in 0..1: a warm-up ramp over one window, deliberately
    /// capped low because we currently have only a rate lower bound, not the
    /// delay-gradient Kalman's variance-based confidence (added in a later phase).
    /// </summary>
    public double Confidence
    {
        get
        {
            double warmup = (double)samples / rateWindow.Length;
            if (warmup > 1.0)
                warmup = 1.0;
            return 0.5 * warmup;
        }
    }
}

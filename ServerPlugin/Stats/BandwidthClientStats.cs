using PluginSdk.Stats;

namespace ServerPlugin.Stats;

/// <summary>
/// One per-client row of a bandwidth snapshot: the two directional rate estimates
/// plus connection facts. Captured into a multi-instance <see cref="StatGroup"/>,
/// one <see cref="StatInstance"/> per connected client, labelled by Steam ID (or a
/// stable anonymous hash when redaction is enabled). A reconnect of the same user
/// is a new row distinguished by <see cref="ConnectionEpoch"/>, so a consumer keys
/// a time series on (label, epoch).
/// </summary>
public sealed class BandwidthClientStats
{
    /// <summary>Client identity: the Steam ID, or a stable per-process anonymous
    /// hash when client-id redaction is enabled.</summary>
    [StatLabel("Client identity (Steam ID, or an anonymous hash when redaction is on)")]
    public string Client { get; set; }

    /// <summary>Distinguishes reconnects of the same user within this process; a
    /// fresh connection gets a higher epoch. Not summable; carried as the latest
    /// value over time.</summary>
    [Discrete("Reconnect epoch within the server process", OverTime = TimeAggregation.Last)]
    public uint ConnectionEpoch { get; set; }

    /// <summary>Server → client measured goodput over the last interval (bytes/sec).
    /// Sums across clients to the server's total downlink.</summary>
    [Gauge("Server → client throughput", Unit = "B/s")]
    public double DownBytesPerSec { get; set; }

    /// <summary>Server → client windowed-max capacity estimate (bytes/sec).</summary>
    [Gauge("Server → client capacity estimate (windowed max)", Unit = "B/s")]
    public double DownEstimateBytesPerSec { get; set; }

    /// <summary>Confidence of the server → client estimate in 0..1. Averaged across
    /// clients rather than summed.</summary>
    [Gauge("Server → client estimate confidence (0..1)", AcrossInstances = StatAggregation.Mean)]
    public double DownConfidence { get; set; }

    /// <summary>Client → server measured goodput over the last interval (bytes/sec).
    /// Sums across clients to the server's total uplink.</summary>
    [Gauge("Client → server throughput", Unit = "B/s")]
    public double UpBytesPerSec { get; set; }

    /// <summary>Client → server windowed-max capacity estimate (bytes/sec).</summary>
    [Gauge("Client → server capacity estimate (windowed max)", Unit = "B/s")]
    public double UpEstimateBytesPerSec { get; set; }

    /// <summary>Seconds since this connection was established. Per-client, so it is
    /// not summed across instances; the longest-lived value is kept over time.</summary>
    [Gauge("Connection age", Unit = "s", AcrossInstances = StatAggregation.None, OverTime = TimeAggregation.Max)]
    public double ConnectedSeconds { get; set; }
}

using PluginSdk.Stats;

namespace ServerPlugin.Stats;

/// <summary>
/// The single-instance "server" group of a bandwidth snapshot: process-wide facts
/// that frame the per-client rows. Captured once per publish into a
/// <see cref="StatGroup"/> with exactly one <see cref="StatInstance"/> labelled
/// <c>"server"</c>.
/// </summary>
public sealed class BandwidthServerStats
{
    /// <summary>Constant label identifying this single server-scope instance.</summary>
    [StatLabel("Telemetry scope")]
    public string Scope { get; set; }

    /// <summary>Number of connected clients at publish time.</summary>
    [Gauge("Connected clients")]
    public int ClientCount { get; set; }

    /// <summary>Sim/replication tick rate (~60 Hz). A configuration-like constant,
    /// so it is folded with "last" over time and not summed across instances.</summary>
    [Discrete("Replication tick rate", Unit = "Hz", OverTime = TimeAggregation.Last)]
    public int TickRateHz { get; set; }

    /// <summary>Whether outgoing pacing is currently enforced. Always false in this
    /// observe-only build; published so a consumer can tell measurement from pacing.</summary>
    [Discrete("Outgoing pacing currently enforced", OverTime = TimeAggregation.Last)]
    public bool LimiterActive { get; set; }
}

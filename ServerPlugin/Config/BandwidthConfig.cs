using PluginSdk.Config;
using Shared.Config;

namespace ServerPlugin.Config;

/// <summary>
/// Outgoing-traffic pacing strategy. <see cref="Off"/> (the safe default) is observe-only;
/// <see cref="PacketBudget"/> is the implemented adaptive AIMD limiter (Phase 2,
/// <c>Docs/BandwidthEstimator.md</c> §6.4/§7.2). <see cref="TokenBucket"/> is reserved and
/// currently falls back to <see cref="PacketBudget"/>. The value is stored by member name, so
/// reordering the enum never breaks an existing config.
/// </summary>
public enum BandwidthLimiterMode
{
    /// <summary>No pacing. The server sends exactly as the engine produces (observe-only).</summary>
    [EnumCaption("Off (observe-only)")]
    Off,

    /// <summary>Adaptive AIMD per-client packet budget (the §7.2 O2 limiter). Paces only unreliable
    /// state sync, never above the stock budget.</summary>
    [EnumCaption("Packet budget (adaptive AIMD)")]
    PacketBudget,

    /// <summary>Reserved: token-bucket pacing. Not yet implemented; falls back to
    /// <see cref="PacketBudget"/>.</summary>
    [EnumCaption("Token bucket (reserved → packet budget)")]
    TokenBucket,
}

/// <summary>
/// Server-side configuration for the per-client bandwidth estimator and telemetry
/// surface, declared through Magnetar's PluginSdk. Admins edit it remotely via
/// Quasar, which renders the UI from the layout declared below; it is also written
/// to <c>Bandwidth.cfg</c> in the server's user data directory in the sparse
/// "only non-default values" XML form.
///
/// <para>
/// It implements the shared <see cref="IPluginConfig"/> so the rest of the plugin
/// reads it via <c>Plugin.Common.Config</c>. <see cref="INotifyPropertyChanged"/>
/// is provided by the PluginSdk base, so a remote edit (e.g. toggling
/// <see cref="Enabled"/>) raises a change notification that the server plugin
/// pushes straight into the running monitor — enabling/disabling at runtime with no
/// restart.
/// </para>
///
/// <para>
/// The base class is fully qualified because the Shared project also defines a
/// <c>Shared.Config.PluginConfig</c> (the client plugin's config), so the bare name
/// would be ambiguous here where both namespaces are imported.
/// </para>
/// </summary>
[Section("core", caption: "Bandwidth measurement")]
public class BandwidthConfig : PluginSdk.Config.PluginConfig, IPluginConfig
{
    /// <summary>Master switch. When false the estimator records nothing, the
    /// periodic publish stops, and the published telemetry is cleared. Toggling
    /// this at runtime takes effect immediately (no restart).</summary>
    [BoolOption("Enable bandwidth measurement and telemetry publishing", Parent = "core")]
    public bool Enabled { get; set => SetField(ref field, value); } = true;

    /// <summary>How often (milliseconds) a fresh telemetry snapshot is computed and
    /// published. Also the BBR delivery-rate sampling interval.</summary>
    [IntOption(100, 60000, description: "Telemetry publish / rate-sampling interval in milliseconds")]
    public int PublishIntervalMs { get; set => SetField(ref field, value); } = 1000;

    /// <summary>Length of the windowed-max delivery-rate window, in publish
    /// intervals. With the defaults this is an 8-second look-back.</summary>
    [IntOption(1, 64, description: "Windowed-max delivery-rate window length, in publish intervals")]
    public int WindowSize { get; set => SetField(ref field, value); } = 8;

    /// <summary>When true the client Steam ID is replaced in published telemetry by
    /// a stable per-process anonymous hash, so a consumer can still follow a client
    /// across snapshots without learning who it is.</summary>
    [BoolOption("Redact client Steam IDs in telemetry (publish a stable anonymous hash instead)", Parent = "core")]
    public bool RedactClientId { get; set => SetField(ref field, value); } = false;

    /// <summary>Outgoing pacing strategy. <see cref="BandwidthLimiterMode.Off"/> (default) never
    /// changes what the server sends; <see cref="BandwidthLimiterMode.PacketBudget"/> enables the
    /// adaptive AIMD limiter. The limiter only ever paces a client down (never above the stock
    /// budget) and only unreliable state sync, so enabling it cannot make delivery worse than
    /// stock.</summary>
    [EnumOption("Outgoing pacing strategy (Off = observe-only; Packet budget = adaptive AIMD limiter)", Parent = "core")]
    public BandwidthLimiterMode LimiterMode { get; set => SetField(ref field, value); } = BandwidthLimiterMode.Off;

    /// <summary>AIMD lower bound on the per-client operating target (R_min, bytes/sec). The packet
    /// budget never falls below 1 regardless, so this bounds the worst-case pacing of a stalling
    /// client.</summary>
    [DoubleOption(1024.0, 16777216.0, description: "Limiter: minimum operating target rate (R_min), bytes/sec")]
    public double RateFloorBytesPerSec { get; set => SetField(ref field, value); } = 32768.0;

    /// <summary>AIMD upper bound on the per-client operating target (R_max, bytes/sec). At or above
    /// this the packet budget clamps to the stock 7, so an unconstrained client behaves as
    /// stock.</summary>
    [DoubleOption(1024.0, 16777216.0, description: "Limiter: maximum operating target rate (R_max), bytes/sec")]
    public double RateMaxBytesPerSec { get; set => SetField(ref field, value); } = 524288.0;

    /// <summary>Initial operating target (R_target, bytes/sec) for a freshly connected client, before
    /// the controller has adapted.</summary>
    [DoubleOption(1024.0, 16777216.0, description: "Limiter: initial operating target rate for a new connection, bytes/sec")]
    public double RatePriorBytesPerSec { get; set => SetField(ref field, value); } = 262144.0;

    /// <summary>AIMD additive-increase rate (bytes/sec gained per second of elapsed time) used to
    /// probe upward when no overuse is detected.</summary>
    [DoubleOption(0.0, 16777216.0, description: "Limiter: AIMD additive increase, bytes/sec per second")]
    public double AimdIncreaseBytesPerSec2 { get; set => SetField(ref field, value); } = 65536.0;

    /// <summary>AIMD multiplicative-decrease factor in (0, 1) applied to the operating target on
    /// sustained overuse (e.g. 0.85 backs off 15% per stalled window).</summary>
    [DoubleOption(0.05, 0.99, description: "Limiter: AIMD multiplicative decrease factor (0..1)")]
    public double AimdDecreaseFactor { get; set => SetField(ref field, value); } = 0.85;

    /// <summary>Fraction of serviced ticks in a window that must stall (ACK window exhausted) before
    /// the controller treats the window as overuse and backs off.</summary>
    [DoubleOption(0.01, 1.0, description: "Limiter: stall fraction that triggers AIMD back-off (0..1)")]
    public double StallBackoffFraction { get; set => SetField(ref field, value); } = 0.2;
}

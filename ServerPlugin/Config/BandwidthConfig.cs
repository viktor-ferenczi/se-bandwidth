using PluginSdk.Config;
using Shared.Config;

namespace ServerPlugin.Config;

/// <summary>
/// Outgoing-traffic pacing strategy. This build implements only <see cref="Off"/>
/// (observe-only, the safe default — see <c>Docs/BandwidthEstimator.md</c> §12);
/// the remaining modes are reserved so enabling pacing in a later version does not
/// change the config schema. The value is stored by member name, so reordering the
/// enum never breaks an existing config.
/// </summary>
public enum BandwidthLimiterMode
{
    /// <summary>No pacing. The server sends exactly as the engine produces.</summary>
    [EnumCaption("Off (observe-only)")]
    Off,

    /// <summary>Reserved: per-tick byte budget (not implemented in this build).</summary>
    [EnumCaption("Packet budget (reserved)")]
    PacketBudget,

    /// <summary>Reserved: token-bucket pacing (not implemented in this build).</summary>
    [EnumCaption("Token bucket (reserved)")]
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

    /// <summary>Outgoing pacing strategy. This build honours only
    /// <see cref="BandwidthLimiterMode.Off"/>; the estimator never changes what the
    /// server sends regardless of this value. Reserved for a future pacing release.</summary>
    [EnumOption("Outgoing pacing strategy (reserved; this build is observe-only and never paces traffic)", Parent = "core")]
    public BandwidthLimiterMode LimiterMode { get; set => SetField(ref field, value); } = BandwidthLimiterMode.Off;
}

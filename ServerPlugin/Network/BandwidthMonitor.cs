using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using PluginSdk.Stats;
using ServerPlugin.Config;
using ServerPlugin.Stats;
using Shared.Plugin;

namespace ServerPlugin.Network;

/// <summary>
/// Process-lifetime registry of per-client bandwidth trackers and the periodic
/// publisher that projects them into self-describing <see cref="StatsSnapshot"/>s
/// published through Magnetar's generic <see cref="PluginStats"/> transport under
/// the <see cref="ProviderName"/> provider.
///
/// <para>
/// The byte-counting hot paths (<see cref="RecordDown"/>/<see cref="RecordUp"/>)
/// are fed by Harmony patches on the replication send/receive chokepoints, applied
/// before world load, so all client traffic is covered from the very first packet.
/// The lifecycle hooks (<see cref="OnClientConnected"/>/<see cref="OnClientDisconnected"/>)
/// are fed by patches on <c>MyReplicationServer</c>. <see cref="Update"/> is driven
/// from the engine update loop and republishes on the configured interval.
/// </para>
///
/// <para>
/// This build is observe-only with the limiter off (<c>Docs/BandwidthEstimator.md</c>
/// §12): this type measures and publishes telemetry but never changes what the
/// server sends. It is safe at default.
/// </para>
/// </summary>
internal static class BandwidthMonitor
{
    /// <summary>Provider name this plugin publishes its stats under. A consumer
    /// (Quasar, or another plugin) reads the latest snapshot by this name.</summary>
    public const string ProviderName = "bandwidth";

    // Sim/replication tick rate (~60 Hz). Constant for this build; surfaced in the
    // server stats for consumers that annotate per-tick budgets.
    private const int TickRateHz = 60;

    private static readonly ConcurrentDictionary<ulong, ConnectionTracker> Live = new();

    // Last epoch handed out per Steam ID, so a reconnect always gets a higher one.
    private static readonly ConcurrentDictionary<ulong, uint> Epochs = new();

    // Configuration — safe defaults until the host calls Configure.
    private static volatile bool enabled = true;
    private static int publishIntervalMs = 1000;
    private static int windowSize = 8;
    private static bool redactClientId;
    private static BandwidthLimiterMode limiterMode = BandwidthLimiterMode.Off;

    private static long lastPublishTicks = Stopwatch.GetTimestamp();

    /// <summary>Apply host configuration. Called at startup before any client
    /// connects, and again whenever the live config changes (so enable/disable and
    /// the tuning parameters take effect at runtime). Disabling clears the published
    /// telemetry so a consumer sees the provider go quiet, and forces the limiter off.</summary>
    public static void Configure(
        bool enabled, int publishIntervalMs, int windowSize, bool redactClientId,
        BandwidthLimiterMode limiterMode,
        double rateFloorBytesPerSec, double rateMaxBytesPerSec, double ratePriorBytesPerSec,
        double aimdIncreaseBytesPerSec2, double aimdDecreaseFactor, double stallBackoffFraction)
    {
        BandwidthMonitor.enabled = enabled;
        BandwidthMonitor.publishIntervalMs = publishIntervalMs > 0 ? publishIntervalMs : 1000;
        BandwidthMonitor.windowSize = windowSize > 0 ? windowSize : 8;
        BandwidthMonitor.redactClientId = redactClientId;
        BandwidthMonitor.limiterMode = limiterMode;

        // Push the AIMD tunables into the controller statics. Order the bounds so Floor <= Max even
        // if a misconfiguration inverts them, and keep the decrease factor a true contraction.
        double floor = rateFloorBytesPerSec > 0.0 ? rateFloorBytesPerSec : 1.0;
        double max = rateMaxBytesPerSec > floor ? rateMaxBytesPerSec : floor;
        RateController.Floor = floor;
        RateController.Max = max;
        RateController.Prior = Math.Min(max, Math.Max(floor, ratePriorBytesPerSec));
        RateController.Alpha = aimdIncreaseBytesPerSec2 > 0.0 ? aimdIncreaseBytesPerSec2 : 0.0;
        RateController.Beta = aimdDecreaseFactor > 0.0 && aimdDecreaseFactor < 1.0 ? aimdDecreaseFactor : 0.85;
        RateController.StallFraction = stallBackoffFraction > 0.0 ? stallBackoffFraction : 0.2;

        // The limiter only enforces when the plugin is enabled; disabling the plugin disables pacing
        // regardless of the configured mode, so replication returns to byte-for-byte stock.
        BandwidthLimiter.Configure(enabled ? limiterMode : BandwidthLimiterMode.Off);

        if (!enabled)
            PluginStats.Clear(ProviderName);
    }

    /// <summary>Look up a live connection by Steam ID. Used by the limiter and the ACK-stall
    /// postfix on their hot paths.</summary>
    internal static bool TryGetConnection(ulong steamId, out ConnectionTracker connection)
        => Live.TryGetValue(steamId, out connection);

    // ---- hot paths (callable from any thread) ----

    /// <summary>Record server → client bytes. The reliability class is captured
    /// for the future limiter (§7.7); this build's telemetry sums both classes.</summary>
    public static void RecordDown(ulong steamId, int bytes, bool reliable)
    {
        if (!enabled || bytes <= 0)
            return;
        if (Live.TryGetValue(steamId, out var connection))
            connection.Down.Add(bytes);
    }

    /// <summary>Record client → server bytes.</summary>
    public static void RecordUp(ulong steamId, int bytes)
    {
        if (!enabled || bytes <= 0)
            return;
        if (Live.TryGetValue(steamId, out var connection))
            connection.Up.Add(bytes);
    }

    // ---- lifecycle (engine update thread) ----

    /// <summary>A client connected: bump its epoch and start a fresh tracker.</summary>
    public static void OnClientConnected(ulong steamId)
    {
        if (steamId == 0UL)
            return;
        uint epoch = Epochs.AddOrUpdate(steamId, 1U, (_, previous) => previous + 1U);
        Live[steamId] = new ConnectionTracker(steamId, epoch, windowSize);
    }

    /// <summary>A client disconnected: drop it from the live registry.</summary>
    public static void OnClientDisconnected(ulong steamId)
    {
        if (Live.TryRemove(steamId, out var connection))
            connection.Closed = true;
    }

    // ---- publish (engine update thread) ----

    /// <summary>Republish a snapshot if the configured interval has elapsed.
    /// Cheap to call every frame.</summary>
    public static void Update()
    {
        if (!enabled)
            return;

        long now = Stopwatch.GetTimestamp();
        double elapsedMs = (now - lastPublishTicks) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs < publishIntervalMs)
            return;

        lastPublishTicks = now;

        try
        {
            Publish(elapsedMs / 1000.0);
        }
        catch (Exception e)
        {
            // Telemetry must never disrupt the update loop.
            Common.Logger?.Error(e, "BandwidthMonitor publish failed");
        }
    }

    private static void Publish(double dtSeconds)
    {
        var clientStats = new List<BandwidthClientStats>(Live.Count);
        foreach (var connection in Live.Values)
        {
            connection.Down.Sample(dtSeconds);
            connection.Up.Sample(dtSeconds);

            // Step the downlink AIMD controller over the same interval. It runs even when pacing is
            // off, so the published target/budget are a live preview of what the limiter would do.
            connection.Control.Update(connection.Down.AchievedBytesPerSec, dtSeconds);

            clientStats.Add(new BandwidthClientStats
            {
                Client = FormatClientLabel(connection.SteamId),
                ConnectionEpoch = connection.Epoch,
                ConnectedSeconds = connection.ConnectedSeconds,
                DownBytesPerSec = connection.Down.AchievedBytesPerSec,
                DownEstimateBytesPerSec = connection.Down.EstimateBytesPerSec,
                DownConfidence = connection.Down.Confidence,
                UpBytesPerSec = connection.Up.AchievedBytesPerSec,
                UpEstimateBytesPerSec = connection.Up.EstimateBytesPerSec,
                TargetBytesPerSec = connection.Control.TargetBytesPerSec,
                PacketBudget = BandwidthLimiter.BudgetFromTarget(connection.Control.TargetBytesPerSec),
            });
        }

        var serverStats = new[]
        {
            new BandwidthServerStats
            {
                Scope = "server",
                ClientCount = clientStats.Count,
                TickRateHz = TickRateHz,
                // True when an opt-in pacing mode is active on the enabled plugin.
                LimiterActive = enabled && limiterMode != BandwidthLimiterMode.Off,
            }
        };

        var snapshot = new StatsSnapshot { UtcTimestamp = DateTime.UtcNow };
        snapshot.Groups.Add(StatsSchema.Build(typeof(BandwidthServerStats)).CaptureGroup(serverStats));
        snapshot.Groups.Add(StatsSchema.Build(typeof(BandwidthClientStats)).CaptureGroup(clientStats));

        PluginStats.Publish(ProviderName, snapshot);
    }

    /// <summary>
    /// Renders a client's identity for telemetry. With redaction off this is the raw
    /// Steam ID; with redaction on it is a stable per-process anonymous token derived
    /// from the Steam ID by a 64-bit FNV-1a hash, so a consumer can still key a time
    /// series on one client across snapshots without learning who it is.
    /// </summary>
    private static string FormatClientLabel(ulong steamId)
    {
        if (!redactClientId)
            return steamId.ToString(CultureInfo.InvariantCulture);

        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        for (int i = 0; i < 8; i++)
        {
            hash ^= (steamId >> (i * 8)) & 0xFFUL;
            hash *= prime;
        }

        return "anon-" + hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>Clear all state and remove the published telemetry. Called on
    /// shutdown (plugin Dispose).</summary>
    public static void Reset()
    {
        Live.Clear();
        Epochs.Clear();
        PluginStats.Clear(ProviderName);
    }
}

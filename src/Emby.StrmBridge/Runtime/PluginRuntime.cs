using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Extraction;
using Emby.StrmBridge.Persistence;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Policy;
using MediaBrowser.Model.Logging;

namespace Emby.StrmBridge.Runtime;

public sealed class PluginRuntime : IDisposable
{
    internal const int MaximumDetectedRedirectHosts = 32;
    private readonly object sync = new();
    private readonly ReaderWriterLockSlim mediaCommitFence = new();
    private readonly object detectedRedirectHostsSync = new();
    private readonly List<string> detectedRedirectHosts = new();
    private CancellationTokenSource operationCancellation = new();
    private PluginConfiguration options = new();
    private int generation;
    private int pendingHostNotificationState;
    private PlaybackPatchStatus playbackPatchStatus;
    private string hostAbi = "unavailable";
    private HarmonyPatchHost? playbackPatch;
    private bool disposed;

    public IClock Clock { get; private set; } = new SystemClock();

    public StrmSourcePolicy? SourcePolicy { get; private set; }

    public MediaInfoStore? MediaInfoStore { get; private set; }

    public ExtractionStateStore? ExtractionState { get; private set; }

    public TicketStore Tickets { get; private set; } = new(new SystemClock());

    public GatewayTransport? Gateway { get; private set; }

    internal Emby.StrmBridge.Subtitles.SubtitleRequestProcessor? SubtitleRequests { get; set; }

    internal Emby.StrmBridge.Subtitles.SubtitleCoordinator? Subtitles { get; set; }

    internal Emby.StrmBridge.Subtitles.SubtitlePatchHost? SubtitlePatch { get; set; }

    internal FastSeekCoordinator? FastSeek { get; private set; }

    public ExtractionCoordinator? Extraction { get; internal set; }

    internal MaintenanceService? Maintenance { get; set; }

    public string? DataDirectory { get; private set; }

    internal bool SubtitleInputsReady => playbackPatch?.SubtitleInputsReady == true;

    public bool IsInitialized => SourcePolicy is not null && Gateway is not null;

    public int Generation
    {
        get { lock (sync) return generation; }
    }

    public PlaybackHealthSnapshot GetPlaybackHealth()
    {
        lock (sync)
        {
            var prefixDiagnostics = playbackPatch?.GetProgressivePrefixDiagnostics() ??
                                    ProgressivePrefixDiagnostics.Unavailable;
            return new PlaybackHealthSnapshot(
                playbackPatchStatus,
                hostAbi,
                generation,
                Tickets.Count,
                Gateway?.ActiveRequests ?? 0,
                prefixDiagnostics);
        }
    }

    internal void SetPlaybackHealth(PlaybackPatchStatus status, string abi)
    {
        lock (sync)
        {
            if (disposed) return;
            playbackPatchStatus = status;
            hostAbi = string.IsNullOrWhiteSpace(abi) ? "unavailable" : abi;
        }
    }

    internal void AttachPlaybackPatch(HarmonyPatchHost patch)
    {
        if (patch is null) throw new ArgumentNullException(nameof(patch));
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(PluginRuntime));
            if (playbackPatch is not null) throw new InvalidOperationException("The playback patch is already attached.");
            playbackPatch = patch;
        }
    }

    public PluginConfiguration GetOptionsSnapshot()
    {
        lock (sync) return options.Snapshot();
    }

    public void UpdateOptions(PluginConfiguration updatedOptions, bool invalidateSensitiveState)
    {
        if (updatedOptions is null) throw new ArgumentNullException(nameof(updatedOptions));
        using var fence = AcquireMediaCommitFence(write: true);
        CancellationTokenSource? previous = null;
        lock (sync)
        {
            if (disposed) return;
            var redirectTrustChanged = !(options.AllowedRedirectHosts ?? Array.Empty<string>()).SequenceEqual(
                updatedOptions.AllowedRedirectHosts ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            options = updatedOptions.Snapshot();
            if (redirectTrustChanged) pendingHostNotificationState = 0;
            if (!invalidateSensitiveState) return;
            generation++;
            previous = operationCancellation;
            operationCancellation = new CancellationTokenSource();
            Tickets.Clear();
            Gateway?.Clear();
            FastSeek?.Clear();
            if (!options.Enabled) ClearDetectedRedirectHosts();
        }
        previous.Cancel();
        previous.Dispose();
    }

    public void Initialize(string configurationDirectory, IClock? clock = null)
    {
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(PluginRuntime));
            if (IsInitialized) return;
            Clock = clock ?? new SystemClock();
            DataDirectory = Path.Combine(configurationDirectory, "Emby.StrmBridge");
            Directory.CreateDirectory(DataDirectory);
            var identity = HmacIdentityProvider.LoadOrCreate(Path.Combine(DataDirectory, "identity.key"));
            SourcePolicy = new StrmSourcePolicy(identity);
            MediaInfoStore = new MediaInfoStore(Path.Combine(DataDirectory, "mediainfo"), new SnapshotSerializer());
            ExtractionState = new ExtractionStateStore(Path.Combine(DataDirectory, "state", "extraction-state.json"));
            Tickets = new TicketStore(Clock);
            var redirectPolicy = new RedirectPolicy(
                () => GetOptionsSnapshot().AllowedRedirectHosts,
                RecordDetectedRedirectHost);
            Gateway = new GatewayTransport(redirectPolicy, Clock);
        }
    }

    internal void InitializeFastSeek(ILogger logger, IFastSeekProbeClient? probeClient = null)
    {
        if (logger is null) throw new ArgumentNullException(nameof(logger));
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(PluginRuntime));
            if (FastSeek is not null) return;
            var gateway = Gateway ?? throw new InvalidOperationException("The gateway is unavailable.");
            FastSeek = new FastSeekCoordinator(
                probeClient ?? new GatewayFastSeekProbeClient(gateway),
                Clock,
                logger,
                () => Generation);
            gateway.RepresentationChanged = source => FastSeek?.InvalidateSource(source);
        }
    }

    internal void RecordDetectedRedirectHost(string host)
    {
        var normalized = PluginConfiguration.NormalizeHost(host);
        if (!PluginConfiguration.IsValidExactHost(normalized)) return;
        lock (sync)
        {
            if (disposed || !options.Enabled) return;
            lock (detectedRedirectHostsSync)
            {
                var alreadyDetected = detectedRedirectHosts.RemoveAll(value =>
                    string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase)) > 0;
                detectedRedirectHosts.Add(normalized);
                if (detectedRedirectHosts.Count > MaximumDetectedRedirectHosts)
                    detectedRedirectHosts.RemoveAt(0);
                if (!alreadyDetected) Interlocked.Exchange(ref pendingHostNotificationState, 0);
            }
        }
    }

    internal string[] GetDetectedRedirectHosts(IEnumerable<string>? excludedHosts = null)
    {
        var excluded = (excludedHosts ?? Array.Empty<string>()).ToArray();
        lock (detectedRedirectHostsSync)
            return detectedRedirectHosts
                .Where(host => !PluginConfiguration.IsHostAllowed(host, excluded))
                .Reverse()
                .ToArray();
    }

    internal bool TryBeginPendingHostNotification() =>
        Interlocked.CompareExchange(ref pendingHostNotificationState, 1, 0) == 0;

    internal void AbandonPendingHostNotification() =>
        Interlocked.Exchange(ref pendingHostNotificationState, 0);

    private void ClearDetectedRedirectHosts()
    {
        lock (detectedRedirectHostsSync) detectedRedirectHosts.Clear();
        Interlocked.Exchange(ref pendingHostNotificationState, 0);
    }

    public OperationContext BeginOperation()
    {
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(PluginRuntime));
            return new OperationContext(generation, operationCancellation.Token);
        }
    }

    public bool IsOperationCurrent(int operationGeneration)
    {
        lock (sync)
            return !disposed && operationGeneration == generation && !operationCancellation.IsCancellationRequested;
    }

    public bool TryCommit(int operationGeneration, Func<bool> predicate, Action action)
    {
        if (predicate is null) throw new ArgumentNullException(nameof(predicate));
        if (action is null) throw new ArgumentNullException(nameof(action));
        lock (sync)
        {
            if (disposed || operationGeneration != generation || operationCancellation.IsCancellationRequested || !predicate())
                return false;
            action();
            return true;
        }
    }

    internal bool TryCommitMediaInfo(int operationGeneration, Func<bool> predicate, Action action)
    {
        using var fence = AcquireMediaCommitFence(write: false);
        if (!IsOperationCurrent(operationGeneration) || !predicate()) return false;
        action();
        return true;
    }

    private IDisposable AcquireMediaCommitFence(bool write)
    {
        if (write) mediaCommitFence.EnterWriteLock();
        else mediaCommitFence.EnterReadLock();
        return new MediaCommitLease(mediaCommitFence, write);
    }

    private sealed class MediaCommitLease : IDisposable
    {
        private readonly ReaderWriterLockSlim fence;
        private readonly bool write;

        public MediaCommitLease(ReaderWriterLockSlim fence, bool write)
        {
            this.fence = fence;
            this.write = write;
        }

        public void Dispose()
        {
            if (write) fence.ExitWriteLock();
            else fence.ExitReadLock();
        }
    }

    public void ClearSensitiveState()
    {
        using var fence = AcquireMediaCommitFence(write: true);
        CancellationTokenSource previous;
        lock (sync)
        {
            if (disposed) return;
            generation++;
            previous = operationCancellation;
            operationCancellation = new CancellationTokenSource();
            Tickets.Clear();
            Gateway?.Clear();
            FastSeek?.Clear();
            ClearDetectedRedirectHosts();
        }
        previous.Cancel();
        previous.Dispose();
    }

    public void Dispose()
    {
        CancellationTokenSource previous;
        ExtractionCoordinator? extraction;
        MaintenanceService? maintenance;
        HarmonyPatchHost? patch;
        using (AcquireMediaCommitFence(write: true))
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                generation++;
                previous = operationCancellation;
                extraction = Extraction;
                maintenance = Maintenance;
                Maintenance = null;
                patch = playbackPatch;
                playbackPatch = null;
            }
        previous.Cancel();
        maintenance?.Dispose();
        patch?.Dispose();
        SubtitlePatch?.Dispose();
        Subtitles?.Dispose();
        extraction?.CancelAndDrain();
        previous.Dispose();
        Tickets.Clear();
        FastSeek?.Clear();
        Gateway?.Dispose();
        ClearDetectedRedirectHosts();
        Gateway = null;
        FastSeek = null;
        Extraction = null;
    }
}

public readonly struct OperationContext
{
    public OperationContext(int generation, CancellationToken cancellationToken)
    {
        Generation = generation;
        CancellationToken = cancellationToken;
    }

    public int Generation { get; }

    public CancellationToken CancellationToken { get; }
}

public readonly struct PlaybackHealthSnapshot
{
    public PlaybackHealthSnapshot(
        PlaybackPatchStatus patchStatus,
        string hostAbi,
        int runtimeGeneration,
        int ticketCount,
        int activeRelayCount,
        ProgressivePrefixDiagnostics prefixDiagnostics)
    {
        PatchStatus = patchStatus;
        HostAbi = hostAbi;
        RuntimeGeneration = runtimeGeneration;
        TicketCount = ticketCount;
        ActiveRelayCount = activeRelayCount;
        PrefixDiagnosticsAvailable = prefixDiagnostics.IsAvailable;
        ExternalPrefixCount = prefixDiagnostics.ExternalPrefixCount;
        NativePrefixInstalled = prefixDiagnostics.NativePrefixInstalled;
        NativePrefixPriority = prefixDiagnostics.NativePrefixPriority;
        HighestExternalPrefixPriority = prefixDiagnostics.HighestExternalPrefixPriority;
        NativePrefixUncontended = prefixDiagnostics.NativePrefixUncontended;
        NativePrefixPriorityStrictlyHigher = prefixDiagnostics.NativePrefixPriorityStrictlyHigher;
    }

    public PlaybackPatchStatus PatchStatus { get; }

    public string HostAbi { get; }

    public int RuntimeGeneration { get; }

    public int TicketCount { get; }

    public int ActiveRelayCount { get; }

    public bool PrefixDiagnosticsAvailable { get; }

    public int ExternalPrefixCount { get; }

    public bool NativePrefixInstalled { get; }

    public int? NativePrefixPriority { get; }

    public int? HighestExternalPrefixPriority { get; }

    public bool NativePrefixUncontended { get; }

    public bool NativePrefixPriorityStrictlyHigher { get; }
}

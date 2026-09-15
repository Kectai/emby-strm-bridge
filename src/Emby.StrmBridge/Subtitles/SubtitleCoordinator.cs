using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Logging;

namespace Emby.StrmBridge.Subtitles;

internal sealed class SubtitleRequestContext
{
    internal int Generation;
    internal Guid ItemId;
    internal Guid RequestedItemId;
    internal Guid UserId;
    internal Func<bool>? StillAuthorized;
    // Informational playlist seek hint; HLS segment starts intentionally differ.
    internal long VideoStartTicks;
    internal bool NativeHlsClock;
    internal long? MseTimestampOffsetTicks;
    internal string PlaySessionId = string.Empty;
    internal string MediaSourceId = string.Empty;
    internal SourceIdentity Source = null!;
    internal int Index;
    internal string StreamFingerprint = "";
    internal string Codec = string.Empty;
    internal long Start;
    internal long End;

}

internal sealed class SubtitleCoordinator : IDisposable
{
    private readonly PluginRuntime runtime;
    private readonly IServerApplicationHost host;
    internal SharedSubtitleOutput Shared { get; }
    private readonly ILogger logger;
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, SubtitlePlaybackSession> sessions = new(StringComparer.Ordinal);
    internal string Status { get; set; } = "NativeOnly";
    internal int ActiveJobs => Shared.ActiveJobs;
    internal SubtitleCoordinator(PluginRuntime runtime, IServerApplicationHost host, IServerApplicationPaths paths, ILogger logger)
    {
        this.runtime = runtime; this.host = host; this.logger = logger;
        Shared = new SharedSubtitleOutput(runtime, Path.Combine(runtime.DataDirectory ?? throw new InvalidOperationException("Subtitle storage unavailable."), "subtitle-output"), logger);
    }
    internal SubtitlePlaybackSession CreateSession(SubtitleRequestContext context)
    {
        if (context.NativeHlsClock) throw new SubtitleProblem("outside-scope");
        if (string.IsNullOrEmpty(context.PlaySessionId) || !Shared.HasSession(context))
            throw new SubtitleProblem("video-input-unavailable");
        lock (sync)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            foreach (var key in sessions.Where(p => DateTimeOffset.UtcNow - p.Value.LastUsed > TimeSpan.FromMinutes(10)).Select(p => p.Key).ToArray())
            { sessions[key].Stop(); sessions.Remove(key); }
            if (sessions.Count >= 64) throw new SubtitleBusyException();
            var session = new SubtitlePlaybackSession(context);
            sessions.Add(session.Id, session);
            return session;
        }
    }
    internal SubtitlePlaybackSession? FindSession(string id, Guid user)
    {
        lock (sync)
        {
            if (!sessions.TryGetValue(id, out var session) || session.Context.UserId != user || session.Token.IsCancellationRequested ||
                DateTimeOffset.UtcNow - session.LastUsed > TimeSpan.FromMinutes(10) || !runtime.IsOperationCurrent(session.Context.Generation)) return null;
            session.LastUsed = DateTimeOffset.UtcNow;
            return session;
        }
    }
    internal bool EndSession(string id, Guid user)
    {
        lock (sync)
        {
            if (!sessions.TryGetValue(id, out var session) || session.Context.UserId != user) return false;
            sessions.Remove(id); session.Stop(); return true;
        }
    }
    internal async Task<Stream> OpenAsync(SubtitleRequestContext context, CancellationToken token, CancellationToken sessionToken)
    {
        context.StillAuthorized = () => CheckAccess(context);
        if (!CheckAccess(context)) throw new SubtitleProblem("authorization-changed");
        var operation = runtime.BeginOperation();
        if (operation.Generation != context.Generation) throw new SubtitleProblem("source-changed");
        var linked = CancellationTokenSource.CreateLinkedTokenSource(token, sessionToken, operation.CancellationToken, lifetime.Token);
        try { var stream = await Shared.OpenAsync(context, linked.Token).ConfigureAwait(false); stream.RequestLifetime = linked; return stream; }
        catch { linked.Dispose(); throw; }
    }
    private bool CheckAccess(SubtitleRequestContext request)
    {
        var user = host.TryResolve<MediaBrowser.Controller.Library.IUserManager>()?.GetUserById(request.UserId);
        var library = host.TryResolve<MediaBrowser.Controller.Library.ILibraryManager>();
        if (user is null || user.Policy.IsDisabled || !user.Policy.EnableMediaPlayback || library is null) return false;
        foreach (var id in new[] { request.ItemId, request.RequestedItemId })
        {
            var item = library.GetItemById(id);
            if (item is null || !item.IsVisible(user) || !library.GetCollectionFolders(item).Any(folder =>
                runtime.GetOptionsSnapshot().IncludedLibraryIds.Any(value => Guid.TryParse(value, out var selected) && selected == folder.Id))) return false;
        }
        return runtime.IsOperationCurrent(request.Generation) &&
            request.Source.HasSameFileVersion(runtime.SourcePolicy!.Read(request.Source.LocalPath));
    }
    internal int ClearCache()
    {
        lock (sync)
        {
            foreach (var session in sessions.Values) session.Stop();
            sessions.Clear();
        }
        var count = Shared.ActiveJobs;
        Shared.Clear();
        return count;
    }
    public void Dispose()
    {
        lifetime.Cancel();
        ClearCache(); Shared.Dispose();
    }

}

internal sealed class SubtitlePlaybackSession
{
    internal readonly string Id = Guid.NewGuid().ToString("N");
    internal readonly SubtitleRequestContext Context;
    private readonly CancellationTokenSource cancellation = new();
    internal CancellationToken Token { get; }
    private int stopped;
    internal DateTimeOffset LastUsed = DateTimeOffset.UtcNow;
    internal SubtitlePlaybackSession(SubtitleRequestContext context) { Context = context; Token = cancellation.Token; }
    internal void Stop() { if (Interlocked.Exchange(ref stopped, 1) != 0) return; cancellation.Cancel(); cancellation.Dispose(); }
}

internal sealed class SubtitleBusyException : Exception { }
internal sealed class SubtitleProblem : Exception
{
    internal string Reason { get; }
    internal SubtitleProblem(string reason) : base(reason) { Reason = reason; }
}

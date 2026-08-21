using System.Collections.Concurrent;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Extraction;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Notifications;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class ExtractionCoordinatorTests
{
    [TestMethod]
    public async Task PostScan_DoesNotQueueWorkWhenScanWasCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new ExtractionPostScanTask().Run(null!, cancellation.Token));
    }

    [TestMethod]
    public async Task Extract_AcceptsDirectMediaResponseAndUsesRepositoryUpdateWithoutDroppingExternalStreams()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Write("direct.strm", "https://source.invalid/media");
        var external = new MediaStream
        {
            Type = MediaStreamType.Subtitle,
            Index = 7,
            IsExternal = true,
            Path = Path.Combine(workspace.Path, "subtitle.srt"),
        };
        var item = CreateItem(path, new List<MediaStream> { external });
        var fixture = CreateFixture(workspace, new[] { item }, _ => new RedirectSourceResponse(206, null, null));

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Extracted,
            $"failed={result.Failed}, skipped={result.Skipped}, source={fixture.SourceClient.Calls}, probe={fixture.ProbeCalls()}, update={fixture.UpdateCalls()}");
        Assert.AreEqual(1, fixture.SourceClient.Calls);
        Assert.AreEqual(1, fixture.ProbeCalls());
        Assert.AreEqual(1, fixture.UpdateCalls());
        Assert.AreEqual(1, fixture.MediaStreamSaveCalls());
        Assert.IsTrue(item.MediaStreams.Any(stream => stream.Type == MediaStreamType.Video));
        Assert.IsTrue(item.MediaStreams.Any(stream => stream.IsExternal && stream.Path == external.Path));
        Assert.IsTrue(fixture.LastSavedStreams().Any(stream => stream.Type == MediaStreamType.Video));
        Assert.IsTrue(fixture.LastSavedStreams().Any(stream => stream.IsExternal && stream.Path == external.Path));
        var source = fixture.Runtime.SourcePolicy!.Read(path);
        Assert.AreEqual(false, fixture.Runtime.ExtractionState!.GetRedirectBridgeRequirement(
            source.StorageKey,
            source.SourceFingerprint));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_ClassifiesRedirectWithoutOverwritingCompleteTechnicalInformation()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(
            workspace.Write("classify.strm", "https://source.invalid/media"),
            new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0 } });
        item.Container = "mkv";
        item.RunTimeTicks = TimeSpan.FromHours(1).Ticks;
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            _ => new RedirectSourceResponse(302, "https://source.invalid/final", null),
            enablePlayback: true);
        var source = fixture.Runtime.SourcePolicy!.Read(item.Path);
        fixture.Runtime.MediaInfoStore!.Save(
            source,
            MediaInfoSnapshot.FromMediaSource(
                source,
                new MediaSourceInfo
                {
                    Container = "mkv",
                    RunTimeTicks = item.RunTimeTicks,
                    MediaStreams = item.MediaStreams,
                },
                fixture.Runtime.Clock.UtcNow));

        var classified = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);
        var skipped = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, classified.Skipped);
        Assert.AreEqual(1, skipped.Skipped);
        Assert.AreEqual(1, fixture.SourceClient.Calls);
        Assert.AreEqual(0, fixture.ProbeCalls());
        Assert.AreEqual(0, fixture.UpdateCalls());
        Assert.AreEqual(true, fixture.Runtime.ExtractionState!.GetRedirectBridgeRequirement(
            source.StorageKey,
            source.SourceFingerprint));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_PreservesRepositoryExternalStreamsWhenItemIsNotHydrated()
    {
        using var workspace = new TestWorkspace();
        var external = new MediaStream
        {
            Type = MediaStreamType.Subtitle,
            Index = 5,
            IsExternal = true,
            Path = Path.Combine(workspace.Path, "repository-only.srt"),
        };
        var item = CreateItem(workspace.Write("repository.strm", "https://source.invalid/media"));
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            _ => new RedirectSourceResponse(206, null, null),
            storedStreamsProvider: _ => new List<MediaStream> { external });

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Extracted);
        Assert.IsTrue(fixture.LastSavedStreams().Any(stream => stream.Type == MediaStreamType.Video));
        Assert.IsTrue(fixture.LastSavedStreams().Any(stream =>
            stream.IsExternal && string.Equals(stream.Path, external.Path, StringComparison.Ordinal)));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_ReindexesConflictingExternalSubtitlesAndFiltersNonPlaybackStreams()
    {
        using var workspace = new TestWorkspace();
        var firstSubtitle = new MediaStream
        {
            Type = MediaStreamType.Subtitle,
            Index = 0,
            IsExternal = true,
            Path = Path.Combine(workspace.Path, "first.ass"),
        };
        var secondSubtitle = new MediaStream
        {
            Type = MediaStreamType.Subtitle,
            Index = 1,
            IsExternal = true,
            Path = Path.Combine(workspace.Path, "second.ass"),
        };
        var item = CreateItem(workspace.Write("collision.strm", "https://source.invalid/media"));
        item.SubtitleStreamIndex = 1;
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            _ => new RedirectSourceResponse(206, null, null),
            storedStreamsProvider: _ => new List<MediaStream> { firstSubtitle, secondSubtitle },
            probedStreamsProvider: () => new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                new() { Type = MediaStreamType.Audio, Index = 1, Codec = "aac" },
                new() { Type = MediaStreamType.EmbeddedImage, Index = 2, Codec = "mjpeg" },
                new() { Type = MediaStreamType.Attachment, Index = 3, Codec = "ttf" },
            });

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Extracted);
        var saved = fixture.LastSavedStreams();
        CollectionAssert.AreEquivalent(
            new[] { MediaStreamType.Video, MediaStreamType.Audio, MediaStreamType.Subtitle, MediaStreamType.Subtitle },
            saved.Select(stream => stream.Type).ToArray());
        CollectionAssert.AreEquivalent(new[] { 0, 1, 2, 3 }, saved.Select(stream => stream.Index).ToArray());
        Assert.AreEqual(saved.Count, saved.Select(stream => stream.Index).Distinct().Count());
        Assert.AreEqual(3, item.SubtitleStreamIndex);
        Assert.IsFalse(saved.Any(stream => stream.Type == MediaStreamType.EmbeddedImage));
        Assert.IsFalse(saved.Any(stream => stream.Type == MediaStreamType.Attachment));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_WhenOnlyMissingIsDisabled_BypassesSnapshotAndProbesAgain()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("refresh.strm", "https://source.invalid/media"));
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            _ => new RedirectSourceResponse(206, null, null));

        var initial = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);
        Assert.AreEqual(1, initial.Extracted);
        Assert.AreEqual(1, fixture.ProbeCalls());

        fixture.Options.OnlyMissingMediaInfo = false;
        fixture.Runtime.UpdateOptions(fixture.Options, invalidateSensitiveState: false);
        var refreshed = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, refreshed.Extracted,
            $"restored={refreshed.Restored}, skipped={refreshed.Skipped}, failed={refreshed.Failed}");
        Assert.AreEqual(0, refreshed.Restored);
        Assert.AreEqual(2, fixture.ProbeCalls());
        Assert.AreEqual(2, fixture.UpdateCalls());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task ClearStoredMediaInfo_RemovesSelectedSnapshotsAndInternalStreamsButRetainsExternalStreams()
    {
        using var workspace = new TestWorkspace();
        var external = new MediaStream
        {
            Type = MediaStreamType.Subtitle,
            Index = 3,
            IsExternal = true,
            Path = Path.Combine(workspace.Path, "retained.srt"),
        };
        var item = CreateItem(workspace.Write("clear.strm", "https://source.invalid/media"));
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            _ => new RedirectSourceResponse(206, null, null),
            storedStreamsProvider: _ => new List<MediaStream> { external });
        var source = fixture.Runtime.SourcePolicy!.Read(item.Path);

        var extracted = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);
        Assert.AreEqual(1, extracted.Extracted);
        Assert.IsTrue(fixture.Runtime.MediaInfoStore!.TryLoad(source, out _));
        Assert.AreEqual(source.SourceFingerprint,
            fixture.Runtime.ExtractionState!.GetLastSuccessfulFingerprint(source.StorageKey));

        var cleared = await fixture.Coordinator.ClearStoredMediaInfoAsync(null, CancellationToken.None);

        Assert.AreEqual(1, cleared.Total);
        Assert.AreEqual(1, cleared.Cleared);
        Assert.AreEqual(0, cleared.Failed);
        Assert.AreEqual(1, cleared.SnapshotsRemoved);
        Assert.AreEqual(1, cleared.StateEntriesRemoved);
        Assert.AreEqual("strm", item.Container);
        Assert.IsFalse(item.RunTimeTicks.HasValue);
        Assert.AreEqual(0, item.Size);
        Assert.AreEqual(0, item.TotalBitrate);
        Assert.IsFalse(fixture.Runtime.MediaInfoStore.TryLoad(source, out _));
        Assert.IsNull(fixture.Runtime.ExtractionState.GetLastSuccessfulFingerprint(source.StorageKey));
        Assert.AreEqual(1, fixture.LastSavedStreams().Count);
        Assert.IsTrue(fixture.LastSavedStreams().Single().IsExternal);
        Assert.AreEqual(external.Path, fixture.LastSavedStreams().Single().Path);
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task ClearStoredMediaInfo_RemovesSnapshotAfterSourceContentBecomesInvalid()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Write("invalidated-clear.strm", "https://source.invalid/media");
        var item = CreateItem(path);
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            _ => new RedirectSourceResponse(206, null, null));
        var source = fixture.Runtime.SourcePolicy!.Read(path);
        var extracted = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);
        Assert.AreEqual(1, extracted.Extracted);

        File.WriteAllText(path, "not-a-url");
        var cleared = await fixture.Coordinator.ClearStoredMediaInfoAsync(null, CancellationToken.None);

        Assert.AreEqual(1, cleared.Cleared);
        Assert.AreEqual(0, cleared.Failed);
        Assert.AreEqual(1, cleared.SnapshotsRemoved);
        Assert.AreEqual(1, cleared.StateEntriesRemoved);
        Assert.IsFalse(fixture.Runtime.MediaInfoStore!.TryLoad(source, out _));
        Assert.IsNull(fixture.Runtime.ExtractionState!.GetLastSuccessfulFingerprint(source.StorageKey));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task ClearStoredMediaInfo_RemovesSuccessfulItemsWhenAnotherDatabaseClearFails()
    {
        using var workspace = new TestWorkspace();
        var first = CreateItem(workspace.Write("clear-fails.strm", "https://source.invalid/first"));
        var second = CreateItem(workspace.Write("clear-succeeds.strm", "https://source.invalid/second"));
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { first, second },
            _ => new RedirectSourceResponse(206, null, null),
            mediaStreamSaveFailures: 1);
        var firstSource = fixture.Runtime.SourcePolicy!.Read(first.Path);
        var secondSource = fixture.Runtime.SourcePolicy.Read(second.Path);
        var mediaSource = new MediaSourceInfo
        {
            Container = "mkv",
            MediaStreams = new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0 } },
        };
        fixture.Runtime.MediaInfoStore!.Save(
            firstSource,
            MediaInfoSnapshot.FromMediaSource(firstSource, mediaSource, fixture.Runtime.Clock.UtcNow));
        fixture.Runtime.MediaInfoStore.Save(
            secondSource,
            MediaInfoSnapshot.FromMediaSource(secondSource, mediaSource, fixture.Runtime.Clock.UtcNow));
        fixture.Runtime.ExtractionState!.RecordSuccess(firstSource.StorageKey, firstSource.SourceFingerprint);
        fixture.Runtime.ExtractionState.RecordSuccess(secondSource.StorageKey, secondSource.SourceFingerprint);
        fixture.Runtime.ExtractionState.Flush();

        var cleared = await fixture.Coordinator.ClearStoredMediaInfoAsync(null, CancellationToken.None);

        Assert.AreEqual(1, cleared.Cleared);
        Assert.AreEqual(1, cleared.Failed);
        Assert.AreEqual(1, cleared.SnapshotsRemoved);
        Assert.AreEqual(1, cleared.StateEntriesRemoved);
        Assert.IsTrue(fixture.Runtime.MediaInfoStore.TryLoad(firstSource, out _));
        Assert.IsFalse(fixture.Runtime.MediaInfoStore.TryLoad(secondSource, out _));
        Assert.AreEqual(firstSource.SourceFingerprint,
            fixture.Runtime.ExtractionState.GetLastSuccessfulFingerprint(firstSource.StorageKey));
        Assert.IsNull(fixture.Runtime.ExtractionState.GetLastSuccessfulFingerprint(secondSource.StorageKey));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task ClearStoredMediaInfo_CancellationDoesNotLeaveClearedItemsRestorable()
    {
        using var workspace = new TestWorkspace();
        var first = CreateItem(workspace.Write("cancel-clear-first.strm", "https://source.invalid/first"));
        var second = CreateItem(workspace.Write("cancel-clear-second.strm", "https://source.invalid/second"));
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { first, second },
            _ => new RedirectSourceResponse(206, null, null));
        var firstSource = fixture.Runtime.SourcePolicy!.Read(first.Path);
        var secondSource = fixture.Runtime.SourcePolicy.Read(second.Path);
        var mediaSource = new MediaSourceInfo
        {
            Container = "mkv",
            MediaStreams = new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0 } },
        };
        fixture.Runtime.MediaInfoStore!.Save(
            firstSource,
            MediaInfoSnapshot.FromMediaSource(firstSource, mediaSource, fixture.Runtime.Clock.UtcNow));
        fixture.Runtime.MediaInfoStore.Save(
            secondSource,
            MediaInfoSnapshot.FromMediaSource(secondSource, mediaSource, fixture.Runtime.Clock.UtcNow));
        fixture.Runtime.ExtractionState!.RecordSuccess(firstSource.StorageKey, firstSource.SourceFingerprint);
        fixture.Runtime.ExtractionState.RecordSuccess(secondSource.StorageKey, secondSource.SourceFingerprint);
        fixture.Runtime.ExtractionState.Flush();
        using var cancellation = new CancellationTokenSource();
        var progress = TestProxy.Create<IProgress<double>>((method, args) =>
        {
            if (method.Name == nameof(IProgress<double>.Report) && args is { Length: 1 } &&
                args[0] is double value && value >= 50d)
                cancellation.Cancel();
            return TestDispatchProxy.DefaultValue(method.ReturnType);
        });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            fixture.Coordinator.ClearStoredMediaInfoAsync(progress, cancellation.Token));

        Assert.IsFalse(fixture.Runtime.MediaInfoStore.TryLoad(firstSource, out _));
        Assert.IsNull(fixture.Runtime.ExtractionState.GetLastSuccessfulFingerprint(firstSource.StorageKey));
        Assert.IsTrue(fixture.Runtime.MediaInfoStore.TryLoad(secondSource, out _));
        Assert.AreEqual(secondSource.SourceFingerprint,
            fixture.Runtime.ExtractionState.GetLastSuccessfulFingerprint(secondSource.StorageKey));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_SharesOneProbeAcrossItemsWithIdenticalSource()
    {
        using var workspace = new TestWorkspace();
        const string sourceUrl = "https://source.invalid/shared";
        var first = CreateItem(workspace.Write("first.strm", sourceUrl));
        var second = CreateItem(workspace.Write("second.strm", sourceUrl));
        var fixture = CreateFixture(
            workspace,
            new[] { first, second },
            _ => new RedirectSourceResponse(206, null, null),
            maximumConcurrency: 2);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(2, result.Extracted,
            $"failed={result.Failed}, skipped={result.Skipped}, source={fixture.SourceClient.Calls}, probe={fixture.ProbeCalls()}, update={fixture.UpdateCalls()}");
        Assert.AreEqual(1, fixture.SourceClient.Calls);
        Assert.AreEqual(1, fixture.ProbeCalls());
        Assert.AreEqual(2, fixture.UpdateCalls());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_RestoresItemAndRepositoryStreamsWhenMediaStreamSaveFails()
    {
        using var workspace = new TestWorkspace();
        var external = new MediaStream
        {
            Type = MediaStreamType.Subtitle,
            Index = 0,
            IsExternal = true,
            Path = Path.Combine(workspace.Path, "existing.srt"),
        };
        var item = CreateItem(
            workspace.Write("rollback.strm", "https://source.invalid/media"),
            new List<MediaStream> { external });
        item.SubtitleStreamIndex = 0;
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            _ => new RedirectSourceResponse(206, null, null),
            mediaStreamSaveFailures: 1);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual("strm", item.Container);
        Assert.IsFalse(item.RunTimeTicks.HasValue);
        Assert.IsTrue(item.MediaStreams.Single().IsExternal);
        Assert.AreEqual(0, item.MediaStreams.Single().Index);
        Assert.AreEqual(0, item.SubtitleStreamIndex);
        Assert.AreEqual(2, fixture.UpdateCalls());
        Assert.AreEqual(2, fixture.MediaStreamSaveCalls());
        Assert.IsTrue(fixture.LastSavedStreams().Single().IsExternal);
        Assert.AreEqual(0, fixture.LastSavedStreams().Single().Index);
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_BaselinesCompleteItemThenReextractsWhenStrmContentChanges()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Write("changed.strm", "https://source.invalid/first");
        var item = CreateItem(path, new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
        });
        item.Container = "mkv";
        item.RunTimeTicks = TimeSpan.FromHours(1).Ticks;
        var fixture = CreateFixture(workspace, new[] { item }, _ => new RedirectSourceResponse(206, null, null));

        var baseline = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);
        Assert.AreEqual(1, baseline.Skipped);
        Assert.AreEqual(0, fixture.SourceClient.Calls);

        File.WriteAllText(path, "https://source.invalid/other", new System.Text.UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        var changed = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, changed.Extracted, $"failed={changed.Failed}, skipped={changed.Skipped}");
        Assert.AreEqual(1, fixture.SourceClient.Calls);
        Assert.AreEqual(1, fixture.UpdateCalls());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_RetriesOneBackedOffSourceToRefreshRedirectDetection()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("backoff.strm", "https://source.invalid/media"));
        var fixture = CreateFixture(workspace, new[] { item }, _ => new RedirectSourceResponse(206, null, null));
        var source = fixture.Runtime.SourcePolicy!.Read(item.Path);
        fixture.Runtime.ExtractionState!.RecordFailure(
            source.StorageKey,
            source.SourceFingerprint,
            fixture.Runtime.Clock.UtcNow);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Extracted);
        Assert.AreEqual(1, fixture.SourceClient.Calls);
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task UntrustedRedirect_NotifiesAdminsOnceAndTrustRetryRunsAutomatically()
    {
        using var workspace = new TestWorkspace();
        var notifications = new ConcurrentQueue<NotificationRequest>();
        var activities = new ConcurrentQueue<ActivityLogEntry>();
        var notificationManager = TestProxy.Create<INotificationManager>((method, args) =>
        {
            if (method.Name == nameof(INotificationManager.SendNotification) &&
                args is { Length: 2 } && args[0] is NotificationRequest request)
            {
                notifications.Enqueue(request);
                return Task.CompletedTask;
            }
            return TestDispatchProxy.DefaultValue(method.ReturnType);
        });
        var activityManager = TestProxy.Create<IActivityManager>((method, args) =>
        {
            if (method.Name == nameof(IActivityManager.Create) &&
                args is { Length: 1 } && args[0] is ActivityLogEntry entry)
                activities.Enqueue(entry);
            return TestDispatchProxy.DefaultValue(method.ReturnType);
        });
        var first = CreateItem(workspace.Write("approval-first.strm", "https://source.invalid/first"));
        var second = CreateItem(workspace.Write("approval-second.strm", "https://source.invalid/second"));
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { first, second },
            _ => new RedirectSourceResponse(302, "https://detected.invalid/media", null),
            notificationManager: notificationManager,
            activityManager: activityManager);

        var blocked = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(2, blocked.AwaitingApproval);
        Assert.AreEqual(0, blocked.Failed);
        Assert.AreEqual(1, notifications.Count);
        Assert.AreEqual(1, activities.Count);
        var awaiting = notifications.Single();
        Assert.AreEqual(SendToUserType.Admins, awaiting.SendToUserMode);
        Assert.AreEqual(NotificationLevel.Warning, awaiting.Level);
        Assert.IsFalse(awaiting.Description.Contains("detected.invalid", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(awaiting.Url.Contains("configurationpage?name=", StringComparison.Ordinal));
        var awaitingActivity = activities.Single();
        Assert.AreEqual(LogSeverity.Warn, awaitingActivity.Severity);
        Assert.IsFalse(awaitingActivity.Overview.Contains("detected.invalid", StringComparison.OrdinalIgnoreCase));

        fixture.Options.AllowedRedirectHosts = new[] { "detected.invalid" };
        fixture.Options.AllowedRedirectHostsText = "detected.invalid";
        fixture.Runtime.UpdateOptions(fixture.Options, invalidateSensitiveState: true);
        fixture.Runtime.ExtractionState!.ClearFailures();
        fixture.Runtime.ExtractionState.Flush();

        Assert.IsTrue(fixture.Coordinator.QueueTrustRetry());
        await fixture.Coordinator.WaitForPostScanIdleAsync();

        Assert.AreEqual(2, notifications.Count);
        Assert.AreEqual(2, activities.Count);
        Assert.AreEqual(4, fixture.SourceClient.Calls);
        Assert.AreEqual(2, fixture.ProbeCalls());
        Assert.AreEqual(2, fixture.UpdateCalls());
        var completed = notifications.Last();
        Assert.AreEqual(SendToUserType.Admins, completed.SendToUserMode);
        Assert.AreEqual(NotificationLevel.Normal, completed.Level);
        Assert.IsFalse(completed.Description.Contains("detected.invalid", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(LogSeverity.Info, activities.Last().Severity);
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task UntrustedRedirect_CreatesDashboardActivityWithoutExternalNotificationProvider()
    {
        using var workspace = new TestWorkspace();
        var activities = new ConcurrentQueue<ActivityLogEntry>();
        var activityManager = TestProxy.Create<IActivityManager>((method, args) =>
        {
            if (method.Name == nameof(IActivityManager.Create) &&
                args is { Length: 1 } && args[0] is ActivityLogEntry entry)
                activities.Enqueue(entry);
            return TestDispatchProxy.DefaultValue(method.ReturnType);
        });
        var item = CreateItem(workspace.Write("dashboard-warning.strm", "https://source.invalid/media"));
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { item },
            _ => new RedirectSourceResponse(302, "https://pending.invalid/media", null),
            activityManager: activityManager);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.AwaitingApproval);
        Assert.AreEqual(1, activities.Count);
        Assert.AreEqual(LogSeverity.Warn, activities.Single().Severity);
        Assert.IsFalse(activities.Single().Overview.Contains("pending.invalid", StringComparison.OrdinalIgnoreCase));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task UntrustedRedirect_CancellationInterruptsAdministratorNotification()
    {
        using var workspace = new TestWorkspace();
        var notificationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationManager = TestProxy.Create<INotificationManager>((method, args) =>
        {
            if (method.Name == nameof(INotificationManager.SendNotification) &&
                args is { Length: 2 } && args[1] is CancellationToken token)
            {
                notificationStarted.TrySetResult(true);
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return TestDispatchProxy.DefaultValue(method.ReturnType);
        });
        var item = CreateItem(workspace.Write("cancel-notification.strm", "https://source.invalid/media"));
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { item },
            _ => new RedirectSourceResponse(302, "https://pending.invalid/media", null),
            notificationManager: notificationManager);
        using var cancellation = new CancellationTokenSource();
        var extraction = fixture.Coordinator.ExtractAsync(false, null, cancellation.Token);
        await notificationStarted.Task;

        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await extraction);
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_EmptyLibrarySelectionProcessesNothing()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("disabled.strm", "https://source.invalid/media"));
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            _ => new RedirectSourceResponse(206, null, null),
            includeLibrary: false);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(0, result.Total);
        Assert.AreEqual(0, fixture.CandidateQueries());
        Assert.AreEqual(0, fixture.SourceClient.Calls);
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Cleanup_StillEnumeratesAllLibrariesWhenSelectionIsEmpty()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("cleanup.strm", "https://source.invalid/media"));
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            _ => new RedirectSourceResponse(206, null, null),
            includeLibrary: false);

        await fixture.Coordinator.CleanupAsync(CancellationToken.None);

        Assert.AreEqual(1, fixture.CandidateQueries());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task PostScan_QueuesOneFollowUpPassWhenScanArrivesDuringActivePass()
    {
        using var workspace = new TestWorkspace();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = CreateItem(workspace.Write("postscan.strm", "https://source.invalid/media"));
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            _ => new RedirectSourceResponse(206, null, null),
            probeDelay: async () =>
            {
                entered.TrySetResult(true);
                await release.Task;
            });

        Assert.IsTrue(fixture.Coordinator.QueuePostScan());
        await entered.Task;
        Assert.IsFalse(fixture.Coordinator.QueuePostScan());
        release.TrySetResult(true);
        await fixture.Coordinator.WaitForPostScanIdleAsync();

        Assert.AreEqual(2, fixture.CandidateQueries());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_CancellationFlushesCompletedBaselineState()
    {
        using var workspace = new TestWorkspace();
        var complete = CreateItem(
            workspace.Write("complete.strm", "https://source.invalid/complete"),
            new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0 } });
        complete.Container = "mkv";
        complete.RunTimeTicks = TimeSpan.FromHours(1).Ticks;
        var pending = CreateItem(workspace.Write("pending.strm", "https://source.invalid/pending"));
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { complete, pending },
            _ => new RedirectSourceResponse(206, null, null),
            probeDelay: async () =>
            {
                entered.TrySetResult(true);
                await release.Task;
            });
        var source = fixture.Runtime.SourcePolicy!.Read(complete.Path);
        using var cancellation = new CancellationTokenSource();
        var extracting = fixture.Coordinator.ExtractAsync(false, null, cancellation.Token);
        await entered.Task;

        cancellation.Cancel();
        release.TrySetResult(true);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await extracting);

        var reloaded = new Emby.StrmBridge.Persistence.ExtractionStateStore(
            Path.Combine(fixture.Runtime.DataDirectory!, "state", "extraction-state.json"));
        Assert.AreEqual(source.SourceFingerprint, reloaded.GetLastSuccessfulFingerprint(source.StorageKey));
        fixture.Runtime.Dispose();
    }

    private static Movie CreateItem(string path, List<MediaStream>? streams = null)
    {
        var parent = new Folder { Id = Guid.NewGuid(), InternalId = Random.Shared.NextInt64(1, long.MaxValue), Name = "Library" };
        var item = new Movie
        {
            Id = Guid.NewGuid(),
            InternalId = Random.Shared.NextInt64(1, long.MaxValue),
            Name = "Item",
            Path = path,
            Container = "strm",
            MediaStreams = streams ?? new List<MediaStream>(),
            Parent = parent,
            ParentId = parent.InternalId,
        };
        item.SetParent(parent);
        item.SetCachedParent(parent);
        return item;
    }

    private static Fixture CreateFixture(
        TestWorkspace workspace,
        BaseItem[] items,
        Func<Uri, RedirectSourceResponse> sourceResponse,
        int maximumConcurrency = 1,
        Func<Task>? probeDelay = null,
        bool includeLibrary = true,
        INotificationManager? notificationManager = null,
        IActivityManager? activityManager = null,
        int mediaStreamSaveFailures = 0,
        Func<BaseItem, List<MediaStream>>? storedStreamsProvider = null,
        Func<List<MediaStream>>? probedStreamsProvider = null,
        bool enablePlayback = false)
    {
        var libraryId = Guid.NewGuid();
        var probeCalls = 0;
        var updateCalls = 0;
        var mediaStreamSaveCalls = 0;
        var remainingMediaStreamSaveFailures = mediaStreamSaveFailures;
        var candidateQueries = 0;
        var lastSavedStreams = new List<MediaStream>();
        var sourceClient = new StubRedirectClient((_, source, _, _) =>
            Task.FromResult(sourceResponse(source)));
        var mediaSourceManager = TestProxy.Create<IMediaSourceManager>((method, args) =>
        {
            if (method.Name == nameof(IMediaSourceManager.GetMediaStreams))
            {
                var item = (BaseItem)args![0]!;
                return storedStreamsProvider?.Invoke(item) ?? item.MediaStreams;
            }
            if (method.Name == nameof(IMediaSourceManager.GetStaticMediaSources))
            {
                var item = (BaseItem)args![0]!;
                var source = File.ReadAllText(item.Path).Trim();
                return new List<MediaSourceInfo>
                {
                    new() { Path = source, RequiresOpening = false },
                };
            }
            if (method.Name == nameof(IMediaSourceManager.AddMediaInfoWithProbeSafe))
            {
                Interlocked.Increment(ref probeCalls);
                var media = (MediaSourceInfo)args![0]!;
                media.Container = "mkv";
                media.RunTimeTicks = TimeSpan.FromMinutes(90).Ticks;
                media.MediaStreams = probedStreamsProvider?.Invoke() ?? new List<MediaStream>
                    { new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" } };
                return probeDelay?.Invoke() ?? Task.CompletedTask;
            }
            return TestDispatchProxy.DefaultValue(method.ReturnType);
        });
        var libraryManager = TestProxy.Create<ILibraryManager>((method, args) =>
        {
            if (method.Name == nameof(ILibraryManager.GetItemList))
            {
                Interlocked.Increment(ref candidateQueries);
                return items;
            }
            if (method.Name == nameof(ILibraryManager.GetCollectionFolders))
                return new[] { new Folder { Id = libraryId, Name = "Library" } };
            if (method.Name == nameof(ILibraryManager.GetItemById))
            {
                var parentId = (long)args![0]!;
                return new Folder { InternalId = parentId, Name = "Library" };
            }
            if (method.Name == nameof(ILibraryManager.UpdateItems))
            {
                Interlocked.Increment(ref updateCalls);
                Assert.AreEqual(false, args![4]);
                return null;
            }
            return TestDispatchProxy.DefaultValue(method.ReturnType);
        });
        var itemRepository = TestProxy.Create<IItemRepository>((method, args) =>
        {
            if (method.Name == nameof(IItemRepository.SaveMediaStreams))
            {
                Interlocked.Increment(ref mediaStreamSaveCalls);
                if (Interlocked.Decrement(ref remainingMediaStreamSaveFailures) >= 0)
                    throw new InvalidOperationException("Simulated repository failure.");
                lastSavedStreams = ((List<MediaStream>)args![1]!).ToList();
            }
            return TestDispatchProxy.DefaultValue(method.ReturnType);
        });
        var logger = TestProxy.Create<ILogger>((method, _) => TestDispatchProxy.DefaultValue(method.ReturnType));
        var logManager = TestProxy.Create<ILogManager>((method, _) =>
            method.Name == nameof(ILogManager.GetLogger) ? logger : TestDispatchProxy.DefaultValue(method.ReturnType));
        var runtime = new PluginRuntime();
        runtime.Initialize(workspace.Path, new ManualClock(), sourceClient);
        var options = new PluginConfiguration
        {
            ConfigurationVersion = PluginConfiguration.CurrentConfigurationVersion,
            Enabled = true,
            EnablePlaybackSource = enablePlayback,
            EnablePersistence = true,
            OnlyMissingMediaInfo = true,
            MaximumExtractionConcurrency = maximumConcurrency,
            ExtractionTimeoutSeconds = 30,
            IncludedLibraryIds = includeLibrary
                ? new[] { libraryId.ToString("N") }
                : Array.Empty<string>(),
        };
        runtime.UpdateOptions(options, invalidateSensitiveState: false);
        var coordinator = new ExtractionCoordinator(
            runtime,
            libraryManager,
            mediaSourceManager,
            itemRepository,
            logManager,
            () => options,
            notificationManager,
            activityManager);
        runtime.Extraction = coordinator;
        return new Fixture(
            coordinator,
            runtime,
            sourceClient,
            options,
            () => Volatile.Read(ref probeCalls),
            () => Volatile.Read(ref updateCalls),
            () => Volatile.Read(ref mediaStreamSaveCalls),
            () => lastSavedStreams.ToList(),
            () => Volatile.Read(ref candidateQueries));
    }

    private sealed class Fixture
    {
        public Fixture(
            ExtractionCoordinator coordinator,
            PluginRuntime runtime,
            StubRedirectClient sourceClient,
            PluginConfiguration options,
            Func<int> probeCalls,
            Func<int> updateCalls,
            Func<int> mediaStreamSaveCalls,
            Func<List<MediaStream>> lastSavedStreams,
            Func<int> candidateQueries)
        {
            Coordinator = coordinator;
            Runtime = runtime;
            SourceClient = sourceClient;
            Options = options;
            ProbeCalls = probeCalls;
            UpdateCalls = updateCalls;
            MediaStreamSaveCalls = mediaStreamSaveCalls;
            LastSavedStreams = lastSavedStreams;
            CandidateQueries = candidateQueries;
        }

        public ExtractionCoordinator Coordinator { get; }
        public PluginRuntime Runtime { get; }
        public StubRedirectClient SourceClient { get; }
        public PluginConfiguration Options { get; }
        public Func<int> ProbeCalls { get; }
        public Func<int> UpdateCalls { get; }
        public Func<int> MediaStreamSaveCalls { get; }
        public Func<List<MediaStream>> LastSavedStreams { get; }
        public Func<int> CandidateQueries { get; }
    }
}

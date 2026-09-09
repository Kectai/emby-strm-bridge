using System.Collections.Concurrent;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Extraction;
using Emby.StrmBridge.Persistence;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Policy;
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
    [DataRow("https://source.invalid/opaque-download")]
    [DataRow("https://source.invalid/track.flac")]
    public async Task Extract_ActualAudioUsesItemTypeForProbeSkipAndRestore(string url)
    {
        using var workspace = new TestWorkspace();
        var item = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Id = Guid.NewGuid(),
            Path = workspace.Write("actual-audio.strm", url),
            Container = "strm",
        };
        var fixture = CreateFixture(workspace, new BaseItem[] { item }, expectedProbeIsAudio: true,
            probedStreamsProvider: () => new() { new() { Type = MediaStreamType.Audio, Index = 0, Codec = "flac" } });
        using var runtime = fixture.Runtime;
        Assert.AreEqual(1, (await fixture.Coordinator.ExtractAsync(true, null, CancellationToken.None)).Extracted);
        Assert.AreEqual(1, (await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None)).Skipped);
        item.MediaStreams = new();
        item.Container = "strm";
        item.RunTimeTicks = null;
        Assert.AreEqual(1, (await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None)).Restored);
        Assert.AreEqual(1, fixture.ProbeCalls());
        Assert.AreEqual(MediaStreamType.Audio, item.MediaStreams.Single().Type);
    }

    [TestMethod]
    public async Task Extract_SharedSourceDoesNotMixAudioAndVideoProbeContracts()
    {
        using var workspace = new TestWorkspace();
        const string url = "https://source.invalid/opaque-download";
        var audio = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Id = Guid.NewGuid(),
            Path = workspace.Write("audio.strm", url),
            Container = "strm",
        };
        var video = CreateItem(workspace.Write("video.strm", url));
        var fixture = CreateFixture(workspace, new BaseItem[] { audio, video },
            probedStreamsProvider: () => new() { new() { Type = MediaStreamType.Audio, Index = 0, Codec = "flac" } });
        using var runtime = fixture.Runtime;
        var result = await fixture.Coordinator.ExtractAsync(true, null, CancellationToken.None);
        Assert.AreEqual(1, result.Extracted);
        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual(2, fixture.ProbeCalls());
        Assert.AreEqual("strm", video.Container);
    }


    [TestMethod]
    public async Task Extract_FallbackMustOpenInputEvenWhenPartialHostOpenedEarlier()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("independent-evidence.strm", "https://source.invalid/movie.mkv"));
        var fallback = new TestExtractionProbeFallback(media =>
        {
            media.Container = "mp4";
            media.RunTimeTicks = TimeSpan.FromMinutes(7).Ticks;
            media.MediaStreams = new() { new() { Type = MediaStreamType.Video, Index = 0 } };
            return Task.CompletedTask;
        });
        var fixture = CreateFixture(workspace, new[] { item }, probeFallback: fallback,
            configureProbedMediaSource: media => media.Container = null);
        using var runtime = fixture.Runtime;
        var result = await fixture.Coordinator.ExtractAsync(true, null, CancellationToken.None);
        Assert.AreEqual(0, result.Extracted);
        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual(1, fallback.Calls);
        Assert.AreEqual(0, fixture.UpdateCalls());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Extract_CompleteResultWithoutInputCannotBecomeFreshSuccess(bool hostThrows)
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("cached.strm", "https://source.invalid/movie.mkv"));
        var fixture = CreateFixture(workspace, new[] { item },
            probeTicketOperation: (_, _) => hostThrows ? Task.FromException(new InvalidOperationException("host intercepted")) : Task.CompletedTask);
        using var runtime = fixture.Runtime;
        var result = await fixture.Coordinator.ExtractAsync(true, null, CancellationToken.None);
        var source = runtime.SourcePolicy!.Read(item.Path);
        Assert.AreEqual(0, result.Extracted);
        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual(0, fixture.UpdateCalls());
        Assert.IsNull(runtime.ExtractionState!.GetLastSuccessfulFingerprint(source.StorageKey));
        Assert.IsTrue(runtime.ExtractionState.ShouldAttempt(source.StorageKey, source.SourceFingerprint, runtime.Clock.UtcNow));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Extract_CompleteOrThrowingHostWithoutInputUsesFreshFallback(bool hostThrows)
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("fallback-cached.strm", "https://source.invalid/movie.mkv"));
        Fixture? fixture = null;
        var fallback = new TestExtractionProbeFallback(media =>
        {
            MarkProbeInputObserved(GetProbeTicket(fixture!.Runtime, media.Path));
            media.Container = "mp4";
            media.RunTimeTicks = TimeSpan.FromMinutes(7).Ticks;
            media.MediaStreams = new() { new() { Type = MediaStreamType.Video, Index = 0, Codec = "hevc" } };
            return Task.CompletedTask;
        });
        fixture = CreateFixture(workspace, new[] { item }, probeFallback: fallback,
            probeTicketOperation: (_, _) => hostThrows ? Task.FromException(new InvalidOperationException("host intercepted")) : Task.CompletedTask);
        using var runtime = fixture.Runtime;
        var result = await fixture.Coordinator.ExtractAsync(true, null, CancellationToken.None);
        Assert.AreEqual(1, result.Extracted);
        Assert.AreEqual(1, fallback.Calls);
        Assert.AreEqual("mp4", item.Container);
        Assert.AreEqual("hevc", item.MediaStreams.Single().Codec);
    }

    [TestMethod]
    [DataRow("force")]
    [DataRow("replace-all")]
    public async Task FreshProbe_UnknownSizeAndBitrateReplaceOldValuesAndRestoreConsistently(string mode)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Write("unknown-fields.strm", "https://source.invalid/first");
        var item = CreateItem(path, new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
        });
        item.Container = "mkv";
        item.RunTimeTicks = TimeSpan.FromHours(1).Ticks;
        item.Size = 9999999999;
        item.TotalBitrate = 80000000;
        var fixture = CreateFixture(workspace, new[] { item });
        using var runtime = fixture.Runtime;
        Assert.AreEqual(1, (await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None)).Skipped);
        if (mode == "replace-all") fixture.Options.OnlyMissingMediaInfo = false;

        var result = await fixture.Coordinator.ExtractAsync(mode == "force", null, CancellationToken.None);

        Assert.AreEqual(1, result.Extracted);
        Assert.AreEqual(0L, item.Size);
        Assert.AreEqual(0, item.TotalBitrate);
        var source = runtime.SourcePolicy!.Read(path);
        Assert.IsTrue(runtime.MediaInfoStore!.TryLoad(source, out var snapshot));
        Assert.IsNull(snapshot!.Size);
        Assert.IsNull(snapshot.Bitrate);
        Assert.AreEqual(1, fixture.UpdateCalls());

        item.Size = 9999999999;
        item.TotalBitrate = 80000000;
        Assert.AreEqual(1, (await fixture.Coordinator.RestoreAsync(null, CancellationToken.None)).Restored);
        Assert.AreEqual(0L, item.Size);
        Assert.AreEqual(0, item.TotalBitrate);
        Assert.AreEqual(1, fixture.ProbeCalls(), "Snapshot restoration must not start another remote probe.");
    }

    [TestMethod]
    public async Task Extract_ClearsDefaultsForRemovedInternalStreams()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("removed.strm", "https://source.invalid/media"),
            new List<MediaStream>
            {
                new() { Type = MediaStreamType.Audio, Index = 4 },
                new() { Type = MediaStreamType.Subtitle, Index = 5 },
            });
        item.AudioStreamIndex = 4;
        item.SubtitleStreamIndex = 5;
        var fixture = CreateFixture(workspace, new[] { item });
        fixture.Options.OnlyMissingMediaInfo = false;
        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);
        Assert.AreEqual(1, result.Extracted);
        Assert.IsNull(item.AudioStreamIndex);
        Assert.IsNull(item.SubtitleStreamIndex);
        Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task PostScan_DoesNotQueueWorkWhenScanWasCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new ExtractionPostScanTask().Run(null!, cancellation.Token));
    }

    [TestMethod]
    public async Task Extract_UsesLoopbackProbeAndRepositoryUpdateWithoutDroppingExternalStreams()
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
        var fixture = CreateFixture(workspace, new[] { item });

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Extracted,
            $"failed={result.Failed}, skipped={result.Skipped}, probe={fixture.ProbeCalls()}, update={fixture.UpdateCalls()}");
        Assert.AreEqual(1, fixture.ProbeCalls());
        Assert.AreEqual(1, fixture.UpdateCalls());
        Assert.AreEqual(1, fixture.MediaStreamSaveCalls());
        Assert.IsTrue(item.MediaStreams.Any(stream => stream.Type == MediaStreamType.Video));
        Assert.IsTrue(item.MediaStreams.Any(stream => stream.IsExternal && stream.Path == external.Path));
        Assert.IsTrue(fixture.LastSavedStreams().Any(stream => stream.Type == MediaStreamType.Video));
        Assert.IsTrue(fixture.LastSavedStreams().Any(stream => stream.IsExternal && stream.Path == external.Path));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Extract_MergesHydratedExternalStreamsMissingFromRepositoryWithoutDuplicates(
        bool repositoryContainsOneExternal)
    {
        using var workspace = new TestWorkspace();
        var firstPath = Path.Combine(workspace.Path, "first.srt");
        var secondPath = Path.Combine(workspace.Path, "second.srt");
        var first = new MediaStream
        {
            Type = MediaStreamType.Subtitle,
            Index = 7,
            IsExternal = true,
            Path = firstPath,
        };
        var second = new MediaStream
        {
            Type = MediaStreamType.Subtitle,
            Index = 7,
            IsExternal = true,
            Path = secondPath,
        };
        var item = CreateItem(
            workspace.Write("external-merge.strm", "https://source.invalid/media"),
            new List<MediaStream> { first, second });
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { item },
            storedStreamsProvider: _ => repositoryContainsOneExternal
                ? new List<MediaStream>
                {
                    new()
                    {
                        Type = MediaStreamType.Subtitle,
                        Index = first.Index,
                        IsExternal = true,
                        Path = Path.Combine(workspace.Path, "nested", "..", "first.srt"),
                    },
                }
                : new List<MediaStream>(),
            simulateGatewayProbe: true);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Extracted);
        var external = item.MediaStreams.Where(stream => stream.IsExternal).ToList();
        Assert.HasCount(2, external);
        var pathComparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        Assert.AreEqual(1, external.Count(stream => !string.IsNullOrWhiteSpace(stream.Path) &&
            string.Equals(Path.GetFullPath(stream.Path), firstPath, pathComparison)));
        Assert.AreEqual(1, external.Count(stream => !string.IsNullOrWhiteSpace(stream.Path) &&
            string.Equals(Path.GetFullPath(stream.Path), secondPath, pathComparison)));
        Assert.HasCount(2, fixture.LastSavedStreams().Where(stream => stream.IsExternal));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_StreamLimitRetainsInternalVideoAndNextMissingOnlyPassDoesNotProbe()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("stream-limit.strm", "https://source.invalid/media"));
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { item },
            probedStreamsProvider: () => Enumerable.Range(0, MediaInfoSnapshot.MaximumMediaStreams)
                .Select(index => new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = index,
                    Codec = "aac",
                })
                .Concat(new[]
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Index = MediaInfoSnapshot.MaximumMediaStreams,
                        Codec = "h264",
                    },
                })
                .ToList(),
            simulateGatewayProbe: true);
        fixture.Options.EnablePersistence = false;
        var source = fixture.Runtime.SourcePolicy!.Read(item.Path);

        var extracted = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);
        var skipped = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, extracted.Extracted);
        Assert.AreEqual(1, skipped.Skipped);
        Assert.AreEqual(1, fixture.ProbeCalls(),
            "The retained video must make the item complete for the next missing-only pass.");
        Assert.AreEqual(1, fixture.UpdateCalls());
        Assert.AreEqual(MediaInfoSnapshot.MaximumMediaStreams, item.MediaStreams.Count);
        Assert.AreEqual(MediaInfoSnapshot.MaximumMediaStreams - 1,
            item.MediaStreams.Count(stream => stream.Type == MediaStreamType.Audio));
        Assert.IsTrue(item.MediaStreams.Any(stream =>
            stream.Type == MediaStreamType.Video &&
            stream.Index == MediaInfoSnapshot.MaximumMediaStreams));
        Assert.IsTrue(fixture.LastSavedStreams().Any(stream =>
            stream.Type == MediaStreamType.Video &&
            stream.Index == MediaInfoSnapshot.MaximumMediaStreams));
        Assert.IsFalse(fixture.Runtime.MediaInfoStore!.TryLoad(source, out _),
            "This regression must prove completeness without snapshot restoration.");
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_DoesNotProbeCompleteUnchangedTechnicalInformation()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(
            workspace.Write("classify.strm", "https://source.invalid/media"),
            new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0 } });
        item.Container = "mkv";
        item.RunTimeTicks = TimeSpan.FromHours(1).Ticks;
        var fixture = CreateFixture(
            workspace,
            new[] { item });
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

        var first = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);
        var skipped = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, first.Skipped);
        Assert.AreEqual(1, skipped.Skipped);
        Assert.AreEqual(0, fixture.ProbeCalls());
        Assert.AreEqual(0, fixture.UpdateCalls());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_CompleteItemWhoseSourceDisappearsDuringBaselineIsSkippedAndRunContinues()
    {
        using var workspace = new TestWorkspace();
        var complete = CreateItem(
            workspace.Write("vanishing-complete.strm", "https://source.invalid/complete"),
            new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0 } });
        complete.Container = "mkv";
        complete.RunTimeTicks = TimeSpan.FromHours(1).Ticks;
        var missing = CreateItem(workspace.Write("remaining.strm", "https://source.invalid/missing"));
        var completeCollectionChecks = 0;
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { complete, missing },
            simulateGatewayProbe: true,
            collectionFoldersObserved: item =>
            {
                if (item.Id == complete.Id && Interlocked.Increment(ref completeCollectionChecks) == 3)
                    File.Delete(complete.Path);
            });

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(2, result.Total);
        Assert.AreEqual(1, result.Skipped);
        Assert.AreEqual(1, result.Extracted);
        Assert.AreEqual(0, result.Failed);
        Assert.AreEqual(1, fixture.ProbeCalls());
        Assert.IsTrue(missing.MediaStreams.Any(stream =>
            stream.Type == MediaStreamType.Video && !stream.IsExternal));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_DoesNotProbeCompleteTechnicalInformationWhenStateCapacityIsFull()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(
            workspace.Write("capacity.strm", "https://source.invalid/media"),
            new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0 } });
        item.Container = "mkv";
        item.RunTimeTicks = TimeSpan.FromHours(1).Ticks;
        var fixture = CreateFixture(
            workspace,
            new[] { item });
        var source = fixture.Runtime.SourcePolicy!.Read(item.Path);
        var inserted = 0;
        for (var value = 0; inserted < ExtractionStateStore.DefaultMaximumEntries; value++)
        {
            var storageKey = value.ToString("x64");
            if (string.Equals(storageKey, source.StorageKey, StringComparison.Ordinal)) continue;
            Assert.IsTrue(fixture.Runtime.ExtractionState!.TryRecordBaseline(
                storageKey,
                new string('f', 64)));
            inserted++;
        }
        Assert.IsFalse(fixture.Runtime.ExtractionState!.TryRecordBaseline(
            source.StorageKey,
            source.SourceFingerprint));

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Skipped);
        Assert.AreEqual(0, fixture.ProbeCalls());
        Assert.AreEqual(0, fixture.UpdateCalls());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_DoesNotProbeHydratedInternalVideoWhenRepositoryStreamsAreEmpty()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(
            workspace.Write("hydrated.strm", "https://source.invalid/media"),
            new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0 } });
        item.Container = "mkv";
        item.RunTimeTicks = TimeSpan.FromHours(1).Ticks;
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            storedStreamsProvider: _ => new List<MediaStream>());

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Skipped);
        Assert.AreEqual(0, fixture.ProbeCalls());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_HydratedCompleteItemDoesNotDependOnRepositoryStreamRead()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(
            workspace.Write("hydrated-repository-failure.strm", "https://source.invalid/media"),
            new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0 } });
        item.Container = "mkv";
        item.RunTimeTicks = TimeSpan.FromHours(1).Ticks;
        var repositoryReads = 0;
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            storedStreamsProvider: _ =>
            {
                Interlocked.Increment(ref repositoryReads);
                throw new InvalidOperationException("Simulated repository failure.");
            });

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Skipped);
        Assert.AreEqual(0, result.Failed);
        Assert.AreEqual(0, repositoryReads,
            "Hydrated internal video is sufficient and must avoid the failing repository read.");
        Assert.AreEqual(0, fixture.ProbeCalls());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_RepositoryStreamReadFailureFailsOnlyThatItemAndRunContinues()
    {
        using var workspace = new TestWorkspace();
        var failed = CreateItem(workspace.Write("repository-failure.strm", "https://source.invalid/failed"));
        var remaining = CreateItem(workspace.Write("repository-remaining.strm", "https://source.invalid/remaining"));
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { failed, remaining },
            storedStreamsProvider: item => item.Id == failed.Id
                ? throw new InvalidOperationException("Simulated repository failure.")
                : item.MediaStreams,
            simulateGatewayProbe: true);
        var failedSource = fixture.Runtime.SourcePolicy!.Read(failed.Path);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(2, result.Total);
        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual(1, result.Extracted);
        Assert.AreEqual(1, fixture.ProbeCalls());
        Assert.IsTrue(fixture.Runtime.ExtractionState!.ShouldAttempt(
            failedSource.StorageKey,
            failedSource.SourceFingerprint,
            fixture.Runtime.Clock.UtcNow),
            "A local repository read failure must not create remote-source backoff.");
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_DoesNotTreatExternalVideoAsCompleteTechnicalInformation()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(
            workspace.Write("external-video.strm", "https://source.invalid/media"),
            new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Index = 0, IsExternal = true },
            });
        item.Container = "mkv";
        item.RunTimeTicks = TimeSpan.FromHours(1).Ticks;
        var fixture = CreateFixture(
            workspace,
            new[] { item });

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Extracted);
        Assert.AreEqual(1, fixture.ProbeCalls());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_RestoresMatchingSnapshotAfterTimestampOnlySourceChange()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Write("touched.strm", "https://source.invalid/media");
        var item = CreateItem(path);
        var fixture = CreateFixture(
            workspace,
            new[] { item });

        var initial = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);
        Assert.AreEqual(1, initial.Extracted);
        Assert.AreEqual(1, fixture.ProbeCalls());
        item.MediaStreams = new List<MediaStream>();
        item.Container = "strm";
        item.RunTimeTicks = null;
        item.Size = 0;
        item.TotalBitrate = 0;
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));

        var restored = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, restored.Restored,
            $"extracted={restored.Extracted}, skipped={restored.Skipped}, failed={restored.Failed}");
        Assert.AreEqual(1, fixture.ProbeCalls(), "A timestamp-only change must not start another remote probe.");
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    [DataRow("missing-container")]
    [DataRow("short-runtime")]
    [DataRow("external-video")]
    public async Task Extract_DoesNotPersistProbeResultsThatRemainTechnicallyIncomplete(string mode)
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("incomplete-probe.strm", "https://source.invalid/media"));
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            configureProbedMediaSource: mediaSource =>
            {
                if (mode == "missing-container") mediaSource.Container = null;
                if (mode == "short-runtime") mediaSource.RunTimeTicks = TimeSpan.FromMilliseconds(500).Ticks;
                if (mode == "external-video")
                {
                    mediaSource.MediaStreams = new List<MediaStream>
                    {
                        new() { Type = MediaStreamType.Video, Index = 0, IsExternal = true },
                    };
                }
            });
        var source = fixture.Runtime.SourcePolicy!.Read(item.Path);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual(1, fixture.ProbeCalls());
        Assert.IsFalse(fixture.Runtime.MediaInfoStore!.TryLoad(source, out _));
        Assert.IsNull(fixture.Runtime.ExtractionState!.GetLastSuccessfulFingerprint(source.StorageKey));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_SucceedsWhenCompleteProbeCannotBeStoredAsSafeSnapshot()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("unsafe-snapshot.strm", "https://source.invalid/media"));
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            configureProbedMediaSource: mediaSource => mediaSource.Container = "matroska/webm");
        var source = fixture.Runtime.SourcePolicy!.Read(item.Path);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Extracted,
            $"failed={result.Failed}, skipped={result.Skipped}, restored={result.Restored}");
        Assert.AreEqual("matroska/webm", item.Container);
        Assert.IsTrue(item.RunTimeTicks.HasValue);
        Assert.IsTrue(item.MediaStreams.Any(stream => stream.Type == MediaStreamType.Video));
        Assert.AreEqual(1, fixture.UpdateCalls());
        Assert.AreEqual(source.SourceFingerprint,
            fixture.Runtime.ExtractionState!.GetLastSuccessfulFingerprint(source.StorageKey));
        Assert.IsFalse(fixture.Runtime.MediaInfoStore!.TryLoad(source, out _));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task ProbeCircuit_InputOpenCannotBeOverwrittenByThresholdFailureAlreadyInProgress()
    {
        using var thresholdReached = new ManualResetEventSlim(false);
        using var allowThresholdCommit = new ManualResetEventSlim(false);
        var circuit = new ProbeRunCircuit(() =>
        {
            thresholdReached.Set();
            Assert.IsTrue(allowThresholdCommit.Wait(TimeSpan.FromSeconds(2)));
        });
        Assert.IsFalse(circuit.RecordInputNotOpened());
        Assert.IsFalse(circuit.RecordInputNotOpened());
        var thresholdFailure = Task.Run(() => circuit.RecordInputNotOpened());
        Assert.IsTrue(thresholdReached.Wait(TimeSpan.FromSeconds(2)));
        var inputOpenStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputOpen = Task.Run(() =>
        {
            inputOpenStarted.TrySetResult(true);
            circuit.RecordInputOpened();
        });
        await inputOpenStarted.Task;

        allowThresholdCommit.Set();
        Assert.IsTrue(await thresholdFailure);
        await inputOpen;

        Assert.IsFalse(circuit.IsOpen,
            "A real input-open event ordered after the threshold failure must leave the circuit closed.");
        Assert.IsFalse(circuit.RecordInputNotOpened(),
            "The input-open event must also reset the consecutive failure count.");
    }

    [TestMethod]
    public async Task Extract_HostProbeBlockedBeforeInputUsesFallbackForRestOfRun()
    {
        using var workspace = new TestWorkspace();
        var items = Enumerable.Range(0, 2)
            .Select(index => (BaseItem)CreateItem(workspace.Write(
                $"fallback-{index}.strm",
                $"https://source.invalid/media/{index}")))
            .ToArray();
        Fixture? fixture = null;
        var fallback = new TestExtractionProbeFallback(mediaSource =>
        {
            Assert.IsTrue(new Uri(mediaSource.Path).IsLoopback);
            var ticket = GetProbeTicket(fixture!.Runtime, mediaSource.Path);
            MarkProbeInputObserved(ticket);
            mediaSource.Container = "mkv";
            mediaSource.RunTimeTicks = TimeSpan.FromMinutes(24).Ticks;
            mediaSource.MediaStreams = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                new() { Type = MediaStreamType.Audio, Index = 1, Codec = "aac" },
            };
            return Task.CompletedTask;
        });
        fixture = CreateFixture(
            workspace,
            items,
            maximumConcurrency: 1,
            hostOpensInput: false,
            configureProbedMediaSource: mediaSource =>
            {
                mediaSource.Container = null;
                mediaSource.RunTimeTicks = null;
                mediaSource.MediaStreams = new List<MediaStream>();
            },
            probeFallback: fallback);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(items.Length, result.Extracted);
        Assert.AreEqual(0, result.Failed);
        Assert.AreEqual(1, fixture.ProbeCalls(),
            "After the first host probe never opens its input, this run should avoid repeating that path.");
        Assert.AreEqual(items.Length, fallback.Calls);
        Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_IncompleteOpenedHostProbeUsesFallbackForThatItem()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write(
            "deep-transport-probe.strm",
            "https://source.invalid/media.m2ts"));
        Fixture? fixture = null;
        var fallback = new TestExtractionProbeFallback(mediaSource =>
        {
            var ticket = GetProbeTicket(fixture!.Runtime, mediaSource.Path);
            MarkProbeInputObserved(ticket);
            mediaSource.Container = "mpegts";
            mediaSource.RunTimeTicks = TimeSpan.FromSeconds(107).Ticks;
            mediaSource.MediaStreams = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                new() { Type = MediaStreamType.Audio, Index = 1, Codec = "pcm_bluray" },
            };
            return Task.CompletedTask;
        });
        fixture = CreateFixture(
            workspace,
            new BaseItem[] { item },
            configureProbedMediaSource: mediaSource =>
            {
                mediaSource.Container = "mpegts";
                mediaSource.RunTimeTicks = null;
                mediaSource.MediaStreams = new List<MediaStream>
                {
                    new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                };
            },
            probeTicketOperation: (ticket, _) =>
            {
                MarkProbeInputObserved(ticket);
                return Task.CompletedTask;
            },
            probeFallback: fallback);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Extracted);
        Assert.AreEqual(0, result.Failed);
        Assert.AreEqual(1, fixture.ProbeCalls());
        Assert.AreEqual(1, fallback.Calls);
        Assert.AreEqual("mpegts", item.Container);
        Assert.AreEqual(TimeSpan.FromSeconds(107).Ticks, item.RunTimeTicks);
        Assert.IsTrue(item.MediaStreams.Any(stream =>
            stream.Type == MediaStreamType.Video && !stream.IsExternal));
        Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_SerializesFallbackProbesWhenExtractionConcurrencyIsTwo()
    {
        using var workspace = new TestWorkspace();
        var items = Enumerable.Range(0, 2)
            .Select(index => (BaseItem)CreateItem(workspace.Write(
                $"serialized-fallback-{index}.strm",
                $"https://source.invalid/media/{index}")))
            .ToArray();
        Fixture? fixture = null;
        var sync = new object();
        var active = 0;
        var maximumActive = 0;
        var fallback = new TestExtractionProbeFallback(async mediaSource =>
        {
            var ticket = GetProbeTicket(fixture!.Runtime, mediaSource.Path);
            MarkProbeInputObserved(ticket);
            lock (sync)
            {
                active++;
                maximumActive = Math.Max(maximumActive, active);
            }
            try
            {
                await Task.Delay(50);
                mediaSource.Container = "mkv";
                mediaSource.RunTimeTicks = TimeSpan.FromMinutes(24).Ticks;
                mediaSource.MediaStreams = new List<MediaStream>
                    { new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" } };
            }
            finally
            {
                lock (sync) active--;
            }
        });
        fixture = CreateFixture(
            workspace,
            items,
            maximumConcurrency: 2,
            configureProbedMediaSource: mediaSource =>
            {
                mediaSource.Container = null;
                mediaSource.RunTimeTicks = null;
                mediaSource.MediaStreams = new List<MediaStream>();
            },
            probeFallback: fallback);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(items.Length, result.Extracted);
        Assert.AreEqual(0, result.Failed);
        Assert.AreEqual(1, maximumActive,
            "Independent ffprobe processes must not compete for the loopback relay slots.");
        Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    [DataRow("flac")]
    [DataRow("mp3")]
    [DataRow("m4a")]
    public async Task Extract_ProbesMimeClassifiedAudioSourceAsAudio(string extension)
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write(
            $"audio-{extension}.strm",
            $"https://source.invalid/track.{extension}?signature=value"));
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { item },
            probedStreamsProvider: () => new List<MediaStream>
                { new() { Type = MediaStreamType.Audio, Index = 0, Codec = extension } },
            configureProbedMediaSource: mediaSource => mediaSource.Container = extension,
            expectedProbeIsAudio: true);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Extracted);
        Assert.AreEqual(0, result.Failed);
        Assert.AreEqual(1, fixture.ProbeCalls());
        Assert.AreEqual(1, fixture.UpdateCalls());
        Assert.IsTrue(item.MediaStreams.Any(stream =>
            stream.Type == MediaStreamType.Audio && !stream.IsExternal));
        Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_InvalidSelectedSourceIsReportedAsFailure()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write(
            "invalid-source.strm",
            "https://source.invalid/track#unencoded.flac"));
        var fixture = CreateFixture(workspace, new BaseItem[] { item });

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Total);
        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual(0, result.Skipped);
        Assert.AreEqual(0, result.Extracted);
        Assert.AreEqual(0, fixture.ProbeCalls());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_OnlyMissingSkipsCompleteMimeClassifiedAudioSource()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(
            workspace.Write("complete-audio.strm", "https://source.invalid/track.flac"),
            new List<MediaStream>
                { new() { Type = MediaStreamType.Audio, Index = 0, Codec = "flac" } });
        item.Container = "flac";
        item.RunTimeTicks = TimeSpan.FromMinutes(4).Ticks;
        var fixture = CreateFixture(workspace, new BaseItem[] { item });

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Skipped);
        Assert.AreEqual(0, result.Failed);
        Assert.AreEqual(0, fixture.ProbeCalls());
        Assert.AreEqual(0, fixture.UpdateCalls());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_AudioFallbackReceivesAudioNormalizationMode()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write(
            "fallback-audio.strm",
            "https://source.invalid/track.flac"));
        var observedAudioMode = false;
        Fixture? fixture = null;
        var fallback = new TestExtractionProbeFallback((mediaSource, isAudio) =>
        {
            observedAudioMode = isAudio;
            var ticket = GetProbeTicket(fixture!.Runtime, mediaSource.Path);
            MarkProbeInputObserved(ticket);
            mediaSource.Container = "flac";
            mediaSource.RunTimeTicks = TimeSpan.FromMinutes(4).Ticks;
            mediaSource.MediaStreams = new List<MediaStream>
                { new() { Type = MediaStreamType.Audio, Index = 0, Codec = "flac" } };
            return Task.CompletedTask;
        });
        fixture = CreateFixture(
            workspace,
            new BaseItem[] { item },
            configureProbedMediaSource: mediaSource =>
            {
                mediaSource.Container = null;
                mediaSource.RunTimeTicks = null;
                mediaSource.MediaStreams = new List<MediaStream>();
            },
            probeFallback: fallback);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Extracted);
        Assert.IsTrue(observedAudioMode);
        Assert.AreEqual(1, fallback.Calls);
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_DoesNotUseFallbackAfterHostProbeOpenedItsInput()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("opened-before-failure.strm", "https://source.invalid/media"));
        var fallback = new TestExtractionProbeFallback(mediaSource =>
        {
            mediaSource.Container = "mkv";
            mediaSource.RunTimeTicks = TimeSpan.FromMinutes(24).Ticks;
            mediaSource.MediaStreams = new List<MediaStream>
                { new() { Type = MediaStreamType.Video, Index = 0 } };
            return Task.CompletedTask;
        });
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { item },
            probeTicketOperation: (ticket, _) =>
            {
                MarkProbeInputObserved(ticket);
                return Task.FromException(new InvalidOperationException("Simulated upstream probe failure."));
            },
            probeFallback: fallback);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual(0, result.Extracted);
        Assert.AreEqual(0, fallback.Calls,
            "An opened input proves that the host probe was not intercepted before execution.");
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_FallbackThatNeverOpensInputStillTripsRunCircuit()
    {
        using var workspace = new TestWorkspace();
        var items = Enumerable.Range(0, 6)
            .Select(index => (BaseItem)CreateItem(workspace.Write(
                $"fallback-no-input-{index}.strm",
                $"https://source.invalid/media/{index}")))
            .ToArray();
        var fallback = new TestExtractionProbeFallback(_ =>
            Task.FromException(new InvalidOperationException("Simulated fallback launch failure.")));
        var fixture = CreateFixture(
            workspace,
            items,
            maximumConcurrency: 1,
            hostOpensInput: false,
            configureProbedMediaSource: mediaSource =>
            {
                mediaSource.Container = null;
                mediaSource.RunTimeTicks = null;
                mediaSource.MediaStreams = new List<MediaStream>();
            },
            probeFallback: fallback);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(3, result.Failed);
        Assert.AreEqual(items.Length - result.Failed, result.Skipped);
        Assert.AreEqual(1, fixture.ProbeCalls());
        Assert.AreEqual(3, fallback.Calls);
        foreach (var item in items)
        {
            var source = fixture.Runtime.SourcePolicy!.Read(item.Path);
            Assert.IsTrue(fixture.Runtime.ExtractionState!.ShouldAttempt(
                source.StorageKey,
                source.SourceFingerprint,
                fixture.Runtime.Clock.UtcNow));
        }
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_RepeatedFallbackResultFailuresTripRunCircuitWithoutSourceBackoff()
    {
        using var workspace = new TestWorkspace();
        var items = Enumerable.Range(0, 6)
            .Select(index => (BaseItem)CreateItem(workspace.Write(
                $"fallback-local-failure-{index}.strm",
                $"https://source.invalid/media/{index}")))
            .ToArray();
        Fixture? fixture = null;
        var fallback = new TestExtractionProbeFallback(mediaSource =>
        {
            var ticket = GetProbeTicket(fixture!.Runtime, mediaSource.Path);
            MarkProbeInputObserved(ticket);
            return Task.FromException(new IndependentProbeResultException(
                new InvalidDataException("Simulated fallback normalization failure.")));
        });
        fixture = CreateFixture(
            workspace,
            items,
            maximumConcurrency: 1,
            hostOpensInput: false,
            configureProbedMediaSource: mediaSource =>
            {
                mediaSource.Container = null;
                mediaSource.RunTimeTicks = null;
                mediaSource.MediaStreams = new List<MediaStream>();
            },
            probeFallback: fallback);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(3, result.Failed);
        Assert.AreEqual(items.Length - result.Failed, result.Skipped);
        Assert.AreEqual(1, fixture.ProbeCalls());
        Assert.AreEqual(3, fallback.Calls);
        Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
        foreach (var item in items)
        {
            var source = fixture.Runtime.SourcePolicy!.Read(item.Path);
            Assert.IsTrue(fixture.Runtime.ExtractionState!.ShouldAttempt(
                source.StorageKey,
                source.SourceFingerprint,
                fixture.Runtime.Clock.UtcNow));
        }
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Extract_StopsRunAfterRepeatedProbeInputsAreNeverOpened(bool probeCallThrows)
    {
        using var workspace = new TestWorkspace();
        var items = Enumerable.Range(0, 12)
            .Select(index => (BaseItem)CreateItem(workspace.Write(
                $"blocked-{index}.strm",
                $"https://source.invalid/media/{index}")))
            .ToArray();
        var fixture = CreateFixture(
            workspace,
            items,
            maximumConcurrency: 2,
            hostOpensInput: false,
            configureProbedMediaSource: mediaSource =>
            {
                if (probeCallThrows) throw new InvalidOperationException("Simulated probe invocation failure.");
                mediaSource.Container = null;
                mediaSource.RunTimeTicks = null;
                mediaSource.MediaStreams = new List<MediaStream>();
            });

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.IsGreaterThanOrEqualTo(3, fixture.ProbeCalls());
        Assert.IsLessThanOrEqualTo(4, fixture.ProbeCalls(), "The run-wide circuit should bound concurrent overshoot.");
        Assert.AreEqual(fixture.ProbeCalls(), result.Failed);
        Assert.AreEqual(items.Length - result.Failed, result.Skipped);
        foreach (var item in items)
        {
            var source = fixture.Runtime.SourcePolicy!.Read(item.Path);
            Assert.IsTrue(fixture.Runtime.ExtractionState!.ShouldAttempt(
                source.StorageKey,
                source.SourceFingerprint,
                fixture.Runtime.Clock.UtcNow),
                "A host-level probe failure must not poison any source-specific retry state.");
        }
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_StopsRunAfterProbeTimeoutsBeforeLoopbackInputIsOpened()
    {
        using var workspace = new TestWorkspace();
        var items = Enumerable.Range(0, 6)
            .Select(index => (BaseItem)CreateItem(workspace.Write(
                $"timeout-{index}.strm",
                $"https://source.invalid/media/{index}")))
            .ToArray();
        Fixture? fixture = null;
        fixture = CreateFixture(
            workspace,
            items,
            maximumConcurrency: 1,
            hostOpensInput: false,
            probeOperation: async cancellationToken =>
            {
                await Task.Yield();
                fixture!.Coordinator.CancelActive();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(3, fixture.ProbeCalls());
        Assert.AreEqual(3, result.Failed);
        Assert.AreEqual(items.Length - result.Failed, result.Skipped);
        foreach (var item in items)
        {
            var source = fixture.Runtime.SourcePolicy!.Read(item.Path);
            Assert.IsTrue(fixture.Runtime.ExtractionState!.ShouldAttempt(
                source.StorageKey,
                source.SourceFingerprint,
                fixture.Runtime.Clock.UtcNow),
                "A host-level probe timeout must not poison any source-specific retry state.");
        }
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task Extract_CancelActiveStopsNonCooperativeProbeAndOpensCircuitAfterThirdAttempt()
    {
        using var workspace = new TestWorkspace();
        var items = Enumerable.Range(0, 6)
            .Select(index => (BaseItem)CreateItem(workspace.Write(
                $"non-cooperative-{index}.strm",
                $"https://source.invalid/media/{index}")))
            .ToArray();
        Fixture? fixture = null;
        fixture = CreateFixture(
            workspace,
            items,
            maximumConcurrency: 1,
            hostOpensInput: false,
            probeOperation: _ =>
            {
                fixture!.Coordinator.CancelActive();
                return new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously).Task;
            });

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(3, fixture.ProbeCalls());
        Assert.AreEqual(3, result.Failed);
        Assert.AreEqual(items.Length - result.Failed, result.Skipped);
        foreach (var item in items)
        {
            var source = fixture.Runtime.SourcePolicy!.Read(item.Path);
            Assert.IsTrue(fixture.Runtime.ExtractionState!.ShouldAttempt(
                source.StorageKey,
                source.SourceFingerprint,
                fixture.Runtime.Clock.UtcNow));
        }
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_ExternalCancellationWinsWhenProbeThrowsAnotherException()
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var item = CreateItem(workspace.Write(
            "cancel-non-cancellation-exception.strm",
            "https://source.invalid/media"));
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { item },
            probeOperation: _ =>
            {
                cancellation.Cancel();
                return Task.FromException(new InvalidOperationException("Simulated late probe failure."));
            });
        var source = fixture.Runtime.SourcePolicy!.Read(item.Path);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            fixture.Coordinator.ExtractAsync(false, null, cancellation.Token));

        Assert.AreEqual(1, fixture.ProbeCalls());
        Assert.IsTrue(fixture.Runtime.ExtractionState!.ShouldAttempt(
            source.StorageKey,
            source.SourceFingerprint,
            fixture.Runtime.Clock.UtcNow),
            "External cancellation must not create source-specific retry backoff.");
        Assert.IsFalse(fixture.Runtime.MediaInfoStore!.TryLoad(source, out _));
        Assert.IsNull(fixture.Runtime.ExtractionState.GetLastSuccessfulFingerprint(source.StorageKey));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_InFlightObservedProbeRetainsCallbackUntilGatewayConsumesIt()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("callback-handoff.strm", "https://source.invalid/media"));
        TicketPayload? observedTicket = null;
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { item },
            probeTicketOperation: (ticket, _) =>
            {
                observedTicket = ticket;
                Interlocked.Exchange(ref ticket.ProbeRequestObserved, 1);
                return Task.FromException(new InvalidOperationException(
                    "Simulated Gateway pause after recording the request."));
            });

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Failed);
        Assert.IsNotNull(observedTicket);
        var callback = Interlocked.Exchange(ref observedTicket!.ProbeInputObservedCallback, null);
        Assert.IsNotNull(callback,
            "Revocation must not clear a callback that an already-redeemed Gateway request still needs.");
        callback();
        Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_LocalGatewayFailuresDoNotOpenCircuitOrCreateSourceBackoff()
    {
        using var workspace = new TestWorkspace();
        var items = Enumerable.Range(0, 6)
            .Select(index => (BaseItem)CreateItem(workspace.Write(
                $"local-gateway-failure-{index}.strm",
                $"https://source.invalid/media/{index}")))
            .ToArray();
        var fixture = CreateFixture(
            workspace,
            items,
            maximumConcurrency: 1,
            probeTicketOperation: (ticket, _) =>
            {
                MarkProbeInputObserved(ticket);
                Interlocked.Exchange(ref ticket.ProbeLocalFailureObserved, 1);
                return Task.FromException(new InvalidOperationException(
                    "Simulated local Gateway failure after loopback input opened."));
            });

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(items.Length, result.Failed);
        Assert.AreEqual(items.Length, fixture.ProbeCalls(),
            "Local failures after input-open must not contribute to the no-input circuit.");
        foreach (var item in items)
        {
            var source = fixture.Runtime.SourcePolicy!.Read(item.Path);
            Assert.IsTrue(fixture.Runtime.ExtractionState!.ShouldAttempt(
                source.StorageKey,
                source.SourceFingerprint,
                fixture.Runtime.Clock.UtcNow),
                "A local Gateway failure must not create remote-source backoff.");
        }
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_FullTicketStoreOpensProbeCircuitWithoutSourceBackoff()
    {
        using var workspace = new TestWorkspace();
        var items = Enumerable.Range(0, 6)
            .Select(index => (BaseItem)CreateItem(workspace.Write(
                $"ticket-capacity-{index}.strm",
                $"https://source.invalid/media/{index}")))
            .ToArray();
        var fixture = CreateFixture(workspace, items, maximumConcurrency: 1);
        var source = fixture.Runtime.SourcePolicy!.Read(items[0].Path);
        for (var index = 0; index < TicketStore.MaximumPlaybackTickets; index++)
        {
            fixture.Runtime.Tickets.IssuePlayback(
                Guid.NewGuid(),
                "capacity-" + index,
                null,
                source,
                PlaybackTicketPurpose.ServerFfmpeg,
                fixture.Runtime.Generation,
                TimeSpan.FromHours(1));
        }
        Assert.AreEqual(TicketStore.MaximumPlaybackTickets, fixture.Runtime.Tickets.Count);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(0, fixture.ProbeCalls(), "A full ticket store must fail before invoking ffprobe.");
        Assert.AreEqual(3, result.Failed);
        Assert.AreEqual(items.Length - result.Failed, result.Skipped);
        foreach (var item in items)
        {
            var itemSource = fixture.Runtime.SourcePolicy.Read(item.Path);
            Assert.IsTrue(fixture.Runtime.ExtractionState!.ShouldAttempt(
                itemSource.StorageKey,
                itemSource.SourceFingerprint,
                fixture.Runtime.Clock.UtcNow),
                "Ticket capacity is a host-level failure and must not create source backoff.");
        }
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
            new[] { item });

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
    public async Task Extract_ForceBypassesMatchingSnapshotAndProbesAgain()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("force-refresh.strm", "https://source.invalid/media"));
        var fixture = CreateFixture(
            workspace,
            new[] { item });

        var initial = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);
        Assert.AreEqual(1, initial.Extracted);
        Assert.AreEqual(1, fixture.ProbeCalls());
        item.MediaStreams = new List<MediaStream>();
        item.Container = "strm";
        item.RunTimeTicks = null;

        var refreshed = await fixture.Coordinator.ExtractAsync(true, null, CancellationToken.None);

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
            new[] { item });
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
            mediaStreamSaveFailures: 1);
        var firstSource = fixture.Runtime.SourcePolicy!.Read(first.Path);
        var secondSource = fixture.Runtime.SourcePolicy.Read(second.Path);
        var mediaSource = new MediaSourceInfo
        {
            Container = "mkv",
            RunTimeTicks = TimeSpan.FromHours(1).Ticks,
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
            new BaseItem[] { first, second });
        var firstSource = fixture.Runtime.SourcePolicy!.Read(first.Path);
        var secondSource = fixture.Runtime.SourcePolicy.Read(second.Path);
        var mediaSource = new MediaSourceInfo
        {
            Container = "mkv",
            RunTimeTicks = TimeSpan.FromHours(1).Ticks,
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
            maximumConcurrency: 2);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(2, result.Extracted,
            $"failed={result.Failed}, skipped={result.Skipped}, probe={fixture.ProbeCalls()}, update={fixture.UpdateCalls()}");
        Assert.AreEqual(1, fixture.ProbeCalls());
        Assert.AreEqual(2, fixture.UpdateCalls());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task Extract_SharedFailureForSamePathAdvancesBackoffOnlyOncePerRun()
    {
        using var workspace = new TestWorkspace();
        const int duplicateCount = 2;
        const string sourceUrl = "https://source.invalid/shared-failure";
        var path = workspace.Write("shared-failure.strm", sourceUrl);
        var items = Enumerable.Range(0, duplicateCount)
            .Select(_ => (BaseItem)CreateItem(path))
            .ToArray();
        var allWorkersEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var collectionFolderCalls = 0;
        var fixture = CreateFixture(
            workspace,
            items,
            maximumConcurrency: items.Length,
            probeTicketOperation: async (ticket, _) =>
            {
                MarkProbeInputObserved(ticket);
                await releaseProbe.Task;
                throw new InvalidOperationException("Simulated shared remote probe failure.");
            },
            collectionFoldersObserved: _ =>
            {
                // Candidate enumeration accounts for the first call per item; the second
                // batch proves every worker has entered ProcessItemAsync.
                if (Interlocked.Increment(ref collectionFolderCalls) == items.Length * 2)
                    allWorkersEntered.TrySetResult(true);
            });
        var source = fixture.Runtime.SourcePolicy!.Read(path);
        var extraction = fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);
        await allWorkersEntered.Task;
        await Task.Delay(50);
        releaseProbe.TrySetResult(true);

        var result = await extraction;

        Assert.AreEqual(items.Length, result.Failed,
            "Every item must observe the shared remote failure.");
        Assert.AreEqual(1, fixture.ProbeCalls());
        Assert.IsFalse(fixture.Runtime.ExtractionState!.ShouldAttempt(
            source.StorageKey,
            source.SourceFingerprint,
            fixture.Runtime.Clock.UtcNow));
        ((ManualClock)fixture.Runtime.Clock).Advance(TimeSpan.FromSeconds(31));
        Assert.IsTrue(fixture.Runtime.ExtractionState.ShouldAttempt(
            source.StorageKey,
            source.SourceFingerprint,
            fixture.Runtime.Clock.UtcNow),
            "One shared failure must retain the first 30-second retry step, not multiply it by waiter count.");
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_OpenCircuitStillReusesCompletedFlightForIdenticalSource()
    {
        using var workspace = new TestWorkspace();
        const string sharedSourceUrl = "https://source.invalid/shared-flight";
        var sharedFirst = CreateItem(workspace.Write("shared-first.strm", sharedSourceUrl));
        var blocked = Enumerable.Range(0, 3)
            .Select(index => (BaseItem)CreateItem(workspace.Write(
                $"blocked-flight-{index}.strm",
                $"https://source.invalid/blocked/{index}")))
            .ToArray();
        var sharedSecond = CreateItem(workspace.Write("shared-second.strm", sharedSourceUrl));
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { sharedFirst, blocked[0], blocked[1], blocked[2], sharedSecond },
            maximumConcurrency: 1,
            probeTicketOperation: (ticket, _) =>
            {
                if (string.Equals(ticket.Source.SourceUri.AbsoluteUri, sharedSourceUrl, StringComparison.Ordinal))
                {
                    MarkProbeInputObserved(ticket);
                    return Task.CompletedTask;
                }
                return Task.FromException(new InvalidOperationException("Simulated probe input not opened."));
            });
        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(4, fixture.ProbeCalls(),
            "The second shared item must reuse the retained flight instead of opening another remote probe.");
        Assert.AreEqual(2, result.Extracted);
        Assert.AreEqual(3, result.Failed);
        Assert.AreEqual(0, result.Skipped);
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
            mediaStreamSaveFailures: 1);
        var source = fixture.Runtime.SourcePolicy!.Read(item.Path);

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
        Assert.IsTrue(fixture.Runtime.ExtractionState!.ShouldAttempt(
            source.StorageKey,
            source.SourceFingerprint,
            fixture.Runtime.Clock.UtcNow),
            "A local repository write failure must not create remote-source backoff.");
        Assert.IsNull(fixture.Runtime.ExtractionState.GetLastSuccessfulFingerprint(source.StorageKey));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_FailedApplyRestoresHydratedInternalStreamsMissingFromRepository()
    {
        using var workspace = new TestWorkspace();
        var subtitlePath = Path.Combine(workspace.Path, "existing-partial.srt");
        var video = new MediaStream
        {
            Type = MediaStreamType.Video,
            Index = 0,
            Codec = "h264",
        };
        var audio = new MediaStream
        {
            Type = MediaStreamType.Audio,
            Index = 1,
            Codec = "aac",
        };
        var hydratedSubtitle = new MediaStream
        {
            Type = MediaStreamType.Subtitle,
            Index = 2,
            IsExternal = true,
            Path = subtitlePath,
        };
        var repositorySubtitle = new MediaStream
        {
            Type = MediaStreamType.Subtitle,
            Index = 2,
            IsExternal = true,
            Path = subtitlePath,
        };
        var item = CreateItem(
            workspace.Write("partial-repository-rollback.strm", "https://source.invalid/media"),
            new List<MediaStream> { video, audio, hydratedSubtitle });
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            mediaStreamSaveFailures: 1,
            storedStreamsProvider: _ => new List<MediaStream> { repositorySubtitle });
        var source = fixture.Runtime.SourcePolicy!.Read(item.Path);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual(1, fixture.ProbeCalls());
        Assert.AreEqual(2, fixture.UpdateCalls());
        Assert.AreEqual(2, fixture.MediaStreamSaveCalls());
        Assert.HasCount(3, item.MediaStreams);
        Assert.AreEqual(1, item.MediaStreams.Count(stream =>
            stream.Type == MediaStreamType.Video && !stream.IsExternal));
        Assert.AreEqual(1, item.MediaStreams.Count(stream =>
            stream.Type == MediaStreamType.Audio && !stream.IsExternal));
        Assert.AreEqual(1, item.MediaStreams.Count(stream =>
            stream.Type == MediaStreamType.Subtitle && stream.IsExternal));
        Assert.HasCount(3, fixture.LastSavedStreams());
        Assert.IsTrue(fixture.LastSavedStreams().Any(stream =>
            stream.Type == MediaStreamType.Video && !stream.IsExternal));
        Assert.IsTrue(fixture.LastSavedStreams().Any(stream =>
            stream.Type == MediaStreamType.Audio && !stream.IsExternal));
        Assert.AreEqual(1, fixture.LastSavedStreams().Count(stream =>
            stream.Type == MediaStreamType.Subtitle && stream.IsExternal));
        Assert.IsTrue(fixture.Runtime.ExtractionState!.ShouldAttempt(
            source.StorageKey,
            source.SourceFingerprint,
            fixture.Runtime.Clock.UtcNow));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task ClearStoredMediaInfo_FailedWriteRestoresHydratedInternalStreamsMissingFromRepository()
    {
        using var workspace = new TestWorkspace();
        var subtitlePath = Path.Combine(workspace.Path, "clear-partial.srt");
        var item = CreateItem(
            workspace.Write("clear-partial-repository.strm", "https://source.invalid/media"),
            new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                new() { Type = MediaStreamType.Audio, Index = 1, Codec = "aac" },
                new()
                {
                    Type = MediaStreamType.Subtitle,
                    Index = 2,
                    IsExternal = true,
                    Path = subtitlePath,
                },
            });
        item.Container = "mkv";
        item.RunTimeTicks = TimeSpan.FromMinutes(90).Ticks;
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            mediaStreamSaveFailures: 1,
            storedStreamsProvider: _ => new List<MediaStream>
            {
                new()
                {
                    Type = MediaStreamType.Subtitle,
                    Index = 2,
                    IsExternal = true,
                    Path = subtitlePath,
                },
            });

        var result = await fixture.Coordinator.ClearStoredMediaInfoAsync(null, CancellationToken.None);

        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual(0, result.Cleared);
        Assert.AreEqual("mkv", item.Container);
        Assert.AreEqual(TimeSpan.FromMinutes(90).Ticks, item.RunTimeTicks);
        Assert.HasCount(3, item.MediaStreams);
        Assert.AreEqual(1, item.MediaStreams.Count(stream =>
            stream.Type == MediaStreamType.Video && !stream.IsExternal));
        Assert.AreEqual(1, item.MediaStreams.Count(stream =>
            stream.Type == MediaStreamType.Audio && !stream.IsExternal));
        Assert.AreEqual(1, item.MediaStreams.Count(stream =>
            stream.Type == MediaStreamType.Subtitle && stream.IsExternal));
        Assert.HasCount(3, fixture.LastSavedStreams());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_SnapshotApplyFailureDoesNotCreateRemoteSourceBackoff()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write(
            "snapshot-apply-failure.strm",
            "https://source.invalid/media"));
        var fixture = CreateFixture(
            workspace,
            new[] { item },
            mediaStreamSaveFailures: 1);
        var source = fixture.Runtime.SourcePolicy!.Read(item.Path);
        fixture.Runtime.MediaInfoStore!.Save(
            source,
            MediaInfoSnapshot.FromMediaSource(
                source,
                new MediaSourceInfo
                {
                    Container = "mkv",
                    RunTimeTicks = TimeSpan.FromHours(1).Ticks,
                    MediaStreams = new List<MediaStream>
                    {
                        new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                    },
                },
                fixture.Runtime.Clock.UtcNow));

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual(0, fixture.ProbeCalls(), "A matching snapshot must not start a remote probe.");
        Assert.AreEqual("strm", item.Container);
        Assert.IsFalse(item.RunTimeTicks.HasValue);
        Assert.IsEmpty(item.MediaStreams);
        Assert.AreEqual(2, fixture.UpdateCalls());
        Assert.AreEqual(2, fixture.MediaStreamSaveCalls());
        Assert.IsTrue(fixture.Runtime.ExtractionState!.ShouldAttempt(
            source.StorageKey,
            source.SourceFingerprint,
            fixture.Runtime.Clock.UtcNow),
            "A local snapshot-apply failure must not create remote-source backoff.");
        Assert.IsNull(fixture.Runtime.ExtractionState.GetLastSuccessfulFingerprint(source.StorageKey));
        Assert.IsTrue(fixture.Runtime.MediaInfoStore.TryLoad(source, out _));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_OnlyMissingSkipsCompleteItemWhenSourceContentChanges()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Write("changed.strm", "https://source.invalid/first");
        var originalLength = new FileInfo(path).Length;
        var originalLastWriteUtc = File.GetLastWriteTimeUtc(path);
        var item = CreateItem(path, new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
        });
        item.Container = "mkv";
        item.RunTimeTicks = TimeSpan.FromHours(1).Ticks;
        var fixture = CreateFixture(workspace, new[] { item });

        var baseline = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);
        Assert.AreEqual(1, baseline.Skipped);

        File.WriteAllText(path, "https://source.invalid/other", new System.Text.UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, originalLastWriteUtc);
        Assert.AreEqual(originalLength, new FileInfo(path).Length);
        Assert.AreEqual(originalLastWriteUtc, File.GetLastWriteTimeUtc(path));
        var changed = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, changed.Skipped, $"failed={changed.Failed}, extracted={changed.Extracted}");
        Assert.AreEqual(0, fixture.ProbeCalls());
        Assert.AreEqual(0, fixture.UpdateCalls());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_DoesNotSkipMimeClassifiedVideoSource()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write(
            "video-mp4.strm",
            "https://source.invalid/feature.mp4?signature=value"));
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { item },
            expectedProbeIsAudio: false);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Extracted);
        Assert.AreEqual(1, fixture.ProbeCalls());
        Assert.AreEqual(1, fixture.UpdateCalls());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_BackoffStrictlySkipsRemoteProbe()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("backoff.strm", "https://source.invalid/media"));
        var fixture = CreateFixture(workspace, new[] { item });
        var source = fixture.Runtime.SourcePolicy!.Read(item.Path);
        fixture.Runtime.ExtractionState!.RecordFailure(
            source.StorageKey,
            source.SourceFingerprint,
            fixture.Runtime.Clock.UtcNow);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Skipped);
        Assert.AreEqual(0, result.Extracted);
        Assert.AreEqual(0, fixture.ProbeCalls());
        Assert.AreEqual(0, fixture.UpdateCalls());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_BackoffStillRestoresMatchingSnapshotWithoutRemoteProbe()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("backoff-restore.strm", "https://source.invalid/media"));
        var fixture = CreateFixture(workspace, new[] { item });
        var source = fixture.Runtime.SourcePolicy!.Read(item.Path);
        fixture.Runtime.MediaInfoStore!.Save(
            source,
            MediaInfoSnapshot.FromMediaSource(
                source,
                new MediaSourceInfo
                {
                    Container = "mkv",
                    RunTimeTicks = TimeSpan.FromHours(1).Ticks,
                    MediaStreams = new List<MediaStream>
                    {
                        new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                    },
                },
                fixture.Runtime.Clock.UtcNow));
        fixture.Runtime.ExtractionState!.RecordFailure(
            source.StorageKey,
            source.SourceFingerprint,
            fixture.Runtime.Clock.UtcNow);
        Assert.IsFalse(fixture.Runtime.ExtractionState.ShouldAttempt(
            source.StorageKey,
            source.SourceFingerprint,
            fixture.Runtime.Clock.UtcNow),
            "The matching snapshot must be restored while source retry backoff is active.");

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Restored);
        Assert.AreEqual(0, result.Skipped);
        Assert.AreEqual(0, fixture.ProbeCalls());
        Assert.AreEqual(1, fixture.UpdateCalls());
        Assert.IsTrue(item.MediaStreams.Any(stream =>
            stream.Type == MediaStreamType.Video && !stream.IsExternal));
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
            simulatedRedirectLocation: "https://detected.invalid/media",
            notificationManager: notificationManager,
            activityManager: activityManager,
            simulateGatewayProbe: true);

        var blocked = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(2, blocked.AwaitingApproval,
            $"failed={blocked.Failed}, skipped={blocked.Skipped}, probes={fixture.ProbeCalls()}");
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
        Assert.AreEqual(4, fixture.ProbeCalls());
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
            simulatedRedirectLocation: "https://pending.invalid/media",
            activityManager: activityManager,
            simulateGatewayProbe: true);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.AwaitingApproval,
            $"failed={result.Failed}, skipped={result.Skipped}, probes={fixture.ProbeCalls()}");
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
            simulatedRedirectLocation: "https://pending.invalid/media",
            notificationManager: notificationManager,
            simulateGatewayProbe: true);
        using var cancellation = new CancellationTokenSource();
        var extraction = fixture.Coordinator.ExtractAsync(false, null, cancellation.Token);
        await notificationStarted.Task;

        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await extraction);
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_ExternalCancellationWinsOverObservedProbeRejection()
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var item = CreateItem(workspace.Write("cancel-rejected-probe.strm", "https://source.invalid/media"));
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { item },
            probeTicketOperation: (ticket, probeToken) =>
            {
                MarkProbeInputObserved(ticket);
                Interlocked.Exchange(
                    ref ticket.ProbeRejectionReason,
                    (int)RedirectRejectionReason.UntrustedTargetHost);
                cancellation.Cancel();
                return Task.FromCanceled(probeToken);
            });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            fixture.Coordinator.ExtractAsync(false, null, cancellation.Token));

        Assert.AreEqual(1, fixture.ProbeCalls());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_GenerationInvalidationDuringProbeDoesNotCommitMediaInformation()
    {
        using var workspace = new TestWorkspace();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = CreateItem(workspace.Write(
            "generation-invalidated.strm",
            "https://source.invalid/media"));
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { item },
            probeOperation: async _ =>
            {
                entered.TrySetResult(true);
                await release.Task;
            });
        var source = fixture.Runtime.SourcePolicy!.Read(item.Path);
        var extraction = fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);
        await entered.Task;

        fixture.Runtime.UpdateOptions(fixture.Options, invalidateSensitiveState: true);
        release.TrySetResult(true);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await extraction);
        Assert.AreEqual("strm", item.Container);
        Assert.IsNull(item.RunTimeTicks);
        Assert.IsEmpty(item.MediaStreams);
        Assert.AreEqual(0, fixture.UpdateCalls());
        Assert.AreEqual(0, fixture.MediaStreamSaveCalls());
        Assert.IsFalse(fixture.Runtime.MediaInfoStore!.TryLoad(source, out _));
        Assert.IsNull(fixture.Runtime.ExtractionState!.GetLastSuccessfulFingerprint(source.StorageKey));
        Assert.IsTrue(fixture.Runtime.ExtractionState.ShouldAttempt(
            source.StorageKey,
            source.SourceFingerprint,
            fixture.Runtime.Clock.UtcNow));
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
            includeLibrary: false);

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(0, result.Total);
        Assert.AreEqual(0, fixture.CandidateQueries());
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
            includeLibrary: false);

        await fixture.Coordinator.CleanupAsync(CancellationToken.None);

        Assert.AreEqual(1, fixture.CandidateQueries());
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Cleanup_RetainsSnapshotAndStateWhenIndexedStrmContentsAreTemporarilyInvalid()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Write("cleanup-invalid.strm", "https://source.invalid/media");
        var item = CreateItem(path);
        var fixture = CreateFixture(workspace, new[] { item });
        var source = fixture.Runtime.SourcePolicy!.Read(path);
        fixture.Runtime.MediaInfoStore!.Save(
            source,
            MediaInfoSnapshot.FromMediaSource(
                source,
                new MediaSourceInfo
                {
                    Container = "mkv",
                    RunTimeTicks = TimeSpan.FromHours(1).Ticks,
                    MediaStreams = new List<MediaStream>
                    {
                        new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                    },
                },
                fixture.Runtime.Clock.UtcNow));
        fixture.Runtime.ExtractionState!.RecordSuccess(source.StorageKey, source.SourceFingerprint);
        File.WriteAllText(path, "temporarily invalid STRM contents", new System.Text.UTF8Encoding(false));

        var removed = await fixture.Coordinator.CleanupAsync(CancellationToken.None);

        Assert.AreEqual(0, removed);
        Assert.IsTrue(fixture.Runtime.MediaInfoStore.TryLoad(source, out _));
        Assert.AreEqual(source.SourceFingerprint,
            fixture.Runtime.ExtractionState.GetLastSuccessfulFingerprint(source.StorageKey));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Cleanup_AbandonsAllDeletionWhenAnyCandidateStorageKeyCannotBeBuilt()
    {
        using var workspace = new TestWorkspace();
        var invalidPath = workspace.Path + Path.DirectorySeparatorChar + "invalid\0.strm";
        var fixture = CreateFixture(workspace, new BaseItem[] { CreateItem(invalidPath) });
        var retainedSource = TestSources.Create();
        fixture.Runtime.MediaInfoStore!.Save(
            retainedSource,
            MediaInfoSnapshot.FromMediaSource(
                retainedSource,
                new MediaSourceInfo
                {
                    Container = "mkv",
                    RunTimeTicks = TimeSpan.FromHours(1).Ticks,
                    MediaStreams = new List<MediaStream>
                    {
                        new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                    },
                },
                fixture.Runtime.Clock.UtcNow));
        fixture.Runtime.ExtractionState!.RecordSuccess(
            retainedSource.StorageKey,
            retainedSource.SourceFingerprint);

        var removed = await fixture.Coordinator.CleanupAsync(CancellationToken.None);

        Assert.AreEqual(0, removed);
        Assert.IsTrue(fixture.Runtime.MediaInfoStore.TryLoad(retainedSource, out _),
            "Cleanup must not mutate snapshots after building an incomplete keep-set.");
        Assert.AreEqual(retainedSource.SourceFingerprint,
            fixture.Runtime.ExtractionState.GetLastSuccessfulFingerprint(retainedSource.StorageKey));
        fixture.Runtime.Dispose();
    }

    [TestMethod]
    public async Task Extract_IncludesStrmExtrasInCandidateQuery()
    {
        using var workspace = new TestWorkspace();
        var item = CreateItem(workspace.Write("trailer.strm", "https://source.invalid/trailer"));
        item.ExtraType = ExtraType.Trailer;
        var fixture = CreateFixture(
            workspace,
            new BaseItem[] { item });

        var result = await fixture.Coordinator.ExtractAsync(false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Extracted);
        Assert.IsNotNull(fixture.LastCandidateQuery());
        Assert.IsFalse(fixture.LastCandidateQuery()!.EnforceExtraType);
        CollectionAssert.AreEquivalent(
            new[] { MediaType.Video, MediaType.Audio },
            fixture.LastCandidateQuery()!.MediaTypes);
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
        string? simulatedRedirectLocation = null,
        int maximumConcurrency = 1,
        Func<Task>? probeDelay = null,
        bool includeLibrary = true,
        INotificationManager? notificationManager = null,
        IActivityManager? activityManager = null,
        int mediaStreamSaveFailures = 0,
        Func<BaseItem, List<MediaStream>>? storedStreamsProvider = null,
        Func<List<MediaStream>>? probedStreamsProvider = null,
        Action<MediaSourceInfo>? configureProbedMediaSource = null,
        Func<CancellationToken, Task>? probeOperation = null,
        bool simulateGatewayProbe = false,
        Func<TicketPayload, CancellationToken, Task>? probeTicketOperation = null,
        Action<BaseItem>? collectionFoldersObserved = null,
        IExtractionProbeFallback? probeFallback = null,
        bool? expectedProbeIsAudio = null,
        bool hostOpensInput = true)
    {
        var libraryId = Guid.NewGuid();
        var probeCalls = 0;
        var updateCalls = 0;
        var mediaStreamSaveCalls = 0;
        var remainingMediaStreamSaveFailures = mediaStreamSaveFailures;
        var candidateQueries = 0;
        InternalItemsQuery? lastCandidateQuery = null;
        var lastSavedStreams = new List<MediaStream>();
        PluginRuntime? runtime = null;
        PluginConfiguration? options = null;
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
                if (expectedProbeIsAudio.HasValue)
                    Assert.AreEqual(expectedProbeIsAudio.Value, (bool)args[1]!);
                Assert.IsTrue(new Uri(media.Path).IsLoopback);
                Assert.Contains("/StrmBridge/Playback/", media.Path);
                Assert.AreEqual(media.Path, media.ProbePath);
                media.Container = "mkv";
                media.RunTimeTicks = TimeSpan.FromMinutes(90).Ticks;
                media.MediaStreams = probedStreamsProvider?.Invoke() ?? new List<MediaStream>
                    { new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" } };
                configureProbedMediaSource?.Invoke(media);
                if (simulateGatewayProbe || probeTicketOperation is not null)
                {
                    var ticket = GetProbeTicket(runtime!, media.Path);
                    if (probeTicketOperation is not null)
                        return probeTicketOperation(ticket, (CancellationToken)args[3]!);
                    return SimulateGatewayProbe(ticket, simulatedRedirectLocation, options!, runtime!);
                }
                // Default successful host simulation represents a real gateway input open.
                if (hostOpensInput) MarkProbeInputObserved(GetProbeTicket(runtime!, media.Path));
                return probeOperation?.Invoke((CancellationToken)args[3]!) ??
                       probeDelay?.Invoke() ??
                       Task.CompletedTask;
            }
            return TestDispatchProxy.DefaultValue(method.ReturnType);
        });
        var libraryManager = TestProxy.Create<ILibraryManager>((method, args) =>
        {
            if (method.Name == nameof(ILibraryManager.GetItemList))
            {
                Interlocked.Increment(ref candidateQueries);
                lastCandidateQuery = (InternalItemsQuery)args![0]!;
                return items;
            }
            if (method.Name == nameof(ILibraryManager.GetCollectionFolders))
            {
                if (args is { Length: > 0 } && args[0] is BaseItem item)
                    collectionFoldersObserved?.Invoke(item);
                return new[] { new Folder { Id = libraryId, Name = "Library" } };
            }
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
        runtime = new PluginRuntime();
        runtime.Initialize(workspace.Path, new ManualClock());
        options = new PluginConfiguration
        {
            ConfigurationVersion = PluginConfiguration.CurrentConfigurationVersion,
            Enabled = true,
            PlaybackMode = PlaybackRoutingMode.Adaptive,
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
            activityManager,
            () => "http://127.0.0.1:8096/emby",
            probeFallback);
        runtime.Extraction = coordinator;
        return new Fixture(
            coordinator,
            runtime,
            options,
            () => Volatile.Read(ref probeCalls),
            () => Volatile.Read(ref updateCalls),
            () => Volatile.Read(ref mediaStreamSaveCalls),
            () => lastSavedStreams.ToList(),
            () => Volatile.Read(ref candidateQueries),
            () => lastCandidateQuery);
    }

    private static TicketPayload GetProbeTicket(PluginRuntime runtime, string path)
    {
        var segments = new Uri(path).AbsolutePath
            .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        var routeIndex = Array.FindIndex(
            segments,
            segment => string.Equals(segment, "Playback", StringComparison.OrdinalIgnoreCase));
        Assert.IsGreaterThanOrEqualTo(0, routeIndex);
        Assert.IsTrue(routeIndex + 2 < segments.Length);
        Assert.AreEqual("v3", segments[routeIndex + 1]);
        Assert.IsTrue(runtime.Tickets.TryInspect(segments[routeIndex + 2], out var ticket));
        Assert.IsNotNull(ticket);
        return ticket;
    }

    private static Task SimulateGatewayProbe(
        TicketPayload ticket,
        string? redirectLocation,
        PluginConfiguration options,
        PluginRuntime runtime)
    {
        MarkProbeInputObserved(ticket);
        if (redirectLocation is not null)
        {
            try
            {
                _ = new RedirectPolicy(
                        () => options.AllowedRedirectHosts,
                        runtime.RecordDetectedRedirectHost)
                    .Validate(ticket.Source.SourceUri, redirectLocation);
            }
            catch (RedirectRejectedException exception)
            {
                Interlocked.Exchange(ref ticket.ProbeRejectionReason, (int)exception.Reason);
                return Task.FromException(new InvalidOperationException(
                    "The simulated loopback gateway rejected the probe source.", exception));
            }
        }
        return Task.CompletedTask;
    }

    private static void MarkProbeInputObserved(TicketPayload ticket)
    {
        if (Interlocked.Exchange(ref ticket.ProbeRequestObserved, 1) == 0)
            Interlocked.Exchange(ref ticket.ProbeInputObservedCallback, null)?.Invoke();
    }

    private sealed class TestExtractionProbeFallback : IExtractionProbeFallback
    {
        private readonly Func<MediaSourceInfo, bool, Task> probe;
        private int calls;

        public TestExtractionProbeFallback(Func<MediaSourceInfo, Task> probe) =>
            this.probe = (mediaSource, _) => probe(mediaSource);

        public TestExtractionProbeFallback(Func<MediaSourceInfo, bool, Task> probe) =>
            this.probe = probe;

        public bool IsAvailable => true;

        public int Calls => Volatile.Read(ref calls);

        public async Task ProbeAsync(
            MediaSourceInfo mediaSource,
            bool isAudio,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref calls);
            await probe(mediaSource, isAudio);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class Fixture
    {
        public Fixture(
            ExtractionCoordinator coordinator,
            PluginRuntime runtime,
            PluginConfiguration options,
            Func<int> probeCalls,
            Func<int> updateCalls,
            Func<int> mediaStreamSaveCalls,
            Func<List<MediaStream>> lastSavedStreams,
            Func<int> candidateQueries,
            Func<InternalItemsQuery?> lastCandidateQuery)
        {
            Coordinator = coordinator;
            Runtime = runtime;
            Options = options;
            ProbeCalls = probeCalls;
            UpdateCalls = updateCalls;
            MediaStreamSaveCalls = mediaStreamSaveCalls;
            LastSavedStreams = lastSavedStreams;
            CandidateQueries = candidateQueries;
            LastCandidateQuery = lastCandidateQuery;
        }

        public ExtractionCoordinator Coordinator { get; }
        public PluginRuntime Runtime { get; }
        public PluginConfiguration Options { get; }
        public Func<int> ProbeCalls { get; }
        public Func<int> UpdateCalls { get; }
        public Func<int> MediaStreamSaveCalls { get; }
        public Func<List<MediaStream>> LastSavedStreams { get; }
        public Func<int> CandidateQueries { get; }
        public Func<InternalItemsQuery?> LastCandidateQuery { get; }
    }
}

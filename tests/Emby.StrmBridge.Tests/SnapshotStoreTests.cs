using System.Text;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class SnapshotStoreTests
{

    [TestMethod]
    [DataRow(ExtendedVideoTypes.DolbyVision, ExtendedVideoSubTypes.DoviProfile81)]
    [DataRow(ExtendedVideoTypes.Hdr10, ExtendedVideoSubTypes.None)]
    public void Serializer_PreservesNegotiationFields(ExtendedVideoTypes type, ExtendedVideoSubTypes subtype)
    {
#pragma warning disable CS0612 // The SDK legacy AVC field still needs lossless restoration.
        var stream = new MediaStream
        {
            Type = MediaStreamType.Video,
            Index = 0,
            Codec = "hevc",
            BitDepth = 10,
            ExtendedVideoType = type,
            ExtendedVideoSubType = subtype,
            Rotation = -90,
            CodecTag = "hvc1",
            RefFrames = 4,
            IsAVC = false,
            NalLengthSize = "4",
            StreamStartTimeTicks = -10000,
            IsAnamorphic = true,
            IsHearingImpaired = true,
            TimeBase = "1/90000",
        };
        var source = new MediaSourceInfo { Container = "mp4", RunTimeTicks = TimeSpan.FromMinutes(5).Ticks, MediaStreams = new() { stream } };
        var serializer = new SnapshotSerializer();
        var snapshot = MediaInfoSnapshot.FromMediaSource(TestSources.Create(), source, DateTimeOffset.UtcNow);
        var restored = serializer.Deserialize(serializer.Serialize(snapshot)).ToMediaSource("restored").MediaStreams.Single();
        Assert.AreEqual(type, restored.ExtendedVideoType);
        Assert.AreEqual(subtype, restored.ExtendedVideoSubType);
        Assert.AreEqual(-90, restored.Rotation);
        Assert.AreEqual("hvc1", restored.CodecTag);
        Assert.AreEqual(4, restored.RefFrames);
        Assert.AreEqual(false, restored.IsAVC);
        Assert.AreEqual("4", restored.NalLengthSize);
        Assert.AreEqual(-10000L, restored.StreamStartTimeTicks);
        Assert.AreEqual(true, restored.IsAnamorphic);
        Assert.IsTrue(restored.IsHearingImpaired);
        Assert.AreEqual("1/90000", restored.TimeBase);
#pragma warning restore CS0612
    }

    [TestMethod]
    [DataRow("enum")]
    [DataRow("timebase")]
    [DataRow("codec-tag")]
    public void Serializer_ValidatesNewNegotiationFields(string field)
    {
        var snapshot = CreateSnapshot(TestSources.Create(), "mkv");
        var stream = snapshot.MediaStreams[0];
        if (field == "enum") stream.ExtendedVideoType = (ExtendedVideoTypes)999;
        if (field == "timebase") stream.TimeBase = "1/0";
        if (field == "codec-tag") stream.CodecTag = "https://secret.invalid/token";
        Assert.ThrowsExactly<InvalidDataException>(() => new SnapshotSerializer().Serialize(snapshot));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Store_UpgradesValidatedLegacySnapshotButProtectsCorruptLegacy(bool corrupt)
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.Path, "snapshots");
        Directory.CreateDirectory(directory);
        var source = TestSources.Create();
        var serializer = new SnapshotSerializer();
        var snapshot = CreateSnapshot(source, "mkv");
        var legacy = Encoding.UTF8.GetString(serializer.Serialize(snapshot))
            .Replace("\"SchemaVersion\":3", "\"SchemaVersion\":2", StringComparison.Ordinal);
        if (corrupt) legacy = legacy.Replace("\"Index\":0", "\"Index\":-1", StringComparison.Ordinal);
        var path = Path.Combine(directory, source.StorageKey + ".json");
        File.WriteAllText(path, legacy);
        var store = new MediaInfoStore(directory, serializer);
        Assert.IsFalse(store.TryLoad(source, out _));
        if (corrupt)
        {
            Assert.ThrowsExactly<InvalidDataException>(() => store.Save(source, snapshot));
            Assert.AreEqual(legacy, File.ReadAllText(path));
            return;
        }
        snapshot.MediaStreams[0].ExtendedVideoType = ExtendedVideoTypes.DolbyVision;
        store.Save(source, snapshot);
        Assert.IsTrue(store.TryLoad(source, out var restored));
        Assert.AreEqual(3, restored!.SchemaVersion);
        Assert.AreEqual(ExtendedVideoTypes.DolbyVision, restored.MediaStreams[0].ExtendedVideoType);
    }

    [TestMethod]
    public void Serializer_RejectsLegacySnapshotInsteadOfInventingMissingHdrFields()
    {
        var serializer = new SnapshotSerializer();
        var json = Encoding.UTF8.GetString(serializer.Serialize(CreateSnapshot(TestSources.Create(), "mkv")));
        var legacy = json.Replace("\"SchemaVersion\":3", "\"SchemaVersion\":2", StringComparison.Ordinal);
        Assert.AreNotEqual(json, legacy);
        Assert.ThrowsExactly<InvalidDataException>(() => serializer.Deserialize(Encoding.UTF8.GetBytes(legacy)));
    }

    [TestMethod]
    public void Serializer_UsesWhitelistAndDoesNotPersistSensitiveMediaFields()
    {
        var source = TestSources.Create();
        var media = new MediaSourceInfo
        {
            Path = "https://media.invalid/video?secret=do-not-store",
            DirectStreamUrl = "https://server.invalid/direct?token=do-not-store",
            RequiredHttpHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer do-not-store" },
            Container = "mkv",
            RunTimeTicks = TimeSpan.FromMinutes(90).Ticks,
            MediaStreams = new List<MediaStream>
            {
                new()
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = "h264",
                    Width = 1920,
                    Height = 1080,
                    Path = "https://subtitle.invalid/private",
                    DeliveryUrl = "/private/delivery",
                    Title = "private title",
                },
                new()
                {
                    Type = MediaStreamType.Subtitle,
                    Index = 1,
                    Codec = "srt",
                    IsExternal = true,
                    Path = "/private/subtitle.srt",
                },
                new()
                {
                    Type = MediaStreamType.EmbeddedImage,
                    Index = 2,
                    Codec = "mjpeg",
                },
                new()
                {
                    Type = MediaStreamType.Attachment,
                    Index = 3,
                    Codec = "ttf",
                },
            },
        };
        var serializer = new SnapshotSerializer();

        var data = serializer.Serialize(MediaInfoSnapshot.FromMediaSource(source, media, DateTimeOffset.UtcNow));
        var json = Encoding.UTF8.GetString(data);

        Assert.IsFalse(json.Contains("do-not-store", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("media.invalid", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("Authorization", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("private title", StringComparison.Ordinal));
        var restored = serializer.Deserialize(data);
        Assert.AreEqual(1, restored.MediaStreams.Count);
        Assert.AreEqual("h264", restored.MediaStreams[0].Codec);
        Assert.AreEqual(1920, restored.MediaStreams[0].Width);
    }

    [TestMethod]
    [DataRow("invalid-fingerprint")]
    [DataRow("null-stream")]
    [DataRow("unsupported-type")]
    [DataRow("negative-index")]
    [DataRow("negative-size")]
    [DataRow("unsafe-string")]
    public void Serializer_RejectsStructurallyInvalidSnapshotFields(string mode)
    {
        var source = TestSources.Create();
        var serializer = new SnapshotSerializer();
        var snapshot = CreateSnapshot(source, "mkv");
        snapshot.Size = 1024;
        var json = Encoding.UTF8.GetString(serializer.Serialize(snapshot));
        var invalid = mode switch
        {
            "invalid-fingerprint" => json.Replace(
                source.SourceFingerprint,
                source.SourceFingerprint.ToUpperInvariant(),
                StringComparison.Ordinal),
            "null-stream" => ReplaceMediaStreams(json, "null"),
            "unsupported-type" => json.Replace(
                "\"Type\":" + (int)MediaStreamType.Video,
                "\"Type\":999",
                StringComparison.Ordinal),
            "negative-index" => json.Replace("\"Index\":0", "\"Index\":-2", StringComparison.Ordinal),
            "negative-size" => json.Replace("\"Size\":1024", "\"Size\":-1", StringComparison.Ordinal),
            "unsafe-string" => json.Replace("\"Codec\":\"h264\"", "\"Codec\":\"../secret\"", StringComparison.Ordinal),
            _ => throw new InvalidOperationException("Unknown mutation mode."),
        };
        Assert.AreNotEqual(json, invalid, "The test mutation must change the serialized snapshot.");

        Assert.ThrowsExactly<InvalidDataException>(() =>
            serializer.Deserialize(Encoding.UTF8.GetBytes(invalid)));
    }

    [TestMethod]
    public void Store_TreatsInvalidSnapshotShapeAsCacheMissWithoutThrowing()
    {
        using var workspace = new TestWorkspace();
        var source = TestSources.Create();
        var serializer = new SnapshotSerializer();
        var json = Encoding.UTF8.GetString(serializer.Serialize(CreateSnapshot(source, "mkv")));
        var path = Path.Combine(workspace.Path, source.StorageKey + ".json");
        File.WriteAllText(path, ReplaceMediaStreams(json, "null"), Encoding.UTF8);
        var store = new MediaInfoStore(workspace.Path, serializer);

        Assert.IsFalse(store.TryLoad(source, out var snapshot));
        Assert.IsNull(snapshot);
    }

    [TestMethod]
    public void Store_DoesNotOverwriteStructurallyInvalidPrimaryAndBackup()
    {
        using var workspace = new TestWorkspace();
        var source = TestSources.Create();
        var serializer = new SnapshotSerializer();
        var store = new MediaInfoStore(workspace.Path, serializer);
        store.Save(source, CreateSnapshot(source, "mkv"));
        store.Save(source, CreateSnapshot(source, "mp4"));
        var path = Path.Combine(workspace.Path, source.StorageKey + ".json");
        var backupPath = path + ".bak";
        var validJson = Encoding.UTF8.GetString(serializer.Serialize(CreateSnapshot(source, "mov")));
        File.WriteAllText(path, ReplaceMediaStreams(validJson, "null"), Encoding.UTF8);
        File.WriteAllText(
            backupPath,
            validJson.Replace("\"Codec\":\"h264\"", "\"Codec\":\"../invalid\"", StringComparison.Ordinal),
            Encoding.UTF8);
        var primaryBefore = File.ReadAllBytes(path);
        var backupBefore = File.ReadAllBytes(backupPath);

        Assert.IsFalse(store.TryLoad(source, out _));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            store.Save(source, CreateSnapshot(source, "mov")));
        Assert.IsTrue(primaryBefore.AsSpan().SequenceEqual(File.ReadAllBytes(path)));
        Assert.IsTrue(backupBefore.AsSpan().SequenceEqual(File.ReadAllBytes(backupPath)));
    }

    [TestMethod]
    public void Store_AtomicallyKeepsBackupAndRecoversFromCorruptMain()
    {
        using var workspace = new TestWorkspace();
        var source = TestSources.Create();
        var serializer = new SnapshotSerializer();
        var store = new MediaInfoStore(workspace.Path, serializer);
        var first = CreateSnapshot(source, "mkv");
        var second = CreateSnapshot(source, "mp4");
        store.Save(source, first);
        store.Save(source, second);
        var path = Path.Combine(workspace.Path, source.StorageKey + ".json");
        var structurallyInvalid = ReplaceMediaStreams(
            Encoding.UTF8.GetString(serializer.Serialize(second)),
            "null");
        File.WriteAllText(path, structurallyInvalid, Encoding.UTF8);

        Assert.IsTrue(store.TryLoad(source, out var recovered));
        Assert.AreEqual("mkv", recovered!.Container);

        File.WriteAllText(path + ".bak", "also-not-json");
        Assert.ThrowsExactly<InvalidDataException>(() => store.Save(source, second));
    }

    [TestMethod]
    public void Store_RefusesSnapshotForDifferentSourceVersion()
    {
        using var workspace = new TestWorkspace();
        var store = new MediaInfoStore(workspace.Path, new SnapshotSerializer());
        Assert.ThrowsExactly<ArgumentException>(() =>
            store.Save(TestSources.Create("two"), CreateSnapshot(TestSources.Create(), "mkv")));
    }

    [TestMethod]
    public void Store_AcceptsMatchingContentAfterTimestampOnlyChange()
    {
        using var workspace = new TestWorkspace();
        var source = TestSources.Create();
        var touched = new SourceIdentity(
            source.StorageKey,
            source.SourceFingerprint,
            new Uri("https://source.invalid/entry"),
            "/library/item.strm",
            source.LocalFileLength,
            source.LocalLastWriteUtc.AddMinutes(1));
        var store = new MediaInfoStore(workspace.Path, new SnapshotSerializer());
        store.Save(source, CreateSnapshot(source, "mkv"));

        Assert.IsTrue(store.TryLoad(touched, out var recovered));
        Assert.AreEqual("mkv", recovered!.Container);
    }

    [TestMethod]
    [DataRow("missing-container")]
    [DataRow("short-runtime")]
    [DataRow("external-video")]
    public void Store_RejectsSnapshotWhenAnyRequiredTechnicalFieldIsIncomplete(string mode)
    {
        using var workspace = new TestWorkspace();
        var source = TestSources.Create();
        var store = new MediaInfoStore(workspace.Path, new SnapshotSerializer());
        var incomplete = MediaInfoSnapshot.FromMediaSource(
            source,
            new MediaSourceInfo
            {
                Container = mode == "missing-container" ? "strm" : "mkv",
                RunTimeTicks = mode == "short-runtime"
                    ? TimeSpan.FromMilliseconds(500).Ticks
                    : TimeSpan.FromHours(1).Ticks,
                MediaStreams = new List<MediaStream>
                {
                    new()
                    {
                        Type = MediaStreamType.Video,
                        Index = 0,
                        IsExternal = mode == "external-video",
                    },
                },
            },
            DateTimeOffset.UtcNow);

        Assert.ThrowsExactly<ArgumentException>(() => store.Save(source, incomplete));
    }

    [TestMethod]
    public void Store_AcceptsCompleteAudioSnapshot()
    {
        using var workspace = new TestWorkspace();
        var source = TestSources.Create("audio");
        var store = new MediaInfoStore(workspace.Path, new SnapshotSerializer());
        var snapshot = MediaInfoSnapshot.FromMediaSource(
            source,
            new MediaSourceInfo
            {
                Container = "flac",
                RunTimeTicks = TimeSpan.FromMinutes(4).Ticks,
                MediaStreams = new List<MediaStream>
                {
                    new() { Type = MediaStreamType.Audio, Index = 0, Codec = "flac" },
                },
            },
            DateTimeOffset.UtcNow);

        store.Save(source, snapshot);

        Assert.IsTrue(store.TryLoad(source, out var restored));
        Assert.IsNotNull(restored);
        Assert.AreEqual(MediaStreamType.Audio, restored!.MediaStreams.Single().Type);
    }

    [TestMethod]
    public void Snapshot_BoundsStreamCountAndSerializedSize()
    {
        var source = TestSources.Create();
        var media = new MediaSourceInfo
        {
            Container = "mkv",
            RunTimeTicks = TimeSpan.FromHours(1).Ticks,
            MediaStreams = Enumerable.Range(0, MediaInfoSnapshot.MaximumMediaStreams + 20)
                .Select(index => new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = index,
                    Codec = "aac",
                })
                .ToList(),
        };
        media.MediaStreams.Add(new MediaStream
        {
            Type = MediaStreamType.Video,
            Index = MediaInfoSnapshot.MaximumMediaStreams + 20,
            Codec = "h264",
        });
        var serializer = new SnapshotSerializer();
        var snapshot = MediaInfoSnapshot.FromMediaSource(source, media, DateTimeOffset.UtcNow);

        Assert.AreEqual(MediaInfoSnapshot.MaximumMediaStreams, snapshot.MediaStreams.Count);
        Assert.IsTrue(snapshot.MediaStreams.Any(stream => stream.Type == MediaStreamType.Video));
        Assert.IsTrue(serializer.Serialize(snapshot).Length <= SnapshotSerializer.MaximumSerializedBytes);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            serializer.Deserialize(new byte[SnapshotSerializer.MaximumSerializedBytes + 1]));
    }

    [TestMethod]
    public void Snapshot_StreamLimitRetainsInternalVideoNeededForCompleteness()
    {
        var source = TestSources.Create();
        var streams = Enumerable.Range(0, MediaInfoSnapshot.MaximumMediaStreams)
            .Select(index => new MediaStream
            {
                Type = MediaStreamType.Audio,
                Index = index,
                Codec = "aac",
            })
            .ToList();
        streams.Add(new MediaStream
        {
            Type = MediaStreamType.Video,
            Index = MediaInfoSnapshot.MaximumMediaStreams,
            Codec = "h264",
        });

        var snapshot = MediaInfoSnapshot.FromMediaSource(
            source,
            new MediaSourceInfo
            {
                Container = "mkv",
                RunTimeTicks = TimeSpan.FromHours(1).Ticks,
                MediaStreams = streams,
            },
            DateTimeOffset.UtcNow);

        Assert.AreEqual(MediaInfoSnapshot.MaximumMediaStreams, snapshot.MediaStreams.Count);
        Assert.AreEqual(MediaInfoSnapshot.MaximumMediaStreams - 1,
            snapshot.MediaStreams.Count(stream => stream.Type == MediaStreamType.Audio));
        Assert.IsTrue(snapshot.MediaStreams.Any(stream =>
            stream.Type == MediaStreamType.Video &&
            stream.Index == MediaInfoSnapshot.MaximumMediaStreams));
        Assert.IsTrue(snapshot.Matches(source));
    }

    private static MediaInfoSnapshot CreateSnapshot(SourceIdentity source, string container) =>
        MediaInfoSnapshot.FromMediaSource(source, new MediaSourceInfo
        {
            Container = container,
            RunTimeTicks = TimeSpan.FromHours(1).Ticks,
            MediaStreams = new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" } },
        }, DateTimeOffset.UtcNow);

    private static string ReplaceMediaStreams(string json, string replacement)
    {
        const string marker = "\"MediaStreams\":[";
        var start = json.IndexOf(marker, StringComparison.Ordinal);
        var end = json.LastIndexOf(']');
        Assert.IsTrue(start >= 0);
        Assert.IsTrue(end > start + marker.Length);
        return json.Substring(0, start + marker.Length) + replacement + json.Substring(end);
    }
}

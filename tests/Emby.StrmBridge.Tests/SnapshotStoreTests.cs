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
        File.WriteAllText(path, "not-json");

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
    public void Snapshot_BoundsStreamCountAndSerializedSize()
    {
        var source = TestSources.Create();
        var media = new MediaSourceInfo
        {
            MediaStreams = Enumerable.Range(0, MediaInfoSnapshot.MaximumMediaStreams + 20)
                .Select(index => new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = index,
                    Codec = "aac",
                })
                .ToList(),
        };
        var serializer = new SnapshotSerializer();
        var snapshot = MediaInfoSnapshot.FromMediaSource(source, media, DateTimeOffset.UtcNow);

        Assert.AreEqual(MediaInfoSnapshot.MaximumMediaStreams, snapshot.MediaStreams.Count);
        Assert.IsTrue(serializer.Serialize(snapshot).Length <= SnapshotSerializer.MaximumSerializedBytes);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            serializer.Deserialize(new byte[SnapshotSerializer.MaximumSerializedBytes + 1]));
    }

    private static MediaInfoSnapshot CreateSnapshot(SourceIdentity source, string container) =>
        MediaInfoSnapshot.FromMediaSource(source, new MediaSourceInfo
        {
            Container = container,
            RunTimeTicks = TimeSpan.FromHours(1).Ticks,
            MediaStreams = new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" } },
        }, DateTimeOffset.UtcNow);
}

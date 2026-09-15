using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Runtime;
using Emby.StrmBridge.Subtitles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class SharedSubtitleNegotiationTests
{
    internal static DeviceProfile Profile() => new()
    {
        Name = SharedSubtitleNegotiation.ProfileName,
        MaxStreamingBitrate = 80_000_000,
        DirectPlayProfiles = new[] { new DirectPlayProfile { Type = DlnaProfileType.Video, Container = "mkv", VideoCodec = "h264", AudioCodec = "aac" } },
        CodecProfiles = new[] { new CodecProfile { Type = CodecType.Video, Codec = "h264" } },
        TranscodingProfiles = new[] {
            new TranscodingProfile { Type = DlnaProfileType.Video, Context = EncodingContext.Streaming, Container = "mkv", Protocol = "http" },
            new TranscodingProfile { Type = DlnaProfileType.Video, Context = EncodingContext.Streaming, Container = "m4s,ts", Protocol = "hls",
                VideoCodec = "h264,hevc", AudioCodec = "aac,ac3", MaxWidth = 3840, MaxAudioChannels = "6", ManifestSubtitles = "vtt" } },
        SubtitleProfiles = new[] { new SubtitleProfile { Format = "vtt", Method = SubtitleDeliveryMethod.Hls } },
    };
    internal static MediaSourceInfo Source() => new()
    {
        Id = "source",
        Container = "mkv",
        Protocol = MediaProtocol.Http,
        SupportsDirectPlay = true,
        SupportsDirectStream = true,
        SupportsTranscoding = true,
        MediaStreams = new() { new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, Codec = "ass" } },
    };

    [TestMethod]
    public void LegacyCapabilityWithoutObservableClockDoesNotForceSharedHls()
    {
        var profile = Profile(); profile.Name = "STRM Bridge Web subtitles v1";
        Assert.IsNull(SharedSubtitleNegotiation.CreateProfile(Source(), profile));
    }

    [TestMethod]
    public void Profile_UsesDeclaredHlsAndKeepsCodecLimitsWithoutChangingOtherVersions()
    {
        var original = Profile(); var source = Source();
        var prepared = SharedSubtitleNegotiation.CreateProfile(source, original)!;
        Assert.IsNotNull(prepared);
        Assert.AreNotSame(original, prepared);
        var hls = prepared.TranscodingProfiles.Single();
        Assert.AreEqual("ts", hls.Container);
        Assert.AreEqual("h264,hevc", hls.VideoCodec);
        Assert.AreEqual("aac,ac3", hls.AudioCodec);
        Assert.AreEqual(3840, hls.MaxWidth);
        Assert.AreEqual("6", hls.MaxAudioChannels);
        Assert.AreEqual(original.MaxStreamingBitrate, prepared.MaxStreamingBitrate);
        Assert.AreSame(original.CodecProfiles, prepared.CodecProfiles);
        Assert.IsTrue(prepared.SubtitleProfiles.Take(4).All(p => p.Method == SubtitleDeliveryMethod.External));
        Assert.IsFalse(prepared.SubtitleProfiles.Any(p => p.Method == SubtitleDeliveryMethod.Hls));
        Assert.IsNull(hls.ManifestSubtitles);
        Assert.AreEqual(2, original.TranscodingProfiles.Length);
        Assert.AreEqual("m4s,ts", original.TranscodingProfiles[1].Container);
        Assert.AreEqual("vtt", original.TranscodingProfiles[1].ManifestSubtitles);
        Assert.IsTrue(source.SupportsDirectStream, "Only the host may change a source's playback capabilities.");
    }

    [TestMethod]
    public void CombinedFmp4Capability_DoesNotAdvertiseUnsupportedTsCodecs()
    {
        var profile = Profile();
        profile.TranscodingProfiles[1].VideoCodec = "av1,vp9,h264";
        profile.TranscodingProfiles[1].AudioCodec = "opus,flac,aac";
        var prepared = SharedSubtitleNegotiation.CreateProfile(Source(), profile)!;
        Assert.AreEqual("h264", prepared.TranscodingProfiles.Single().VideoCodec);
        Assert.AreEqual("aac", prepared.TranscodingProfiles.Single().AudioCodec);
        Assert.AreEqual("av1,vp9,h264", profile.TranscodingProfiles[1].VideoCodec);
        profile.TranscodingProfiles[1].VideoCodec = "av1,vp9";
        Assert.IsNull(SharedSubtitleNegotiation.CreateProfile(Source(), profile));
    }

    [TestMethod]
    [DataRow("unmarked")]
    [DataRow("no-hls")]
    [DataRow("fmp4-only")]
    [DataRow("external")]
    [DataRow("bitmap")]
    [DataRow("m2ts")]
    [DataRow("headers")]
    [DataRow("dynamic")]
    [DataRow("too-many")]
    [DataRow("direct-only")]
    public void UnsupportedSourceOrCapability_DoesNotManufactureAPlaybackPath(string variant)
    {
        var source = Source(); var profile = Profile();
        switch (variant)
        {
            case "direct-only": source.SupportsTranscoding = false; break;
            case "unmarked": profile.Name = "third-party"; break;
            case "no-hls": profile.TranscodingProfiles = profile.TranscodingProfiles.Take(1).ToArray(); break;
            case "fmp4-only": profile.TranscodingProfiles[1].Container = "m4s"; break;
            case "external": source.MediaStreams[0].IsExternal = true; break;
            case "bitmap": source.MediaStreams[0].Codec = "pgssub"; break;
            case "m2ts": source.Container = "m2ts"; break;
            case "headers": source.RequiredHttpHeaders = new() { ["fixture"] = "value" }; break;
            case "dynamic": source.RequiresOpening = true; break;
            case "too-many": source.MediaStreams = Enumerable.Range(0, 9).Select(i => new MediaStream { Index = i, Type = MediaStreamType.Subtitle, Codec = "ass" }).ToList(); break;
        }
        Assert.IsNull(SharedSubtitleNegotiation.CreateProfile(source, profile));
    }

    [TestMethod]
    [DataRow("enabled", true)]
    [DataRow("disabled", false)]
    [DataRow("native", false)]
    [DataRow("other-library", false)]
    [DataRow("changed", false)]
    [DataRow("denied", false)]
    [DataRow("no-transcoding", false)]
    [DataRow("inputs-unavailable", false)]
    public void Negotiation_IsScopedToSelectedUnchangedStrmAndHostPolicy(string variant, bool expected)
    {
        using var workspace = new TestWorkspace(); using var runtime = new PluginRuntime();
        runtime.Initialize(workspace.Path, new ManualClock());
        var folder = new Folder { Id = Guid.NewGuid() };
        var item = new Movie { Id = Guid.NewGuid(), InternalId = 123, Path = workspace.Write("fixture.strm", "https://source.invalid/fixture.mkv") };
        var source = Source(); source.Path = "https://source.invalid/fixture.mkv";
        var library = TestProxy.Create<ILibraryManager>((method, _) => method.Name switch
        {
            "GetItemById" => item,
            "GetCollectionFolders" => new[] { folder },
            _ => TestDispatchProxy.DefaultValue(method.ReturnType),
        });
        var sources = TestProxy.Create<IMediaSourceManager>((method, _) => method.Name == "GetStaticMediaSources"
            ? new List<MediaSourceInfo> { source } : TestDispatchProxy.DefaultValue(method.ReturnType));
        runtime.UpdateOptions(new PluginConfiguration
        {
            Enabled = true,
            EnableSubtitles = variant != "disabled",
            PlaybackMode = variant == "native" ? PlaybackRoutingMode.Native : PlaybackRoutingMode.Adaptive,
            IncludedLibraryIds = new[] { (variant == "other-library" ? Guid.NewGuid() : folder.Id).ToString("N") }
        }, false);
        var user = new User();
        var property = user.GetType().GetProperty("Policy")!;
        property.SetValue(user, Activator.CreateInstance(property.PropertyType));
        user.Policy.EnableMediaPlayback = true; user.Policy.EnablePlaybackRemuxing = variant != "denied";
        user.Policy.EnableVideoPlaybackTranscoding = false; user.Policy.EnableAudioPlaybackTranscoding = false;
        if (variant == "changed") source.Path = "https://source.invalid/different.mkv";
        var result = new SharedSubtitleNegotiation(runtime, library, sources, () => variant != "inputs-unavailable").Prepare(123, source, Profile(), user, variant != "no-transcoding");
        Assert.AreEqual(expected, result is not null);
    }
}

using Emby.StrmBridge.Playback;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class HlsPlaylistRewriterTests
{
    [TestMethod]
    public void Manifest_RewritesLinesAndUriAttributesAgainstCurrentPlaylist()
    {
        var manifest = "\uFEFF#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"keys/key.bin\"\n" +
                       "#EXT-X-MAP:URI=\"init.mp4\"\nsegment-1.ts\n";
        var routed = new List<Uri>();

        var result = HlsPlaylistRewriter.Rewrite(
            manifest,
            new Uri("https://media.invalid/hls/main/index.m3u8"),
            uri =>
            {
                routed.Add(uri);
                return "/gateway/" + routed.Count;
            });

        CollectionAssert.AreEqual(new[]
        {
            new Uri("https://media.invalid/hls/main/keys/key.bin"),
            new Uri("https://media.invalid/hls/main/init.mp4"),
            new Uri("https://media.invalid/hls/main/segment-1.ts"),
        }, routed);
        StringAssert.Contains(result, "URI=\"/gateway/1\"");
        StringAssert.Contains(result, "URI=\"/gateway/2\"");
        StringAssert.Contains(result, "/gateway/3");
    }

    [TestMethod]
    public void Manifest_RequiresExtM3uAndHttpResources()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => HlsPlaylistRewriter.Rewrite(
            "segment.ts",
            new Uri("https://media.invalid/index.m3u8"),
            _ => "/gateway"));
        Assert.ThrowsExactly<InvalidOperationException>(() => HlsPlaylistRewriter.Rewrite(
            "#EXTM3U\nfile:///private/media.ts",
            new Uri("https://media.invalid/index.m3u8"),
            _ => "/gateway"));
    }
}

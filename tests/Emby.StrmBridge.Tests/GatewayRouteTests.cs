using Emby.StrmBridge.Api;
using Emby.StrmBridge.Playback;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class GatewayRouteTests
{
    [TestMethod]
    public void PlaybackRoutes_UseCapabilityTicketAuthentication()
    {
        Assert.IsTrue(Attribute.IsDefined(
            typeof(GetStrmBridgePlayback), typeof(UnauthenticatedAttribute), inherit: true));
        Assert.IsTrue(Attribute.IsDefined(
            typeof(GetStrmBridgeHlsResource), typeof(UnauthenticatedAttribute), inherit: true));
    }

    [TestMethod]
    public void RouteBuilder_PreservesCurrentApiPathBaseAndSanitizesContainer()
    {
        var ticket = new string('a', 43);
        Assert.AreEqual(
            "/emby/StrmBridge/Playback/v3/" + ticket + "/stream.mkv",
            GatewayRouteBuilder.CreatePlaybackRoute("/emby", ticket, "MKV"));
        Assert.AreEqual(
            "/StrmBridge/Playback/v3/" + ticket + "/stream",
            GatewayRouteBuilder.CreatePlaybackRoute("//invalid", ticket, "../private"));
    }

    [TestMethod]
    public void RouteBuilder_DerivesApiPathBaseFromPlaybackAndGatewayRequests()
    {
        var playback = CreateRequest("/emby/Items/abc/PlaybackInfo?UserId=user");
        var gateway = CreateRequest("/emby/StrmBridge/Playback/v3/ticket/stream.mkv");
        var transcode = CreateRequest("/emby/Videos/abc/master.m3u8?MediaSourceId=source");

        Assert.AreEqual("/emby", GatewayRouteBuilder.GetApiPathBase(playback));
        Assert.AreEqual("/emby", GatewayRouteBuilder.GetApiPathBase(gateway));
        Assert.AreEqual("/emby", GatewayRouteBuilder.GetApiPathBase(transcode));
    }

    [TestMethod]
    public void InternalRoute_UsesRuntimeLocalOriginAndCurrentApiPathBase()
    {
        var ticket = new string('a', 43);
        Assert.AreEqual(
            "http://127.0.0.1:8096/emby/StrmBridge/Playback/v3/" + ticket + "/stream.m2ts",
            GatewayRouteBuilder.CreateInternalPlaybackRoute(
                "http://127.0.0.1:8096/ignored", "/emby", ticket, "m2ts"));
        Assert.IsNull(GatewayRouteBuilder.CreateInternalPlaybackRoute(
            "file:///private/socket", "/emby", ticket, "m2ts"));
        Assert.IsNull(GatewayRouteBuilder.CreateInternalPlaybackRoute(
            "http://user@127.0.0.1:8096", "/emby", ticket, "m2ts"));
        Assert.IsNull(GatewayRouteBuilder.CreateInternalPlaybackRoute(
            "http://media.invalid:8096", "/emby", ticket, "m2ts"));
        Assert.AreEqual(
            "http://127.0.0.1:8096/emby/StrmBridge/Playback/v3/" + ticket + "/stream.m2ts",
            GatewayRouteBuilder.CreateInternalPlaybackRoute(
                "http://127.0.0.1:8096/emby/", string.Empty, ticket, "m2ts"));
    }

    [TestMethod]
    public void ServerFfmpegRoutes_AcceptOnlyLoopbackCallers()
    {
        Assert.IsTrue(GatewayService.IsLoopbackRequest(System.Net.IPAddress.Loopback));
        Assert.IsTrue(GatewayService.IsLoopbackRequest(System.Net.IPAddress.IPv6Loopback));
        Assert.IsTrue(GatewayService.IsLoopbackRequest(
            System.Net.IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.IsFalse(GatewayService.IsLoopbackRequest(System.Net.IPAddress.Parse("192.0.2.1")));
        Assert.IsFalse(GatewayService.IsLoopbackRequest(null));
    }

    [TestMethod]
    public void ServerFfmpegRedirects_AreMethodPreservingAndShortLived()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var ffmpeg = GatewayService.CreateRedirectResponsePolicy(
            Emby.StrmBridge.Domain.PlaybackTicketPurpose.ServerFfmpeg,
            now);
        var client = GatewayService.CreateRedirectResponsePolicy(
            Emby.StrmBridge.Domain.PlaybackTicketPurpose.DirectClient,
            now);

        Assert.AreEqual(307, ffmpeg.StatusCode);
        StringAssert.StartsWith(ffmpeg.CacheControl, "max-age=");
        StringAssert.Contains(ffmpeg.CacheControl, "private");
        Assert.AreEqual(
            (now + GatewayService.ServerFfmpegRedirectLifetime).UtcDateTime.ToString("R"),
            ffmpeg.Expires);
        Assert.AreEqual(302, client.StatusCode);
        Assert.AreEqual("private, no-store", client.CacheControl);
        Assert.IsNull(client.Expires);
    }

    [TestMethod]
    [DataRow(5, 30, 5)]
    [DataRow(30, 7, 7)]
    [DataRow(30, 30, 20)]
    [DataRow(0, 30, 0)]
    [DataRow(30, -1, 0)]
    public void ServerFfmpegRedirects_DoNotOutliveTicketOrSourceLease(int ticketSeconds, int sourceSeconds, int expectedAge)
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var response = GatewayService.CreateRedirectResponsePolicy(
            Emby.StrmBridge.Domain.PlaybackTicketPurpose.ServerFfmpeg, now,
            now.AddSeconds(ticketSeconds), now.AddSeconds(sourceSeconds));
        Assert.AreEqual(307, response.StatusCode);
        if (expectedAge == 0)
        {
            Assert.AreEqual("private, no-store", response.CacheControl);
            Assert.IsNull(response.Expires);
        }
        else
        {
            Assert.AreEqual("max-age=" + expectedAge + ", private, must-revalidate", response.CacheControl);
            Assert.AreEqual(now.AddSeconds(expectedAge).UtcDateTime.ToString("R"), response.Expires);
        }
    }

    [TestMethod]
    [DataRow(20, 30, 30, 20)]
    [DataRow(20, 7, 30, 7)]
    [DataRow(20, 30, 5, 5)]
    [DataRow(0, 30, 30, 0)]
    public void DirectClientRedirectCache_IsExplicitAndBounded(
        int configuredSeconds,
        int ticketSeconds,
        int routeSeconds,
        int expectedAge)
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var response = GatewayService.CreateRedirectResponsePolicy(
            Emby.StrmBridge.Domain.PlaybackTicketPurpose.DirectClient,
            now,
            now.AddSeconds(ticketSeconds),
            now.AddSeconds(routeSeconds),
            configuredSeconds);

        Assert.AreEqual(302, response.StatusCode);
        if (expectedAge == 0)
        {
            Assert.AreEqual("private, no-store", response.CacheControl);
            Assert.IsNull(response.Expires);
        }
        else
        {
            Assert.AreEqual("max-age=" + expectedAge + ", private, must-revalidate", response.CacheControl);
            Assert.AreEqual(now.AddSeconds(expectedAge).UtcDateTime.ToString("R"), response.Expires);
        }
    }

    [TestMethod]
    [DataRow(null, null, false)]
    [DataRow("max-age=20, private", null, false)]
    [DataRow("max-age=01", null, false)]
    [DataRow("max-age = 0, private", null, true)]
    [DataRow("MAX-AGE=\"0\"", null, true)]
    [DataRow("private, no-cache", null, true)]
    [DataRow("no-store", null, true)]
    [DataRow(null, "no-cache", true)]
    public void DirectClientRedirectCache_RecognizesExplicitRefreshRequests(
        string? cacheControl,
        string? pragma,
        bool expected)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (cacheControl is not null) headers["Cache-Control"] = cacheControl;
        if (pragma is not null) headers["Pragma"] = pragma;

        Assert.AreEqual(expected, GatewayService.RequestsFreshRedirect(headers));
    }

    private static IRequest CreateRequest(string rawUrl) =>
        TestProxy.Create<IRequest>((method, _) => method.Name == "get_RawUrl"
            ? rawUrl
            : TestDispatchProxy.DefaultValue(method.ReturnType));
}

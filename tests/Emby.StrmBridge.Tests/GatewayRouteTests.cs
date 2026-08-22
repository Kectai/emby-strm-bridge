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
            "/emby/StrmBridge/Playback/v2/" + ticket + "/stream.mkv",
            GatewayRouteBuilder.CreatePlaybackRoute("/emby", ticket, "MKV"));
        Assert.AreEqual(
            "/StrmBridge/Playback/v2/" + ticket + "/stream",
            GatewayRouteBuilder.CreatePlaybackRoute("//invalid", ticket, "../private"));
    }

    [TestMethod]
    public void RouteBuilder_DerivesApiPathBaseFromPlaybackAndGatewayRequests()
    {
        var playback = CreateRequest("/emby/Items/abc/PlaybackInfo?UserId=user");
        var gateway = CreateRequest("/emby/StrmBridge/Playback/v2/ticket/stream.mkv");
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
            "http://127.0.0.1:8096/emby/StrmBridge/Playback/v2/" + ticket + "/stream.m2ts",
            GatewayRouteBuilder.CreateInternalPlaybackRoute(
                "http://127.0.0.1:8096/ignored", "/emby", ticket, "m2ts"));
        Assert.IsNull(GatewayRouteBuilder.CreateInternalPlaybackRoute(
            "file:///private/socket", "/emby", ticket, "m2ts"));
        Assert.IsNull(GatewayRouteBuilder.CreateInternalPlaybackRoute(
            "http://user@127.0.0.1:8096", "/emby", ticket, "m2ts"));
        Assert.IsNull(GatewayRouteBuilder.CreateInternalPlaybackRoute(
            "http://media.invalid:8096", "/emby", ticket, "m2ts"));
        Assert.AreEqual(
            "http://127.0.0.1:8096/emby/StrmBridge/Playback/v2/" + ticket + "/stream.m2ts",
            GatewayRouteBuilder.CreateInternalPlaybackRoute(
                "http://127.0.0.1:8096/emby/", string.Empty, ticket, "m2ts"));
    }

    private static IRequest CreateRequest(string rawUrl) =>
        TestProxy.Create<IRequest>((method, _) => method.Name == "get_RawUrl"
            ? rawUrl
            : TestDispatchProxy.DefaultValue(method.ReturnType));
}

using Emby.StrmBridge.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System.Net;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class GatewayRequestPolicyTests
{
    [TestMethod]
    public void GatewayRoute_BypassesHostAuthenticationForTicketValidation()
    {
        Assert.IsTrue(Attribute.IsDefined(
            typeof(GetStrmBridgeRedirect),
            typeof(UnauthenticatedAttribute),
            inherit: true));
    }

    [TestMethod]
    public void DirectLoopbackWithoutForwardingHeaders_IsAccepted()
    {
        var request = CreateRequest("127.0.0.1", null, null);

        Assert.IsTrue(GatewayService.IsDirectLoopbackRequest(request));
    }

    [TestMethod]
    public void RemoteOrForwardedRequest_IsNotTreatedAsLocalServer()
    {
        Assert.IsFalse(GatewayService.IsDirectLoopbackRequest(CreateRequest("192.0.2.10", null, null)));
        Assert.IsFalse(GatewayService.IsDirectLoopbackRequest(CreateRequest("127.0.0.1", "192.0.2.10", null)));
        Assert.IsFalse(GatewayService.IsDirectLoopbackRequest(CreateRequest("::1", null, "192.0.2.10")));
        Assert.IsFalse(GatewayService.IsDirectLoopbackRequest(
            CreateRequest("127.0.0.1", null, null, "for=192.0.2.10")));
    }

    private static IRequest CreateRequest(
        string remoteIp,
        string? forwardedFor,
        string? realIp,
        string? forwarded = null) =>
        TestProxy.Create<IRequest>((method, _) => method.Name switch
        {
            "get_RemoteIp" => IPAddress.Parse(remoteIp),
            "get_XForwardedFor" => forwardedFor,
            "get_XRealIp" => realIp,
            "get_Headers" => CreateHeaders(forwarded),
            _ => TestDispatchProxy.DefaultValue(method.ReturnType),
        });

    private static QueryParamCollection CreateHeaders(string? forwarded)
    {
        var headers = new QueryParamCollection();
        if (forwarded is not null) headers.Add("Forwarded", forwarded);
        return headers;
    }
}

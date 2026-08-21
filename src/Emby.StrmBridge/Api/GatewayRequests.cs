using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Api;

[Route("/StrmBridge/Gateway/{Ticket}/stream", "GET,HEAD", Summary = "Resolves a short-lived STRM redirect lease")]
[Unauthenticated]
public sealed class GetStrmBridgeRedirect
{
    public string Ticket { get; set; } = string.Empty;
}

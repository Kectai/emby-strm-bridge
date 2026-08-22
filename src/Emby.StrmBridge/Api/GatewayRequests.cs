using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Api;

[Route("/StrmBridge/Playback/v2/{Ticket}/{FileName}", "GET,HEAD", Summary = "Streams a ticketed STRM source")]
[Unauthenticated]
public sealed class GetStrmBridgePlayback
{
    public string Ticket { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;
}

[Route("/StrmBridge/Playback/v2/{ParentTicket}/hls/{Ticket}/{FileName}", "GET,HEAD", Summary = "Streams a ticketed HLS resource")]
[Unauthenticated]
public sealed class GetStrmBridgeHlsResource
{
    public string ParentTicket { get; set; } = string.Empty;

    public string Ticket { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;
}

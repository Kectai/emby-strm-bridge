using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Api;

[Route("/StrmBridge/Maintenance/Extract", "POST", Summary = "Runs STRM media-information extraction")]
public sealed class RunStrmBridgeExtraction
{
    public bool Force { get; set; }

    public string? ItemId { get; set; }

    public string? LibraryId { get; set; }
}

[Route("/StrmBridge/Maintenance/Restore", "POST", Summary = "Restores STRM media information from safe snapshots")]
public sealed class RunStrmBridgeRestore { }

[Route("/StrmBridge/Maintenance/Cleanup", "POST", Summary = "Removes orphaned STRM media-information snapshots")]
public sealed class RunStrmBridgeCleanup { }

[Route("/StrmBridge/Maintenance/Clear", "POST", Summary = "Clears stored STRM Bridge media information")]
public sealed class RunStrmBridgeClear { }

[Route("/StrmBridge/Admin/Health", "GET", Summary = "Returns STRM Bridge playback health")]
public sealed class GetStrmBridgeHealth { }

[Route("/StrmBridge/Admin/Diagnostics", "GET", Summary = "Returns privacy-safe STRM Bridge diagnostics")]
public sealed class GetStrmBridgeDiagnostics { }

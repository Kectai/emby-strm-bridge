using System;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Api;

[Route("/StrmBridge/Managed/Prototype", "POST", Summary = "Creates a memory-only managed STRM feasibility record")]
public sealed class CreateManagedStrmPrototype
{
    public string SourceUrl { get; set; } = string.Empty;

    public string? ContainerHint { get; set; }

    public bool ConfirmExperimental { get; set; }
}

[Route("/StrmBridge/Managed/Prototype", "DELETE", Summary = "Clears memory-only managed STRM feasibility records")]
public sealed class ClearManagedStrmPrototypes { }

[Route("/StrmBridge/Managed/v1/{ManagedId}", "GET,HEAD", Summary = "Resolves a memory-only managed STRM feasibility record")]
[Unauthenticated]
public sealed class GetManagedStrmPrototype
{
    public string ManagedId { get; set; } = string.Empty;
}

public sealed class ManagedStrmPrototypeResponse
{
    public string RelativePath { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; set; }
}

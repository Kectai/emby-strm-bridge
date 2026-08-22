# Compatibility

## Current baseline

- target framework: `netstandard2.1`
- compile-time Emby SDK: `MediaBrowser.Server.Core 4.9.1.80`
- playback ABI: `Emby.Server.MediaEncoding 4.9.5.x`
- Harmony runtime: `Lib.Harmony 2.4.2`, `net6.0`

The local Emby Server 4.9.5.0 assemblies were inspected by `tools/Emby.ApiProbe`. The implementation verifies these runtime signatures:

```text
Task<object> MediaInfoService.Get(GetPlaybackInfo)
Task<object> MediaInfoService.Post(GetPostedPlaybackInfo)
Task<object> BaseProgressiveStreamingService.ProcessRequest(StreamRequest, bool)
Task<TranscodingJob> BaseStreamingService.StartFfMpeg(StreamState, string, CancellationToken, bool)
PlaybackInfoResponse.MediaSources
MediaSourceInfo.DirectStreamUrl
```

An ABI mismatch leaves playback native and logs one fixed compatibility event. Extraction, persistence and maintenance continue independently.

## Playback clients

The plugin exposes ordinary HTTP GET/HEAD routes with Range support. It supports clients that consume `DirectStreamUrl` and clients that request Emby's standard static-video route with an exact media-source ID. Client-specific names and vendor identifiers are absent from routing decisions.

The standard-route adapter activates only for `Static=true` requests whose exact media source maps to a `.strm` item in a participating library. When Emby starts server-side transcoding, a separate adapter revalidates the per-job item, exact media-source ID and unchanged STRM source, then changes that job's `StreamState` input to a gateway URL using the local API origin reported by the running Emby instance. PlaybackInfo source and probe paths stay native. Local files, unmatched media versions and libraries outside the configured scope remain on Emby's native path.

## Upstream behavior

Adaptive mode relays both redirected and direct-body sources. This keeps the actual playback User-Agent and request headers on the server-side redirect chain. RedirectOnly mode exposes the validated final URL to the client and therefore suits sources whose final URL is independent of the resolving request.

Adaptive mode follows the chain and relays the final response within the same gateway request. Release validation uses the live 4.9.5.x matrix in [TESTING.md](TESTING.md).

## Host verification

Follow [TESTING.md](TESTING.md) after installation. The required first signal is:

```text
STRM_BRIDGE_PATCH_READY abi=4.9.5.0 targets=4
```

`Native` mode is the operational fallback for an unsupported host and keeps PlaybackInfo, standard-video execution and FFmpeg input unchanged.

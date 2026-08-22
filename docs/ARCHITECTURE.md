# Architecture

## Runtime flow

1. `StrmSourcePolicy` reads a selected local STRM as a bounded, stable, UTF-8 regular file and creates HMAC-backed source identities.
2. Emby calculates its native PlaybackInfo response, including permissions, media versions, stream information and transcode capabilities.
3. `HarmonyPatchHost` verifies the Emby 4.9.5.0 ABI and installs two PlaybackInfo postfixes, a prefix on the progressive request executor and a prefix immediately before FFmpeg startup. The progressive prefix accepts only an actual video-service instance.
4. `PlaybackInfoProcessor` maps each response source to its owning STRM Item, verifies the current static source, creates a 256-bit memory-only ticket and clones the source with a relative `DirectStreamUrl`. Native source and probe paths remain unchanged.
5. `NativeVideoStreamProcessor` handles clients that use Emby's standard static-video route. It requires GET/HEAD, `Static=true`, an exact media-source ID, a participating library, a local `.strm` owner and an unchanged eligible static source before invoking the same gateway in-process.
6. `TranscodeInputProcessor` handles Emby-created FFmpeg jobs. It revalidates the selected media-source ID, participating library, local `.strm` owner and unchanged static source, then changes only the per-job `StreamState` media paths and protocols to a runtime-derived loopback gateway URL.
7. `GatewayService` validates the ticket, media-library scope, item visibility when authentication is present, and the unchanged STRM fingerprint.
8. `GatewayTransport` forwards approved request headers, follows up to the configured redirect limit and retains the real client User-Agent. A keyed single-flight gate merges concurrent first resolutions, and a short-lived redirect lease lets related Range requests reuse the validated effective address.
9. `TransportPlanner` selects a validated 302, ordinary file relay, or HLS relay.
10. Ordinary relay streams the upstream response with HTTP status, Range metadata, request cancellation and a full-body idle timeout.
11. HLS recognition uses response metadata and a replayable content prefix. Relay bounds and parses each manifest, replaces URI lines and URI attributes with reusable child capability routes, and relays child manifests, segments, keys and initialization data.

## Identity contract

The PlaybackInfo processor preserves:

- source count and order
- `MediaSourceInfo.Id`
- item and version names
- container and stream metadata
- direct-play, direct-stream and transcode capability fields

The processor changes only `DirectStreamUrl` and API-key attachment behavior for exact mapped STRM sources. The client route is relative to the active API base and carries a memory-only capability without an API key in the URL. It prepares a complete result before assigning it to the response; mapping or ticket failures retain the native result. The standard static-video adapter retains the request method, Range context, authorization context and response pipeline while substituting gateway execution only for an exact STRM match. The transcode adapter modifies only the current FFmpeg job state after the same exact source checks; it does not modify the library item or persisted media source.

## ABI gate

The current ABI catalog accepts `Emby.Server.MediaEncoding` version 4.9.5.x and verifies these public service shapes at runtime:

```text
Task<object> MediaInfoService.Get(GetPlaybackInfo)
Task<object> MediaInfoService.Post(GetPostedPlaybackInfo)
Task<object> BaseProgressiveStreamingService.ProcessRequest(StreamRequest, bool)
Task<TranscodingJob> BaseStreamingService.StartFfMpeg(StreamState, string, CancellationToken, bool)
```

Patch ownership uses `Emby.StrmBridge.Playback.v3`. Startup verifies all four target methods and ownership. Shutdown removes all patches associated with that ID. An unsupported or changed ABI leaves playback in native mode while extraction remains available.

## Resource bounds

- STRM file: 16 KiB
- playback tickets: 4096
- HLS child tickets: 20000
- ticket entropy: 256 bit
- preview lifetime: 10 minutes
- maximum ticket lifetime: 24 hours
- reconnect grace: 2 hours
- redirect hops: default 5, range 1–8
- redirect leases: 30 seconds, maximum 4096, memory only
- relay concurrency: default 4, range 1–16
- upstream timeout: default 120 seconds, range 10–180
- HLS manifest: 2 MiB, 20000 lines, 10000 URIs, depth 8
- extraction concurrency: default 1, maximum 2
- extraction timeout: default 120 seconds, range 30–180

## Persistence

Playback tickets, upstream addresses, short-lived redirect leases, HLS child mappings and active response objects stay in memory. Media-information persistence remains below Emby's plugin configuration directory:

```text
Emby.StrmBridge/
├── identity.key
├── mediainfo/
│   ├── <hmac-storage-key>.json
│   └── <hmac-storage-key>.json.bak
└── state/
    ├── extraction-state.json
    └── extraction-state.json.bak
```

The plugin keeps media-library STRM files read-only.

## Implementation documents

- [Playback gateway design](PLAYBACK_GATEWAY_DESIGN.md)
- [Security](SECURITY.md)

# Architecture

## Runtime flow

1. `StrmSourcePolicy` reads a selected local STRM as a bounded, stable, UTF-8 regular file and creates HMAC-backed source identities.
2. Emby calculates its native PlaybackInfo response, including permissions, media versions, stream information and transcode capabilities.
3. `HarmonyPatchHost` verifies the Emby 4.9.5.0 ABI and installs two PlaybackInfo postfixes, prefixes on the progressive request executor and transcode-state creation, and a prefix on the final FFmpeg command runner. The progressive prefix accepts only an actual video-service instance.
4. `PlaybackInfoProcessor` maps each response source to its owning STRM Item, verifies the current static source, creates a 256-bit memory-only direct-client ticket and clones the source with a relative `DirectStreamUrl`. Native source and probe paths remain unchanged, and this client-facing stage performs no fast-seek probes.
5. `NativeVideoStreamProcessor` handles clients that use Emby's standard static-video route. It requires GET/HEAD, `Static=true`, an exact media-source ID, a participating library, a local `.strm` owner and an unchanged eligible static source before invoking the same gateway in-process with a direct-client ticket.
6. `TranscodeInputProcessor` handles Emby-created FFmpeg jobs. It revalidates the selected media-source ID, participating library, local `.strm` owner and unchanged static source, and issues a server-FFmpeg ticket. For an eligible non-zero transport-stream job it prepares the bounded seek calibration, then changes only the per-job `StreamState` media paths and protocols to a runtime-derived loopback gateway URL and binds the calibration to that exact URL.
7. `FfmpegCommandProcessor` accepts only the bound HTTP input plus `segment` muxer. It requires either the native delta contract or an indexed full-transcode contract proven by `copyts`, `start_at_zero`, disabled negative-timestamp rewriting, unshifted input/muxer time, and `segment_start_number × segment_time ≈ absolute seek`. It uses the initial plan or a PCR-corrected plan for each previously unseen later HLS target; a target outside the safe window receives one bounded retry. Validated source-and-target plans are shared across sessions, while each command remains bound to its exact loopback input. It atomically changes the protocol offset and seekability, clears input seek, adds an output-side sequential trim, restores the absolute output timestamp offset, and advances the first segment boundary by half the actual segment duration with a three-second cap. Emby's segment start number and subsequent cadence remain unchanged. A failed check restores every command field.
8. `GatewayService` validates the ticket, media-library scope, item visibility when authentication is present, and the unchanged STRM fingerprint.
9. `GatewayTransport` forwards approved request headers, follows up to the configured redirect limit and retains the real client User-Agent. It also provides bounded partial responses to `GatewayFastSeekProbeClient`. A keyed single-flight gate merges concurrent first resolutions, a ticket lease lets related Range requests reuse an effective address, and a source-scoped candidate lets only the server-FFmpeg media request try the address validated during calibration with its own User-Agent. Direct-client delivery does not consume the source candidate. Candidate reuse requires an exact matching 206 range and length and otherwise resolves again from the original source. When relay concurrency is greater than one, probes leave one configured slot available for playback delivery.
10. `TransportPlanner` selects a validated 302 for ordinary direct-client files, ordinary relay for server FFmpeg or forced relay, and HLS relay for Adaptive or RelayOnly delivery.
11. Ordinary relay streams the upstream response with HTTP status, Range metadata, request cancellation and a full-body idle timeout.
12. HLS recognition uses response metadata and a replayable content prefix. Relay bounds and parses each manifest, replaces URI lines and URI attributes with reusable child capability routes, and relays child manifests, segments, keys and initialization data.

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
Task<bool> FfmpegRunner.Start(FfmpegCommand, CancellationToken)
```

Patch ownership uses `Emby.StrmBridge.Playback.v4`. Startup verifies all five target methods and ownership. Shutdown removes all patches associated with that ID. An unsupported or changed ABI leaves playback in native mode while extraction remains available.

## Configuration UI localization

The generic configuration editor remains the source of all controls, values and save behavior. Emby's Generic UI service applies the request's `ClientLocale` to `CurrentUICulture` before it asks the plugin to build the page. `PluginConfiguration` uses the SDK's `DisplayNameL` and `DescriptionL` attributes with embedded `.resx` resources for English, Simplified Chinese and Traditional Chinese.

`PluginConfiguration.CreateEditContainer()` calls the official base implementation first, then resolves the same localization attributes again for the request-owned `EditorRoot` and editor items. This final request-local assignment avoids the process-wide cache used by .NET's `PropertyDescriptor.Description` while preserving every native editor type, value, validation rule and save action. Routing-mode enum values and all configuration values remain unchanged. Description resources use encoded selectable-text markup so native toggle, input and select containers behave consistently.

.NET execution context isolates the selected UI culture between concurrent requests, and each request owns its generated editor model. This path adds no localization Harmony patch, dashboard script, custom configuration page, public language endpoint, DOM observer, browser cache dependency or persistent UI state.

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
- fast-seek samples: maximum 3 for initial preparation and 2 for a later target, 512 KiB each, 5-second preparation deadline
- fast-seek plans: 512 prepared and 512 bound entries, 2 minutes, memory only
- HLS manifest: 2 MiB, 20000 lines, 10000 URIs, depth 8
- extraction concurrency: default 1, maximum 2
- extraction timeout: default 120 seconds, range 30–180

## Persistence

Playback tickets, upstream addresses, short-lived redirect leases, HLS child mappings, fast-seek plans and active response objects stay in memory. Media-information persistence remains below Emby's plugin configuration directory:

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
- [Remote transport-stream fast seek design](FAST_SEEK_DESIGN.md)
- [Security](SECURITY.md)

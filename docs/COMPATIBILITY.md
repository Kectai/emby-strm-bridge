# Compatibility

## Optional embedded text subtitles

Version 0.2.5 adds opt-in MKV text subtitles for exact verified **4.9.5.0 and 4.10.0.40 Web resources**. Only Web playback that the host routes through hls.js is eligible; native-HLS-only playback retains host subtitle handling. Eligible patched Web clients negotiate a shared server-side TS HLS video/subtitle input, using declared codec capabilities and host permissions instead of browser-name rules. Qualifying Web playback now carries video through Emby; third-party client routing is unchanged. Unsupported sources remain native. Actual-host and synthetic browser checks are complemented by recorded Safari, Chrome and IINA live acceptance; they do not cover every media/font combination. See [setup and limits](SUBTITLES.md). Version 0.2.6 also corrects external ASS/SSA timing in eligible TS HLS Web playback while preserving host subtitle and font loading; the same source and verified-resource boundaries apply.

## Supported baseline

| Component | Target and verification boundary |
| --- | --- |
| Plugin framework | `netstandard2.1` |
| Compile-time SDK | `MediaBrowser.Server.Core 4.9.1.80` |
| Playback ABI | Emby Server media-encoding assembly `4.9.5.x` or `4.10.0.x` with revision ≥ 40; every required signature must match |
| Offline host checks | Actual Emby `4.9.5.0` and `4.10.0.40` assemblies and bundled `5.1-emby` FFmpeg |
| Patch runtime | Reuse one compatible loaded Harmony runtime; otherwise use the embedded `Lib.Harmony 2.4.2` fallback |

The 4.9 baseline remains 4.9.5.x; older 4.9 releases, early 4.10 previews, and other release lines are not admitted by this gate. Later revisions in the admitted lines still require structural validation and deployment checks.

Six methods cover PlaybackInfo GET/POST, standard static video, FFmpeg job preparation, command execution and managed-output cleanup. An unsupported version or incompatible signature leaves playback native. Extraction and maintenance are separate from this playback gate, but are not thereby certified on untested Emby versions. Exact integration contracts are in the [design](STRM_BRIDGE_DESIGN.md); release evidence and pending live checks are in [TESTING.md](TESTING.md#release-readiness).

## Media and client scope

Extraction supports eligible audio and video STRM in selected libraries. Actual Audio items are probed as audio even when their URLs have no extension; other items use Emby's public MIME classification. Audio playback retains native behavior.

Video routing requires an exact item/media-source match and an unchanged local STRM source. It supports clients consuming `DirectStreamUrl`, standard video requests with `Static=true`, and eligible Emby server-side FFmpeg jobs. Clients that use the original source path directly may bypass these entry points. Local media, unselected libraries, unmatched versions, dynamic sources and sources requiring extra upstream headers remain native.

Client routes use the existing Emby origin and URL Base. Server-side FFmpeg and extraction use Emby's reported loopback API origin. The configuration UI uses the public Generic UI lifecycle and embedded language resources; it does not modify Emby Web files.

## Source behavior

### Client-generated seek thumbnails

Some clients open extra media ranges to generate seek thumbnails/live previews. When a source limits concurrent reads or request rate, these requests can compete with playback and cause prolonged buffering, stalled seeking or rejected reads. If affected, try disabling seek thumbnails/live previews. This concerns previews generated from video data, not ordinary poster or artwork requests.

In an observed case, CDN captures contained HTTP 403 responses alongside successful range reads, and disabling thumbnails restored playback. This supports the workaround for that scenario, without establishing a universal connection limit or a shared cause for similar symptoms in other clients.

After redirect handoff, the plugin cannot observe downstream errors or schedule the reader's thumbnail and playback requests. `RelayOnly` exposes server-side reads and applies transport capacity protection, but has no thumbnail-versus-playback priority scheduler. Lower concurrency can produce 503 responses instead of restoring playback; switching modes is not a verified substitute for disabling previews.

### Redirects, caching, and one-use URLs

For known ordinary files, Adaptive DirectClient routing validates the first authoritative redirect and returns 302 without opening its target. The ticket-scoped first-hop cache defaults to 20 seconds, accepts 0–60 and is bounded by remaining ticket/route lifetimes. It is a configurable performance tradeoff: the plugin cannot infer one-use, Range-bound or shorter-lived signature semantics. Set it to 0 for those sources. Client cache-bypass directives force a fresh resolution.

Adaptive may follow and read an unknown source to classify it before obtaining a fresh first hop for file delivery. Server-FFmpeg ordinary-file handoff validates the target before issuing a loopback-only 307, cached for at most 20 seconds and bounded by ticket/source-lease expiry. These paths require URLs that tolerate validation reads. Use `RelayOnly` when the final target cannot be reopened or media reads must pass through Emby, accepting server bandwidth use and capacity limits.

After handoff, later redirects, DNS, CDN responses and retries belong to the reader. Plugin source backoff applies only to failures its own transport observes. A stable redirect reduces source resolution work; it does not enforce downstream connection limits.

### HLS and Range

Adaptive rewrites complete HLS manifests and routes referenced resources through scoped tickets. `RedirectOnly` skips manifest rewriting. Live windows can change ETag, length and resources; static-file representation checks are separate. Rewritten manifests return 200 with their own length and omit origin range/validator metadata.

Manifest limits are 2 MiB, 20,000 lines, 10,000 URIs and depth 8. Resource/reference quotas also apply across sessions; growing EVENT playlists and large VOD lists can reach them. Capacity pressure returns a retryable 503. See the [design](STRM_BRIDGE_DESIGN.md) for retirement and quota contracts.

Server reads validate single-range 206 responses, including suffix ranges. Valid full 200, conditional 304 and unsatisfied 416 responses retain their HTTP meaning. A 200 is never fabricated into a 206. Except in RedirectOnly, body responses that ignore identity encoding are rejected before classification; HEAD remains bodyless and preserves Content-Encoding.

### Server-side fast positioning

Fast positioning is enabled by default but applies only to eligible TS/M2TS jobs with packet, clock and random-access evidence, matching strong ETag/length and a controlled HTTP request profile. Missing validators, custom headers/User-Agent or insufficient evidence retain native positioning. The feature can be disabled without disabling gateway routing.

Preparation has bounded time/bytes before Emby's FFmpeg startup window. If optimized startup returns false after process shutdown, the plugin can restore the original command and retry once within the original budget. Faulted tasks do not start a second process. Correctness must be checked against actual playback time/frames; successful startup alone is insufficient.

## Network and other playback patches

Gateway transport follows .NET's default system/environment proxy and bypass selection. Direct connections validate and pin resolved addresses; named private services require IP/CIDR approval. A selected proxy controls destination DNS and egress. Proxy failure terminates the request. See [security](SECURITY.md) for these distinct trust boundaries.

<a id="other-playback-patches"></a>

Harmony coexistence has two constraints: startup must select a single compatible runtime, and other plugins must not short-circuit the same standard video-stream method. Multiple active runtimes, or multiple inactive candidates without a unique active implementation, fail closed. A numeric prefix priority does not override Harmony ordering constraints. Health/Diagnostics reports current prefix metadata; `NativePrefixUncontended=true` establishes that no competing prefix was present at inspection time.

Disable competing video or audio STRM direct-redirect features that patch that shared method, restart Emby, and verify the loaded patch and actual route. Metadata-only features may remain enabled.

Extraction requires evidence that the current loopback probe input was opened. If Emby's probe returns or fails without opening it, STRM Bridge attempts an independent process using the configured ffprobe within the original budget. Repeated unavailable/unopened fallback or local result failures stop later remote probes for that run after three consecutive failures. Local completeness checks and snapshot recovery continue; the next run can try again. The plugin does not enter another extension's private probe scope.

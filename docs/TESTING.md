# Testing

## Automated verification

```sh
./scripts/verify.sh
```

All mutable test state uses repository-local paths:

- `.local/dotnet-home`
- `.local/nuget/packages`
- `.local/nuget/http-cache`
- `.local/build`
- `.local/test-work`
- `.local/test-results`

Coverage includes:

- strict STRM input and HMAC source identities
- media-information extraction, replacement, recovery and clearing
- configuration normalization, media-library selection, exact hosts, CIDR and subdomain rules
- PlaybackInfo result processing independent of host patch attachment
- standard static-video routing for exact STRM media-source matches
- per-job FFmpeg input routing for exact STRM media-source matches
- 188-, 192- and 204-byte transport framing, PCR selection and wrap handling
- adaptive two/three-sample initial calibration, one/two-sample target correction, native and indexed transcode timelines, plan expiry, exact job binding and atomic FFmpeg command mutation
- compatible cross-session plan sharing, independent waiter cancellation and playback-slot reservation from probes
- native fallback for dynamic, non-STRM, unmatched and out-of-scope video requests
- media-source count, order, ID and metadata preservation
- server-relative route construction with Emby path prefixes
- random ticket scope, direct-client/server-FFmpeg purpose, user binding, capability access, lifetime and capacity
- multi-hop relative redirects
- client User-Agent and Range forwarding
- redirect-lease reuse across Range requests, expiry, clearing and invalid-target fallback
- one bounded source re-resolution for a rejected fresh redirect target, with no retry for direct sources
- Adaptive client redirect, server-FFmpeg/HLS relay, RelayOnly transport and relay concurrency
- HLS signature detection, replayable prefix handling, line and URI-attribute rewriting
- host detection, administrator notification and automatic retry
- privacy-safe logging and persistence
- native Generic UI localization metadata and embedded-resource completeness
- repeated Simplified Chinese, English and Traditional Chinese editor generation despite `PropertyDescriptor` description caching
- concurrent request-local editor generation in different UI cultures
- selectable descriptions, unchanged form values and stable routing-mode identifiers
- embedded dependency identity verification, allowlisted package contents and DLL byte verification

## Package verification

```sh
./scripts/package.sh
```

Packaging verifies:

- complete Release build
- one self-contained `Emby.StrmBridge.dll` with the expected embedded Harmony identity
- exact archive entry allowlist
- ZIP integrity
- current package timestamps and absent extra fields
- byte identity between built and archived DLLs
- release-document vendor-term scan

## Emby 4.9.5.0 host matrix

1. Confirm `STRM_BRIDGE_PATCH_READY` reports the installed 4.9.5.x ABI and five targets.
2. Confirm GET and POST PlaybackInfo both retain source count, order and IDs.
3. Confirm a matching source receives a relative `/StrmBridge/Playback/v2/` URL.
4. Confirm Emby Web direct play reaches the gateway.
5. Confirm an external player launched through Emby reaches the same gateway URL.
6. Exercise a direct-body source with `200` and `206`.
7. Exercise one-hop and multi-hop redirects.
8. Confirm `Adaptive` returns a validated 302 for an ordinary client-direct file, relays a server-FFmpeg file, and rewrites and relays HLS. Confirm `RelayOnly` retains server relay for a source that cannot be consumed from the client network context.
9. Exercise `HEAD`, initial playback, repeated seek, reconnect and resume.
10. Exercise HLS master playlist, media playlist, audio, subtitle, key, map and segment resources.
11. Repeat through HTTPS and an Emby API path prefix.
12. Request `/Videos/{id}/stream` with the exact media-source ID and `Static=true`; confirm the matching STRM reaches the gateway.
13. Repeat with a local file, a different media-source ID and `Static=false`; confirm each remains on Emby's native path.
14. Force an exact STRM source through HLS transcoding; confirm `STRM_BRIDGE_TRANSCODE_INPUT_ROUTED` appears before the gateway event and FFmpeg opens the loopback gateway input.
15. Repeat transcoding with a local file, a different media-source ID and an out-of-scope library; confirm each keeps the native FFmpeg input.
16. Resume an eligible remote TS/M2TS after ten seconds, then seek to multiple non-zero positions through both remux and full-transcode playback. Confirm the initial `STRM_BRIDGE_FAST_SEEK_READY` reports two probes for a bounded estimate or three after correction, later targets report one probe or two after the bounded retry, and `STRM_BRIDGE_FAST_SEEK_APPLIED` reports `timeline=native` or `timeline=indexed` on every eligible FFmpeg job. Confirm the formal media request reuses a compatible candidate without changing its User-Agent, no input-side `-ss` or timestamp-seek failure occurs, the output timestamp offset equals each target, the first segment closes within half a segment plus keyframe alignment, subsequent segments keep the native cadence, no tiny catch-up burst occurs, each job has one continuous media-body Range, and playback positions are correct. Repeating the same source and target in another session must reuse the compatible plan without another probe.
17. Repeat from zero, with a non-TS source, and with an invalid PCR sample; confirm native command behavior.
18. Switch to `Native` and confirm the original PlaybackInfo, standard-video and FFmpeg input paths remain unchanged.
19. Stop Emby and confirm the next start reports one clean patch installation.
20. Run concurrent different-title and same-title playbacks. Cancel one waiter during shared preparation and confirm the other completes; confirm probes do not consume the reserved playback slot.
21. Audit plugin, Emby and reverse-proxy logs for source URLs, query signatures and tickets.
22. Open STRM Bridge settings, switch Emby Web between English, Simplified Chinese and Traditional Chinese, and confirm the next native Generic UI request renders the matching titles, labels, descriptions and trusted-host suffixes without changing selected libraries, playback mode or other values. Confirm all descriptions can be selected and copied while the four routing-mode identifiers stay unchanged.

Use synthetic names and credentials for fixtures and reports.

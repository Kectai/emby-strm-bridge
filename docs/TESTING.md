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
- native fallback for dynamic, non-STRM, unmatched and out-of-scope video requests
- media-source count, order, ID and metadata preservation
- server-relative route construction with Emby path prefixes
- random ticket scope, user binding, capability access, lifetime and capacity
- multi-hop relative redirects
- client User-Agent and Range forwarding
- redirect-lease reuse across Range requests, expiry, clearing and invalid-target fallback
- one bounded source re-resolution for a rejected fresh redirect target, with no retry for direct sources
- adaptive transport selection and relay concurrency
- HLS signature detection, replayable prefix handling, line and URI-attribute rewriting
- host detection, administrator notification and automatic retry
- privacy-safe logging and persistence
- embedded dependency identity verification, deterministic package contents and DLL byte verification

## Package verification

```sh
./scripts/package.sh
```

Packaging verifies:

- complete Release build
- one self-contained `Emby.StrmBridge.dll` with the expected embedded Harmony identity
- exact archive entry allowlist
- ZIP integrity
- normalized timestamps and absent extra fields
- byte identity between built and archived DLLs
- release-document vendor-term scan

## Emby 4.9.5.0 host matrix

1. Confirm `STRM_BRIDGE_PATCH_READY` reports the installed 4.9.5.x ABI and four targets.
2. Confirm GET and POST PlaybackInfo both retain source count, order and IDs.
3. Confirm a matching source receives a relative `/StrmBridge/Playback/v2/` URL.
4. Confirm Emby Web direct play reaches the gateway.
5. Confirm an external player launched through Emby reaches the same gateway URL.
6. Exercise a direct-body source with `200` and `206`.
7. Exercise one-hop and multi-hop redirects.
8. Exercise a User-Agent-bound final address in `Adaptive` mode.
9. Exercise `HEAD`, initial playback, repeated seek, reconnect and resume.
10. Exercise HLS master playlist, media playlist, audio, subtitle, key, map and segment resources.
11. Repeat through HTTPS and an Emby API path prefix.
12. Request `/Videos/{id}/stream` with the exact media-source ID and `Static=true`; confirm the matching STRM reaches the gateway.
13. Repeat with a local file, a different media-source ID and `Static=false`; confirm each remains on Emby's native path.
14. Force an exact STRM source through HLS transcoding; confirm `STRM_BRIDGE_TRANSCODE_INPUT_ROUTED` appears before the gateway event and FFmpeg opens the loopback gateway input.
15. Repeat transcoding with a local file, a different media-source ID and an out-of-scope library; confirm each keeps the native FFmpeg input.
16. Switch to `Native` and confirm the original PlaybackInfo, standard-video and FFmpeg input paths remain unchanged.
17. Stop Emby and confirm the next start reports one clean patch installation.
18. Audit plugin, Emby and reverse-proxy logs for source URLs, query signatures and tickets.

Use synthetic names and credentials for fixtures and reports.

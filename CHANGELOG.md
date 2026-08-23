# Changelog

## 0.2.2

- Adds Adaptive routing for direct playback and server-side media processing.
- Adds fast positioning for remote TS and M2TS media.
- Improves direct client delivery, repeated seeking, reconnects and playback recovery.
- Isolates simultaneous playback across users, devices and sessions.
- Improves HLS playback continuity and gateway compatibility.
- Makes configuration labels and descriptions follow the active Emby Web language without changing setting values.

## 0.2.1

- Processes version-gated GET and POST PlaybackInfo responses on Emby Server 4.9.5.x while preserving media-source count, order, IDs and metadata.
- Routes exact, in-scope STRM matches from Emby's standard static-video GET/HEAD path through the same gateway while retaining native handling for every other video request.
- Routes the input of exact, in-scope STRM FFmpeg jobs through a runtime-derived local gateway address, including large transport-stream sources, without a configured or hard-coded host and port.
- Routes matching STRM sources through current-Origin relative gateway URLs with `Native`, `RedirectOnly`, `Adaptive` and `RelayOnly` modes.
- Embeds and identity-checks the Harmony runtime so installation uses one plugin DLL.
- Supports validated multi-hop redirects, GET/HEAD and Range forwarding, bounded relay concurrency, request cancellation and full-response idle timeouts.
- Reuses short-lived validated redirect targets across related byte requests with keyed single flight, method separation, generation-safe invalidation and automatic source fallback.
- Re-resolves once from the STRM source when a fresh redirected target returns 401, 403, 404 or 410, while keeping direct-source failures and the second result bounded.
- Relays HLS manifests and resources with MIME, path and content-signature detection, bounded rewriting and reusable child tickets.
- Uses memory-only, high-entropy playback and HLS tickets with optional user binding, runtime generations, expiry and independent capacity limits.
- Supports exact host, IP, CIDR and label-bounded subdomain trust rules, detected-host review and bounded retry after trust changes.
- Extracts and restores bounded video, audio and subtitle technical information while preserving external streams and excluding image and attachment streams.
- Provides scheduled extraction plus administrator extraction, restore, cleanup and clear operations.
- Keeps URLs, paths, query values, credentials, headers, tickets, titles, library names and user names outside plugin logs and technical snapshots.
- Packages current documentation, MIT licensing and the bundled Harmony license notice with exact archive validation.

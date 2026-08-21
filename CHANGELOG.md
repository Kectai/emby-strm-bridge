# Changelog

## Unreleased

- Refined the managed STRM architecture after comparing static STRM generation, stable redirect services, reverse-proxy interception, and body-proxy approaches; mirror mode is now the only first-release write path and in-place takeover is deferred.
- Added the M5.0 memory-only managed STRM feasibility rail: administrator-confirmed bounded registration, 256-bit one-hour capabilities, optional allowlisted container hints, GET/HEAD redirect resolution, direct 200/206 rejection, lifecycle invalidation, localized errors, and privacy-focused tests. It does not scan or modify media-library files.

## 0.1.0

- Initial implementation of strict local STRM policy and HMAC identities.
- Added public-API media probing, scheduled/scan-completion extraction, technical-field persistence, restoration, and extraction backoff.
- Added opt-in alternate media source, random tickets, first-hop redirect resolver, 30-second leases, single flight, and source rate/failure controls.
- Added administrator maintenance endpoints, localized native Emby configuration, cleanup, privacy checks, deterministic packaging, and automated tests.
- Switched extension registration to Emby automatic discovery and repository updates to `ILibraryManager.UpdateItems` without external metadata saves.
- Made scan completion enqueue bounded background work, added direct-media probing, source-change detection, same-source probe sharing, and external-stream preservation with in-memory rollback.
- Added operation-generation cancellation and shutdown draining, authenticated user-bound gateway tickets, exact-host redirect trust, global resolver state bounds, strict User-Agent rejection, and stronger key/STRM file handling.
- Added state backup recovery, bounded lock storage, orphan cleanup, NuGet auditing, hardcoding/privacy guards, and exact release-ZIP verification.
- Added host-catalog-backed media-library selection with canonical GUID validation on save.
- Added locked dependency graphs, vulnerability warnings as errors, package validation in CI, and tag-gated GitHub Release publishing.
- Added bounded probe-result sharing, constant-time extraction-state indexing with cancellation-safe batch flushes, bounded snapshot serialization, authorized reconnect tickets, and expanded loopback HTTP failure tests.
- Fixed the native configuration page so redirect hosts use a multiline editor and media-library choices come from Emby's virtual-folder catalog.
- Added a bounded in-memory list of cross-host redirect targets detected during extraction so administrators can approve exact hosts directly from the configuration page without inspecting requests or exposing full URLs.
- Restored Emby's native media-library and detected-host multi-select controls, made library enumeration fall back independently across both public host catalogs, and added a bounded backoff bypass so redirect-host detection can refresh after restart.
- Rehydrates native option lists immediately after saving, keeps unselected detected-host candidates for the current server session, and always shows an actionable empty-state hint for redirect-host detection.
- Moves redirect-detection guidance outside the native multi-select and changes an empty media-library selection to disable all processing while keeping cleanup safe across all libraries.
- Replaces the conflicting fixed 15-second first-hop timeout with the live 30–180 second per-item setting and adds privacy-safe extraction failure reason codes.
- Classifies untrusted redirect targets as awaiting administrator approval, sends a deduplicated counts-only native warning, retains and marks detected hosts after saving, preserves manual exact-host entry, and automatically retries waiting media when new trust is saved.
- Enables playback bridging by default, migrates the earlier disabled default once while preserving later administrator choices, clarifies extraction-only behavior, and adds privacy-safe provider request/issue/skip diagnostics plus direct media-source tests.
- Fixes server-side playback paths so local consumers receive an absolute loopback gateway URL instead of treating a relative route as a missing file; direct loopback redemption remains ticket-scoped and rejects forwarded requests.
- Marks the ticket-protected gateway route as host-unauthenticated so direct clients and Emby's loopback FFmpeg reach the plugin's own ticket and authorization checks instead of being rejected by the host authentication filter.
- Persists probed video and audio streams through Emby's public media-stream repository with compensating rollback, and records pending-host warnings in the administrator dashboard even when no external notification service is configured.
- Adds explicit `*.example.com` trust rules for changing CDN subdomains with strict DNS-label boundaries, while keeping exact hosts and IP literals unchanged.
- Preserves external streams by reading Emby's media-stream repository before replacing probed internal streams, including when the in-memory item is not hydrated.
- Makes disabled only-missing mode perform a fresh probe for every selected STRM instead of restoring a prior snapshot or honoring stale failure state.
- Adds a manual native scheduled task and administrator endpoint to clear stored STRM Bridge technical information for selected libraries while retaining external streams and media files.
- Prevents repository index collisions by safely reindexing retained external streams, keeps selected subtitle/audio indexes aligned, and excludes embedded cover images and attachment streams from persisted technical media information.
- Keeps the bounded host-only redirect detection catalog hydrated after native configuration saves and server restarts, including trusted markers, without persisting URLs, ports, paths, query strings, or credentials.
- Moves administrator-facing text to culture resources with English fallback, Simplified Chinese, and Traditional Chinese coverage for configuration, validation, scheduled tasks, notifications, and plugin descriptions.

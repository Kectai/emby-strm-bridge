# Changelog

## 0.2.4 — 2026-09-10

- Support the Emby 4.10.0 stable line from revision 40 while retaining 4.9.5.x compatibility.
- Keep strict patch signatures, component version consistency and rollback checks; reject unverified release lines and early previews.
- Add host-version boundary regressions and an isolated actual-host installation, disposal and snapshot check.

## 0.2.3 — 2026-09-09

- Add validated first-hop redirects and ticket/profile-scoped caching, configurable from 0–60 seconds, with explicit refresh support.
- Improve standard static-video routing, exact source matching, Harmony runtime coexistence and loaded-build diagnostics.
- Bind TS/M2TS positioning to strong validators and actual request profiles; add bounded native recovery, adaptive probe budgets and an independent feature switch.
- Correct HLS response metadata, dynamic resource retirement and multi-session quotas; remove per-resource global cleanup from large manifests.
- Fix audio STRM extraction and require current input-open evidence; add independent ffprobe fallback and bounded failure handling.
- Upgrade recovery snapshots to schema 3, preserving HDR, rotation and negotiation fields; retain existing external streams during technical updates.
- Improve Range validation, redirect expiry, shared control deadlines, source backoff, cancellation and transcode resource cleanup.
- Consolidate documentation and document the generic seek-thumbnail workaround and relay limitations.
- Add package checksums, immutable-release checks and regression coverage totaling 561 tests.

## 0.2.2

- Add Adaptive routing and remote TS/M2TS fast positioning.
- Improve direct delivery, repeated seeking, playback recovery and session isolation.
- Improve HLS continuity and configuration localization.

## 0.2.1

- Add version-gated PlaybackInfo, static-video and server-FFmpeg integration for Emby 4.9.5.x.
- Add relative ticket routes, four playback modes, validated redirects, Range forwarding and HLS rewriting.
- Add bounded relay, cancellation, redirect reuse and source recovery.
- Add selected-library technical extraction, recovery snapshots, scheduled tasks and administrator maintenance APIs.
- Embed the Harmony runtime and package the single DLL with documentation, licenses and privacy checks.

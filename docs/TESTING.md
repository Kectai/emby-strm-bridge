# Testing and release acceptance

## Maintained checks

Use the .NET SDK pinned by `global.json`, plus Git, ripgrep and zip/unzip. From the repository root:

```sh
./scripts/verify.sh
```

This restores and builds Release, verifies formatting, runs the maintained test suite, checks privacy rules and checks whitespace errors. Mutable build state and test results stay in `.local/`.

To validate and create the release archive in one step:

```sh
./scripts/package.sh
```

Packaging runs the same verification first, then checks the archive contents and produces a SHA-256 file in `artifacts/`. There is no need to run both commands consecutively for the same unchanged tree. The archive contains the single plugin DLL, public documentation and required licenses; local diagnostic evidence and build caches are excluded.

The maintained test project is `tests/Emby.StrmBridge.Tests/Emby.StrmBridge.Tests.csproj`. Its .NET test runtime is separate from actual-host validation; passing it does not certify the host's ABI or player behavior.

| Area | Coverage |
| --- | --- |
| Scope and source policy | Library selection, exact media-source matching, regular STRM input, URI/trust rules and source changes |
| Extraction and persistence | Audio/video completeness, mandatory fresh-input evidence, independent fallback, cancellation, external-stream preservation, schema 3 round trips and corrupted backups |
| HTTP gateway | First-hop redirects, request-profile cache isolation, expiry, Range/validator checks, one authoritative refresh, source backoff and returned-stream cancellation |
| HLS | Complete-manifest HTTP semantics, nested resources, live-window retirement, VOD retention, atomic rollback, quotas and large-list cleanup cost |
| Fast positioning | Packet/clock evidence, video selection, strong representation guards, dynamic budgets, shared preparation, command mutation and native recovery |
| Host lifecycle | Harmony runtime selection, ABI/patch ownership, repeated jobs, startup cooldown and delayed cleanup fencing |
| Network/privacy/package | Pinned DNS, trust revocation, proxy selection, header boundaries, bounded state and archive allowlist |

Add tests for changed contracts at their integration boundary. In particular, large HLS rewrites must not scan all tickets per URI; range validation must cover first authoritative reads as well as cache hits; a fresh probe must not succeed solely because the host returned preexisting fields.

## Evidence for 0.2.3

The maintained suite has **561 passing tests**. The release has also passed Release build, formatting, privacy and packaging checks. These results describe automated coverage, not completed live acceptance.

Offline checks used actual Emby `4.9.5.0` assemblies, .NET `6.0.36` and the host's bundled `5.1-emby` FFmpeg. Record the loaded DLL fingerprint alongside each offline run; distributed ZIP bytes are verified by the release checksum file.

| Offline check | Recorded result |
| --- | --- |
| Technical snapshot round trip | HDR/Dolby Vision and rotation retained |
| Stable representation, target 40 seconds | First frame at 40.0417 seconds; 479 continuous matching frames |
| Representation rejection / native recovery | First frame at 40.0 seconds; 480 continuous matching frames |
| 10,000-URI HLS ticket workload | Initial rewrite 32 ms; refresh 13 ms, versus 7,933/22,005 ms before the cleanup fix |

The HLS measurement covers local ticket bookkeeping, not end-to-end media or network latency. Offline evidence must be rerun when relevant code or the DLL changes. The HLS timings are retained from the prerelease regression of the same implementation; timing values vary by machine and run.

## Release readiness

<a id="implementation-gaps"></a>
<a id="release-readiness-20260906"></a>

**Release channel: stable (0.2.3).** Automated and actual-host offline checks cover the release implementation. A complete live matrix across supported players and sources has not been recorded; stable-channel publication does not expand that verification coverage. Run the applicable deployment checks below with the installed release.

| Live scenario | Required observation |
| --- | --- |
| Install, upgrade and patch coexistence | Health version/BuildId matches the package; six patches install; no competing standard-route prefix; settings and recovery data survive the intended upgrade |
| Scope and extraction | Selected audio/video STRM extract correctly; complete items skip; force refresh reads current input; restore/clear/orphan cleanup have their documented scope; unrelated media remains native |
| First open, reopen and repeated seeking | Web and representative third-party clients start, reopen and perform third/later seeks with correct time, frame and audio sync; long playback continues |
| Direct and relay delivery | Identify the actual media reader and bytes through Emby; verify first-hop cache/bypass behavior and source rejection, expiry, concurrency and cancellation |
| HLS | VOD seeking and long-running live/event refresh work through nested playlists, keys, maps and subtitles; capacity errors are retryable without disrupting another session |
| Server-side processing | Remux/transcode preserve target time and selected streams; unsupported profiles use native positioning; representation changes and failed optimized startup recover without stale output cleanup |
| Network and lifecycle | URL Base, loopback, direct/proxy routing, trust changes, shutdown and overlapping sessions preserve authorization, release resources and avoid stale commits |
| Thumbnail workaround | If source-limited preview reads cause stalls, disable client seek thumbnails/live previews and retest; record the setting as an acceptance condition |

Record the loaded DLL identity, host/player versions, source capabilities, mode/settings and actual result for each applicable scenario. Keep private captures and signed URLs out of public reports. Compare source reads, redirect-target reads and Emby media bytes separately; HTTP 206 or a successful startup event alone does not prove correct seeking or uninterrupted playback.

## Documentation maintenance

Keep user setup in [INSTALL.md](INSTALL.md), supported behavior and workarounds in [COMPATIBILITY.md](COMPATIBILITY.md), security boundaries in [SECURITY.md](SECURITY.md), and implementation contracts in [STRM_BRIDGE_DESIGN.md](STRM_BRIDGE_DESIGN.md). Update evidence when the tested binary changes. Historical review notes and raw captures are not release documentation.

# Emby.StrmBridge

`Emby.StrmBridge` is a lightweight Emby Server plugin for local `.strm` files whose effective content is exactly one HTTP(S) URL. It extracts missing technical media information, stores a URL-free recovery snapshot, and exposes a playback bridge backed by short-lived HTTP redirects.

The plugin never proxies media bodies. Its gateway reads only the first source response headers with `Range: bytes=0-0`, returns an approved redirect target as HTTP 302, and leaves media transfer to the playback consumer. A redirect to a different host is rejected unless an administrator has added that exact host or an explicit `*.example.com` subdomain rule to **Allowed redirect target hosts**. Direct-media STRM sources continue to use the original media source and are not offered a bridge candidate, so their durable source URL is not disclosed through the gateway.

## Current status

- Builds against `MediaBrowser.Server.Core 4.9.1.80` and targets `netstandard2.1`.
- Implements scheduled extraction, bounded scan-completion queuing, direct-media and redirect-based probing, safe persistence/recovery, maintenance APIs, alternate media-source discovery, authorized playback tickets, redirect leases, source-level limiting, and cleanup.
- STRM Bridge playback is enabled by default. Configuration schema version 1 performs a one-time migration of earlier configurations that inherited the old disabled default; administrators can still disable playback independently for extraction-only use.
- Provider selection remains host-dependent until the M0 matrix is completed on the installed server release. Privacy-safe `REQUESTED`, `ISSUED`, and fixed-reason `SKIPPED` events distinguish provider discovery from gateway failures without logging paths, URLs, tickets, or client names.
- The first managed-STRM feasibility rail is implemented but not production-enabled: an administrator can explicitly create up to eight one-hour, memory-only synthetic records for M5.0 host testing. It does not scan, generate, mirror, or replace media-library files, and every record disappears on configuration invalidation, explicit clearing, restart, or shutdown.
- No private hooks, reflection patches, direct database writes, media proxying, vendor APIs, fixed remote endpoints, or vendor-specific production logic are used.

## Build and test

```sh
./scripts/verify.sh
```

All NuGet packages, CLI state, HTTP cache, build outputs, test files, and test results are written below this repository's `.local/` directory. Release packages are written to `artifacts/`.

```sh
./scripts/package.sh
```

See [architecture](docs/ARCHITECTURE.md), [installation](docs/INSTALL.md), [security](docs/SECURITY.md), [compatibility](docs/COMPATIBILITY.md), and [testing](docs/TESTING.md).

## Operations

After installation, Emby exposes the native scheduled tasks **Extract missing STRM media information** and **Clear stored STRM Bridge media information**. The clear task has no automatic trigger: it must be started manually, operates only on selected libraries, removes STRM Bridge snapshots/extraction state and internally probed technical streams, and retains external streams and media files. Administrator-only maintenance endpoints are also available:

- `POST /StrmBridge/Maintenance/Extract` (`Force`, `ItemId`, and `LibraryId` are optional)
- `POST /StrmBridge/Maintenance/Restore`
- `POST /StrmBridge/Maintenance/Cleanup`
- `POST /StrmBridge/Maintenance/Clear`

The experimental M5.0 feasibility API is intentionally separate from normal settings:

- `POST /StrmBridge/Managed/Prototype` accepts one bounded `SourceUrl`, an optional allowlisted `ContainerHint`, and `ConfirmExperimental=true`; it returns only a relative managed path and expiry time.
- `DELETE /StrmBridge/Managed/Prototype` clears every memory-only prototype record.

Both management operations require either an authenticated administrator session or an Emby server API key, which is an administrator-created integration credential. Non-administrator user sessions remain rejected. The returned managed path is a one-hour bearer capability intended only for a synthetic test library; do not publish it or use it for a production library. The managed GET/HEAD route accepts only supported redirects and never returns the original source for a direct 200/206 response. See [the managed STRM design](docs/MANAGED_STRM_DESIGN.md) for the M5.0 host gate and the mirror-first implementation order.

Configuration is rendered through Emby's native `BasePluginSimpleUI` editor, including native multi-select controls for media libraries and detected redirect hosts. Leaving the media-library selection empty disables extraction, restoration, and alternate playback for every library; select at least one library to activate processing. Cleanup still enumerates all STRM files so an empty selection cannot erase valid snapshots as orphans. The persisted configuration contains canonical library IDs rather than display names. Server-side validation constrains extraction concurrency to 1–2, per-item timeout to 30–180 seconds, and selected IDs to the current media-library catalog when it is available.

Administrator-facing configuration, validation, scheduled-task, notification, activity, and plugin-description text follows Emby's current UI culture. The package includes English, Simplified Chinese, and Traditional Chinese resources; unsupported locales fall back to English. Stable diagnostic event codes and API route identifiers remain language-neutral.

With **Only extract missing media information** enabled, unchanged complete items are skipped and a matching snapshot can restore missing information without a remote media probe. When playback bridging is enabled, a complete item may receive a headers-only first-hop check to establish or refresh its direct-versus-redirect classification; this does not invoke FFmpeg or overwrite technical fields. Disable only-missing mode when existing information may be stale or inaccurate: every selected STRM is freshly probed on the next extraction run, even when a snapshot or previous success exists, and the new result replaces the snapshot. Use the manual clear task when the old technical information itself must be removed before rebuilding it.

When a source redirects to a different hostname, enter one exact trusted target hostname (or IP literal) per line in **Allowed redirect target hosts**. For changing CDN hosts, an explicit rule such as `*.example.com` trusts every label-bounded subdomain but not the bare `example.com` host. Commas and semicolons are also accepted. Schemes, paths, ports, credentials, partial wildcards, and wildcard IPs are rejected. An empty field permits same-host redirects only.

The **Detected redirect hosts** control is always visible. When extraction encounters an untrusted cross-host redirect, Emby records one counts-only warning in the administrator dashboard activity log; configured external notification services receive the same warning with a link to this settings page. Detected hosts remain listed after saving and across server restarts, and detections covered by either exact or explicit subdomain trust remain visible with a trusted marker. Select only hosts you trust and save: the plugin clears the relevant backoff and automatically queues a bounded background retry, so the scheduled task does not need to be started a second time. Exact hosts and explicit subdomain rules may still be entered or removed manually in **Additional trusted redirect hosts**. The bounded catalog persists only normalized exact hostnames in the administrator-only plugin configuration and never contains schemes, ports, URL paths, or query strings. Redirects that visibly copy a long source-query value into a hostname are rejected before detection; opaque hostnames remain part of the administrator's upstream-DNS trust boundary. A newly observed hostname rearms the warning without repeating it for an unchanged pending set.

## Scope

The plugin does not yet create or replace STRM files. It does not scrape metadata, call storage-provider APIs, inspect proprietary signing formats, alter FFmpeg arguments, transcode, generate thumbnails, launch external players, or report playback progress.

## License

MIT

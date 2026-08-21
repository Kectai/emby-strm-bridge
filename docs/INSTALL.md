# Install, update, and uninstall

## Build a package

Run from the repository root:

```sh
./scripts/package.sh
```

The script verifies the project and creates `artifacts/Emby.StrmBridge-<version>.zip`. It stages only the Release DLL, README, license, changelog, and documentation. Build caches and test results stay under `.local/` and are not packaged.

## Install

1. Stop Emby Server.
2. Extract `Emby.StrmBridge.dll` from the release ZIP into Emby's plugin directory.
3. Start Emby Server and open the STRM Bridge settings page.
4. Select at least one library under **Included media libraries**. An empty selection intentionally disables all library processing.
5. If a trusted source redirects to a different hostname, enter that exact hostname or IP literal in **Allowed redirect target hosts**, one entry per line. For changing CDN hosts, use an explicit rule such as `*.example.com`; it covers subdomains only, not the bare host. Do not enter a URL, partial wildcard, wildcard IP, port, or path. Alternatively, run extraction once and review the counts-only warning in the administrator dashboard activity log, then open STRM Bridge settings. Configured external notification services also receive a settings link. Select trusted hosts under **Detected redirect hosts** and save; the plugin automatically retries waiting media. Manual host and subdomain-rule entry remain available.
6. STRM Bridge playback is enabled by default. Disable it only when extraction-only operation is desired, and complete the M0 checks in [COMPATIBILITY.md](COMPATIBILITY.md) before relying on playback in production.
7. Run **Extract missing STRM media information** from Scheduled Tasks.

To rebuild stale or inaccurate technical information, turn off **Only extract missing media information** and run extraction; this bypasses matching snapshots and freshly probes every selected STRM. To remove stored information first, manually run **Clear stored STRM Bridge media information**. The clear task has no default schedule, affects selected libraries only, and retains external subtitle/other external-stream rows and all media files. If an older plugin build already removed external subtitle rows from Emby's database, run an Emby library scan once to rediscover the unchanged sidecar files before extracting again.

The Emby service account needs read access to STRM files and write access to its plugin configuration directory. It does not need write access to media libraries.

## Update

Stop Emby, replace the DLL, and restart. Snapshot schema version 1 remains compatible across 0.1.x builds. Back up Emby's plugin configuration directory before any future release that announces a schema migration.

## Roll back

Stop Emby, replace the DLL with the previous version, and restart. If the previous release does not support the current snapshot schema, move the `Emby.StrmBridge` configuration directory aside before starting it; do not copy snapshot JSON into media directories.

## Uninstall

Use Emby's plugin uninstall action, then restart. The plugin's uninstall hook removes its exact `Emby.StrmBridge` configuration subdirectory, including identity key, snapshots, state, backups, and matching temporary files. It does not edit STRM or other media files.

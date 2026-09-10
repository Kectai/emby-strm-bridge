# Install, configure, and maintain

## Package and installation

Build with `./scripts/package.sh` from the repository root; prerequisites and validation are described in [TESTING.md](TESTING.md). The ZIP and matching checksum file appear in `artifacts/`. Before installing, verify both downloaded files from their containing directory, replacing `<version>` with the package version:

```sh
shasum -a 256 -c Emby.StrmBridge-<version>.zip.sha256
```

1. Stop Emby Server. For an existing installation, back up the plugin DLL, configuration and recovery data.
2. Extract `Emby.StrmBridge.dll` into Emby's plugin directory, replacing the previous copy if present. The required fallback patch runtime is embedded; no companion DLL is needed.
3. Start Emby, open STRM Bridge settings and select the participating libraries. An empty selection processes nothing.
4. Keep `Adaptive` as the initial playback mode and configure trusted cross-host redirects. Review detected hosts before approving them.
5. Run **Extract missing STRM media information**, then verify playback and seeking as described below.

The Emby service account needs read access to STRM files and write access to the plugin configuration directory. The plugin does not require media-library write access. Eligible STRM files are regular local files of at most 16 KiB, containing one HTTP(S) URL in strict UTF-8. See [compatibility](COMPATIBILITY.md) for source and playback restrictions.

Disable other features that short-circuit the same standard video-stream method before using this plugin's playback routing. Restart after changing patch settings. Metadata-only features can remain enabled; see [patch coexistence](COMPATIBILITY.md#other-playback-patches).

## Configuration

| Setting | Default | Purpose |
| --- | --- | --- |
| Enabled | On | Enable processing within selected libraries |
| Playback mode | `Adaptive` | File redirects, HLS rewriting and relay as required; `Native` retains native playback |
| Only extract missing media information | On | Skip complete items, even if their STRM source changes; force extraction to refresh them |
| Save recovery snapshots | On | Save plugin recovery data; disabling this does not prevent technical updates to Emby |
| Extract after library scan | Off | Automatically run extraction after a scan |
| Extraction concurrency | 1 | Range 1–2 |
| Extraction timeout | 120 seconds | Range 30–180 |
| Gateway timeout | 120 seconds | Range 10–180; control deadline and separate body-idle timeout |
| Direct redirect cache lifetime | 20 seconds | Range 0–60; set 0 for one-use, Range-bound or shorter-lived signed addresses |
| Enable evidence-based fast positioning | On | Independently enable eligible remote TS/M2TS server-side positioning |
| Redirect hop limit | 5 | Range 1–8 |
| Relay concurrency | 4 | Range 1–16; excess transport requests receive 503, not a priority queue |

Cross-host trust accepts exact hosts, wildcard suffixes, IP addresses and CIDRs. A wildcard does not include the suffix's bare domain. Named LAN services also need explicit approval of their private IP/CIDR; a hostname rule alone is insufficient. Trust rules are a security boundary, not a playback tuning setting. Details are in [SECURITY.md](SECURITY.md).

Extraction supports audio and video STRM; audio playback remains native. Recovery restores technical fields, not scraped metadata or media files. For thumbnail-related buffering, use the [temporary workaround](COMPATIBILITY.md#client-generated-seek-thumbnails).

## Verify installation

Check the plugin log for these events; the ABI value reflects the installed host:

```text
STRM_BRIDGE_HARMONY_RUNTIME shared=<true|false> activeMethods=<count>
STRM_BRIDGE_PATCH_READY abi=4.10.0.40 targets=6
```

On Emby 4.9.5.0, the same ready event reports `abi=4.9.5.0`. Version 0.2.4 requires no configuration or snapshot migration when upgrading from 0.2.3.

`shared=true` indicates reuse of a compatible loaded patch runtime. A patch failure includes a stage and fixed reason code. Use administrator Health/Diagnostics to confirm `PluginVersion` and the 12-character `BuildId` identify the loaded DLL. Check `PrefixDiagnosticsAvailable`, `NativePrefixInstalled` and `NativePrefixUncontended`; numeric priority alone does not prove safe coexistence.

For an eligible video, PlaybackInfo should contain a relative `DirectStreamUrl` beginning with `/StrmBridge/Playback/v3/`, including the Emby URL Base prefix when configured. Useful transport events are:

| Event suffix (after `STRM_BRIDGE_`) | Meaning |
| --- | --- |
| `GATEWAY_SOURCE_REDIRECT` | Resolve a first-hop redirect from the authoritative source |
| `GATEWAY_DIRECT_ROUTE_HIT` | Reuse an unopened, ticket-scoped first-hop address |
| `GATEWAY_DIRECT_ROUTE_REFRESH` | Refresh the address after a client cache-bypass directive |
| `GATEWAY_REDIRECT_LEASE_HIT` | Open and validate a cached target on a server-read path |
| `GATEWAY_RELAY` / `GATEWAY_HLS` | Relay media or deliver a rewritten manifest |
| `GATEWAY_SOURCE_BACKOFF` | Apply backoff to an upstream failure the plugin observed |

A gateway request or redirect-cache hit alone does not establish which process reads the media. Verify first and second opens, repeated seeking and completion with the [live checklist](TESTING.md#release-readiness). Protect access logs and FFmpeg diagnostics as described in [SECURITY.md](SECURITY.md#logging-and-diagnostics).

## Administrator APIs

All routes require an authenticated Emby administrator. Use the server's normal origin and URL Base. Extract accepts optional `Force`, `ItemId` and `LibraryId`; identifiers must be valid GUIDs.

| Method | Route | Action |
| --- | --- | --- |
| GET | `/StrmBridge/Admin/Health` | Loaded version/build and playback patch health |
| GET | `/StrmBridge/Admin/Diagnostics` | Health plus URL-free configuration counts and runtime state |
| POST | `/StrmBridge/Maintenance/Extract` | Extract missing information or explicitly force a refresh |
| POST | `/StrmBridge/Maintenance/Restore` | Restore matching technical snapshots |
| POST | `/StrmBridge/Maintenance/Cleanup` | Remove orphaned plugin snapshots and extraction state |
| POST | `/StrmBridge/Maintenance/Clear` | Clear plugin-managed technical information and recovery snapshots in scope |

Clear changes stored technical information; it is not an orphan cleanup operation. Media files, STRM files, scraped metadata and existing external subtitles are retained.

## Update, rollback, and uninstall

For an update, stop Emby, back up the existing DLL/configuration/recovery data, replace the DLL and restart. Confirm the loaded version/build and repeat the relevant live checks.

Version 0.2.3 uses snapshot schema 3. Older snapshots are ignored because they lack required technical fields. Missing items are reprobed; complete items require explicit refresh. Updating from prerelease builds already using schema 3 requires no further snapshot migration.

For a temporary playback fallback, select `Native` and save. To roll back the binary, stop Emby before replacing the DLL, restore compatible plugin configuration/recovery backups if necessary, and restart. Binary rollback does not undo technical fields already written to Emby.

To uninstall, stop Emby, remove the plugin DLL and restart, or use Emby's uninstall action and restart. Uninstalling does not delete media or STRM files and does not automatically clear previously written technical information.

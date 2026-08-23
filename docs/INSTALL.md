# Install, update, and uninstall

## Build a package

Run from the repository root:

```sh
./scripts/package.sh
```

The archive is created at `artifacts/Emby.StrmBridge-<version>.zip`. Build caches and test results remain below `.local/`.

## Install

1. Stop Emby Server.
2. Extract `Emby.StrmBridge.dll` into Emby's plugin directory. The playback patch runtime is embedded in this DLL.
3. Start Emby Server.
4. Open the STRM Bridge settings page.
5. Select one or more participating media libraries.
6. Select `Adaptive` playback mode.
7. Configure trusted cross-host redirect rules or review detected hosts after extraction.
8. Run **Extract missing STRM media information**.

The Emby service account needs read access to STRM files and write access to its plugin configuration directory. Media-library write access is optional for this plugin. Configuration localization uses Emby's native Generic UI request and does not modify dashboard files.

## Verify the installation

Check Emby's plugin log for:

```text
STRM_BRIDGE_PATCH_READY abi=4.9.5.0 targets=5
```

Request PlaybackInfo for a selected STRM Item and confirm its existing media source contains a relative URL beginning with:

```text
/StrmBridge/Playback/v2/
```

With an Emby reverse-proxy path prefix such as `/emby`, the result begins with `/emby/StrmBridge/Playback/v2/`.

During playback, privacy-safe log events identify the selected transport without printing URLs or tickets:

- `STRM_BRIDGE_PLAYBACK_REWRITTEN`
- `STRM_BRIDGE_TRANSCODE_INPUT_ROUTED`
- `STRM_BRIDGE_FAST_SEEK_READY`
- `STRM_BRIDGE_FAST_SEEK_APPLIED`
- `STRM_BRIDGE_GATEWAY_REDIRECT`
- `STRM_BRIDGE_GATEWAY_RELAY`
- `STRM_BRIDGE_GATEWAY_HLS`

In the settings page, switch the Emby Web language and confirm the title, labels and descriptions update while `Adaptive`, `RedirectOnly`, `RelayOnly` and `Native` remain unchanged.

## Update

Stop Emby, replace `Emby.StrmBridge.dll` with the file from the release archive, and restart.

## Roll back

Set playback mode to `Native`, save, and restart Emby. Then replace the plugin DLL with a previous release when required.

## Uninstall

Use Emby's plugin uninstall action or remove `Emby.StrmBridge.dll` while Emby is stopped. Restart Emby to ensure the patch lifecycle is complete. STRM files and media-library content retain their original state.

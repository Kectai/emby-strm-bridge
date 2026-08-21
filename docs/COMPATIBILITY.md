# Compatibility

## Compiled baseline

- Target framework: `netstandard2.1`
- Emby SDK: `MediaBrowser.Server.Core 4.9.1.80`
- First intended host verification: Emby Server 4.9.5.0

The repository compiles against the SDK baseline and tests its host-independent behavior. An Emby Server binary and configured media library are not present in this workspace, so the M0 real-host checks below are deliberately not marked complete.

## Required M0 host checks

Run these before relying on the playback bridge in production:

1. Confirm automatic type discovery loads the entry point, post-scan task, scheduled task, API services, and exactly one alternate media-source provider without replacing built-in providers.
2. Confirm redirecting movie and episode STRM items receive the provider's alternate source, while direct-media STRM items retain only their original source.
3. Record source ordering in PlaybackInfo for direct play, direct stream, and transcode.
4. Confirm `RequiredHttpHeaders` reaches Emby FFmpeg during technical probing.
5. Confirm `RequiresOpening = false` avoids live-stream-only lifecycle assumptions.
6. Measure PlaybackInfo wait behavior against FFmpeg input-validation timeouts.
7. Confirm `AddMediaInfoWithProbeSafe` scalar results saved through `ILibraryManager.UpdateItems` with `saveMetadata = false` and streams saved through `IItemRepository.SaveMediaStreams` survive item reload and do not create or rewrite NFO files.
8. Verify the gateway route works with the server's configured API base path, client requests retain their authenticated user context, and server-side consumers reach the absolute loopback URL without `X-Forwarded-For`, `X-Real-IP`, or RFC `Forwarded` headers.
9. Exercise same-host and explicitly allowlisted cross-host redirects, including chained redirects, DNS changes, non-default ports, and rejection of an untrusted loopback/private first target.
10. Exercise seek/reconnect after the two-minute unbound window and near the 24-hour bound-ticket limit with MP4, MKV multi-audio, and M2TS/PCM samples.
11. After successful, failed, and timed-out probes, audit Emby and FFmpeg logs to confirm full source/final URLs, query values, headers, and gateway tickets are absent or operationally redacted.
12. Race a library-file replacement and confirm the deployment's filesystem permissions keep untrusted local writers outside the STRM and plugin-configuration directories.
13. Confirm pending-host warnings appear once in the administrator dashboard activity log without any external notification service configured; when such a service is configured, confirm its link opens the native STRM Bridge settings page. Saving newly trusted hosts must queue one automatic retry and record a counts-only completion activity.

If any playback-related check fails on a host, turn off **Enable STRM Bridge playback** and use media-information extraction and URL-free persistence independently until that host integration is corrected.

## Client expectations

The gateway maintains User-Agent consistency by resolving only after the real gateway request arrives and forwarding that normalized User-Agent to the source. The same consumer should use its own User-Agent while following the 302. Clients that rewrite User-Agent between redirect hops may fail with sources that bind temporary URLs to that header.

Browser-native support for M2TS, PCM, subtitle formats, and HDR is outside this plugin. Emby may still choose remuxing or transcoding based on the extracted stream information.

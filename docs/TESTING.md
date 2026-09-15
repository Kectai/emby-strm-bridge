# Testing and release acceptance

## Maintained checks

Use the .NET SDK pinned by `global.json`, Node.js 20 or newer for the subtitle player tests, plus Git, ripgrep and zip/unzip. From the repository root:

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
| Optional subtitles | Shared video output, source/permission isolation, session ownership/capacity, actual time coverage, browser incremental output and cancellation |
| Network/privacy/package | Pinned DNS, trust revocation, proxy selection, header boundaries, bounded state and archive allowlist |

Add tests for changed contracts at their integration boundary. In particular, large HLS rewrites must not scan all tickets per URI; range validation must cover first authoritative reads as well as cache hits; a fresh probe must not succeed solely because the host returned preexisting fields.

## Actual-host compatibility check

After building the Release plugin, use the optional host check with an extracted Emby runtime directory. Build it against the 4.9.5.0 baseline, then run the same check and plugin DLL against each target host in a separate .NET 6 process:

```sh
dotnet build tools/Emby.HostCheck/Emby.HostCheck.csproj -c Release \
  -p:HostAssemblyDirectory="/path/to/emby-4.9.5-runtime"
/path/to/dotnet6/dotnet .local/host-check/Release/net6.0/Emby.StrmBridge.HostCheck.dll \
  "/path/to/target-emby-runtime" \
  "$PWD/.local/build/bin/Release/netstandard2.1/Emby.StrmBridge.dll"
```

The check logs the exact plugin fingerprint and host version, verifies video patch owners plus the subtitle runner, Web and per-source negotiation entries, repeats installation/disposal, exercises reflected input-state setters, and round-trips HDR/rotation snapshots. It loads actual host dependencies rather than the compile-time SDK. The normal two-argument run does not start Emby, invoke playback services, open media or read user configuration. Keep extracted host binaries outside the release package.

An optional third argument selects a synthetic fixture directory containing `fixture.mkv`: a 30-second video stream 0 and ASS stream 1, cues at 2–4 and 27–29 seconds with text `First fixture cue` and `Last fixture cue`. Use a file over 5 MiB so the limited origin can demonstrate early output. This opens only the synthetic fixture and runs one FFmpeg input producing both video and ASS; it checks first cue before media EOF, cancellation and mapped timestamps after seeking. The host directory must contain its FFmpeg executable.

An optional fourth argument supplies that host's original `dashboard-ui` directory. The check verifies the exact resource transformation, rejects an altered fingerprint and writes the transformed module to the fixture directory. After both host runs, use `node tools/subtitle-web-check.cjs /path/to/fixture-directory` to validate complete module syntax and automatic ASS/SubRip versus native dispatch. No host resources are redistributed.

## Subtitle verification: 0.2.5

The implementation emits subtitles from the video FFmpeg process. The obsolete independent subtitle input, demux pipeline and its fixtures have been removed. Current subtitle evidence covers the shared-output path.

Required checks cover:

- Six video integration targets and three independent subtitle targets (runner output, Web resource and playback negotiation) on actual 4.9.5.0 / 4.10.0.40 assemblies, including reinstall and removal.
- Actual runner command, source ticket, authenticated user and playback-session binding. The playlist start may differ from the native HLS segment start; test 184.2869822 seconds against a 180-second runner, then a backward seek with the original playlist URL.
- One host FFmpeg input producing both video and exact ASS cues before held-back media EOF, at zero and after a seek; ASS event duration must survive, and output timestamps must follow the measured video keyframe-to-HLS mapping.
- Local growing-file delivery across UTF-8/line boundaries, cancellation without stopping video, authorization revocation, no reuse across users or playback sessions, local window selection after seeks, and eviction after repeated seeks.
- Retained MSE initPTS across cold server outputs and repeated forward/backward seeks, continuity changes, and a single authoritative renderer clock despite stale frame/host callbacks.
- Exact transformed Web resources, startup selection, early rendering, natural window crossings and URL binding without copying credentials, bounded startup retries, and no native fallback on video-input-unavailable.

Record the DLL hash with host runs. A controlled renderer or direct command-line FFmpeg success does not establish the authenticated Safari pipeline. Test the installed build with initial playback, late selection, a window boundary, forward/backward seeks and a reopen before declaring the subtitle issue closed. Complex fonts, cold-seek long events, long pauses and multi-user load remain relevant acceptance cases.

## Evidence for 0.2.4

The maintained suite has **575 passing tests**. The release has also passed Release build, formatting, privacy and packaging checks. These results describe automated coverage, not completed live acceptance.

Offline checks used actual Emby `4.9.5.0` and `4.10.0.40` assemblies, .NET `6.0.36` and the host's bundled `5.1-emby` FFmpeg. Record the loaded DLL fingerprint alongside each offline run; distributed ZIP bytes are verified by the release checksum file.

| Offline check | Recorded result |
| --- | --- |
| Both host versions: six patch targets | Install, repeat install, remove all owned patches, reinstall |
| Both host versions: reflected input state | Media source identity, paths and protocols writable |
| Technical snapshot round trip | HDR/Dolby Vision and rotation retained |
| Stable representation, target 40 seconds | First frame at 40.0417 seconds; 479 continuous matching frames |
| Representation rejection / native recovery | First frame at 40.0 seconds; 480 continuous matching frames |
| 10,000-URI HLS ticket workload | Initial rewrite 32 ms; refresh 13 ms, versus 7,933/22,005 ms before the cleanup fix |

The HLS measurement covers local ticket bookkeeping, not end-to-end media or network latency. Offline evidence must be rerun when relevant code or the DLL changes. The patch, snapshot and 40-second positioning/native-recovery checks were repeated on both host versions for 0.2.4. The HLS timings are retained from the 0.2.3 prerelease regression of the same HLS implementation; timing values vary by machine and run.

On an installed 4.10.0.40 host, the 0.2.4 DLL reported all six patches ready. Playback and multiple seeks succeeded; intermittent HTTP 403 also occurred after redirect handoff, including in pre-upgrade logs. A separate RelayOnly run completed multiple seeks successfully. These observations do not establish the rejection trigger or a guaranteed workaround.

## Release readiness

<a id="implementation-gaps"></a>
<a id="release-readiness-20260906"></a>

**Release version: 0.2.5, stable channel.** Automated and actual-host offline checks cover their recorded scope. A complete live matrix across supported players and sources has not been recorded; stable-channel publication does not expand that verification coverage. Run the applicable deployment checks below with the installed build.

| Live scenario | Required observation |
| --- | --- |
| Install, upgrade and patch coexistence | Health version/BuildId matches the package; required video and optional subtitle patches install; no competing standard-route prefix; settings and recovery data survive the intended upgrade |
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

字幕时间回归覆盖 TS 封装偏移、关键帧、PTS 回绕、首分片视频时间合同、MSE 当前连续段 initPTS、旧事件取消、暂停和缓冲。实际宿主 Octopus 的 API 与 worker 时钟可离线核对：

```sh
node tools/subtitle-clock-check.cjs /path/to/4.9/dashboard-ui /path/to/4.10/dashboard-ui
```

启动、读取和渲染失败不得添加画面提示，诊断信息不得含原始异常、字幕或凭据。实际 StreamBuilder 检查共享 HLS、External 文字轨、默认字幕关闭／开启和流复制资格；Web 检查即时选轨、手动选择保护、相同毫秒帧顺序及尺寸不变时不清空画布。缺少主播放或任一字幕挂接时，不得协商无法提供的共享字幕链路。

`tools/subtitle-browser-check.cjs <fixture-directory> <4.9-web-root> <4.10-web-root>` 使用独立无界面浏览器及本地合成 HLS、共享 ASS 文件；执行宿主真实 libass/WASM，检查未结束字幕响应的首字幕显示、拖动、重新选轨及无提示。需要 Playwright 和对应测试浏览器，Chrome 路径可通过 CHROME_EXECUTABLE 指定。合成夹具需含 master.m3u8、分片和 shared.ass（2 秒及 22–30 秒有字幕），不连接实际 Emby 或媒体源。该检查与真实宿主协商测试分别验证前后端合同，不等同于已登录服务器的完整端到端验收。

代码复核新增：超长事件不冒充已读取覆盖、异常旧任务不阻塞健康候选、增量索引及回收预算、过期请求不污染当前字幕、完成预取不重复请求、异步启动后的缓冲状态重读、终止错误清理和同轨失败重试。用户已确认 preview.17 的一次 Safari 实片测试正常；这不替代所有影片、字体、并发及宿主版本的验收。

preview.19 新增边界验证：原生 HLS 不宣告字幕能力、不发插件会话请求，视频继续原生播放；即使客户端能力曾声明支持，实际缺少 hls.js 实例也不接管。普通浏览器夹具统一执行宿主 hls.js。`SUBTITLE_TEST_NATIVE=1` 单独检查 WebKit 原生路径排除；`SUBTITLE_TEST_COLD=1` 检查 MSE 冷跳转。

## Live acceptance: preview.19 (2026-09-15)

The user confirmed normal real-media playback with the installed preview.19 build. The installed DLL SHA-256 matches the tested package: `99b02368ca8e093c3f1aa315843677f49d2186190c65941b34aa2bb439a7b16f`. Emby 4.10.0.40 loaded both the video and subtitle patches successfully after restart.

Sanitized server records show Safari, Chrome and IINA playback during 22:10–22:15, including reopening and repeated forward/backward position changes. Three subtitle sessions produced 37 shared-output opens; all used hls.js, with a stable mapping within each session even when selecting output from different video jobs. Eleven video FFmpeg jobs shared their media input with subtitle output. The reviewed logs contain no server errors or warnings and no matched FFmpeg HTTP 403/429, conversion, decode or timeout failures. Visual rendering and synchronization acceptance comes from the user's observation, supported by the server-side evidence.

This closes the missing current-build live acceptance for the tested playback scenario. Together with the automated and actual-host checks, the build can proceed to formal release preparation within its documented scope. It does not certify every subtitle/font combination or sustained multi-user load. The release pipeline verifies the final version/tag, committed source and published artifact checksum. Raw logs, URLs, credentials and media titles are not included in release documentation.

# Emby.StrmBridge

[中文](#中文) | [English](#english)

## 中文

Emby Server 的 STRM 媒体信息与播放插件。为所选媒体库中的本地 `.strm` 文件提取音频、视频技术信息，并为视频 STRM 提供重定向、HLS 改写及流式中转。音频播放保留 Emby 原生行为。

本项目源于作者的个人使用需求，公开代码供有类似需求的用户参考和使用，随个人需求不定期更新，目前没有固定维护计划。

当前版本为 **0.2.3**正式版。播放补丁适配 Emby Server **4.9.5.x**；安装前请查看[兼容性](docs/COMPATIBILITY.md)和[发布说明](RELEASE_NOTES.md)。

### 功能

- 提取容器、时长、码率及音视频和字幕流信息，保留既有外置流；可保存技术快照用于恢复。
- 默认只处理技术信息缺失的项目；刷新完整项目需关闭“仅提取缺失”或显式强制提取。
- 保留 Emby 的媒体版本、播放权限、客户端能力协商和转码决策，网关只接管精确匹配的所选视频 STRM。
- 客户端网关地址沿用当前 Emby Origin；服务端 FFmpeg 使用由 Emby 提供的本地回环地址。
- 对符合条件的远程 TS/M2TS 服务端处理提供有界快速定位，证据不足时保留原生定位。
- 配置界面支持简体中文、繁体中文和英文。

### 播放模式

| 模式 | 行为 |
| --- | --- |
| `Adaptive`（默认） | 普通文件优先重定向；未知类型先分类；HLS 改写，需中转的资源通过 Emby 转发 |
| `RedirectOnly` | 重定向交接，不改写 HLS 清单 |
| `RelayOnly` | 由 Emby 流式中转，消耗服务器带宽 |
| `Native` | 保持 Emby 原生播放行为 |

重定向交接后，媒体由客户端或 FFmpeg 直接读取，插件不能控制其后续并发、重试或 CDN 响应。`RelayOnly` 有传输容量保护，但没有缩略图与主播放的优先级调度。

### 开始使用

1. 按[安装文档](docs/INSTALL.md)校验压缩包、安装单个 `Emby.StrmBridge.dll` 并重启 Emby。
2. 在插件设置中选择参与处理的媒体库，确认播放模式和跨主机重定向信任规则。媒体库留空表示不处理任何项目。
3. 运行“提取缺失的 STRM 媒体信息”，然后验证实际播放和拖动。

STRM 必须是本地普通文件，内容为一条 HTTP(S) URL。动态媒体源或依赖额外来源请求头的媒体源保留原生播放。不要同时启用其他插件中会接管同一视频入口的直链功能。

常用设置：

| 设置 | 默认值 | 说明 |
| --- | --- | --- |
| 仅提取缺失的媒体信息 | 开启 | 完整项目即使 STRM 来源变化也跳过；需要更新时显式刷新 |
| 保存恢复快照 | 开启 | 关闭后，成功探测的技术信息仍写入 Emby |
| 直达重定向缓存时间 | 20 秒 | 范围 0–60；一次性、按 Range 绑定或有效期更短的签名地址设为 0 |
| 启用有证据的快速定位 | 开启 | 只影响符合条件的 TS/M2TS 服务端定位，可独立关闭 |

**已知缩略图兼容问题：** 部分客户端生成进度条缩略图会增加媒体读取请求，在来源限制并发或请求频率时可能造成持续缓冲或拖动卡住。遇到此类问题，可先关闭客户端的缩略图／实时预览，详见[兼容说明](docs/COMPATIBILITY.md#client-generated-seek-thumbnails)。

### 构建与验证

需要 `global.json` 指定的 .NET SDK，以及 Git、ripgrep、zip/unzip。

```sh
./scripts/verify.sh
./scripts/package.sh
```

构建及测试状态位于 `.local/`，安装包与 SHA-256 校验文件位于 `artifacts/`。打包命令会执行完整验证，无需先重复运行验证命令。详细测试范围及实机验收见[TESTING.md](docs/TESTING.md)。

### 文档

- [安装、配置与管理员接口](docs/INSTALL.md)
- [兼容性与已知限制](docs/COMPATIBILITY.md)
- [安全与隐私](docs/SECURITY.md)
- [设计与实现](docs/STRM_BRIDGE_DESIGN.md)
- [测试与验收](docs/TESTING.md)
- [版本变更](CHANGELOG.md)

项目采用 [MIT License](LICENSE)，内嵌依赖声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## English

An Emby Server plugin for technical media information and video playback of local `.strm` items in selected libraries. It extracts audio/video metadata and provides redirects, HLS rewriting and streaming relay for video STRM. Audio playback remains native.

This project serves the author's personal needs and is shared for others with similar setups. Updates follow those needs; there is no fixed maintenance schedule.

The current version is **0.2.3**, a stable release. Playback patches target Emby Server **4.9.5.x**. Read the [compatibility notes](docs/COMPATIBILITY.md) and [release notes](RELEASE_NOTES.md) before installing.

### Features and modes

- Extract technical fields and internal streams while retaining existing external streams; optionally save recovery snapshots.
- Skip complete items by default. Refreshing a complete item requires force or disabling missing-only extraction, even when its STRM source changes.
- Preserve Emby's media-source selection, permissions, capability negotiation and transcoding decisions.
- Use the client's current Emby origin for relative playback routes and Emby's local API address for server-side FFmpeg.
- Apply bounded fast positioning to eligible remote TS/M2TS server jobs; retain native seeking when evidence is insufficient.
- Localize configuration in English, Simplified Chinese and Traditional Chinese.

| Mode | Behavior |
| --- | --- |
| `Adaptive` (default) | Prefer redirects for files, classify unknown resources, rewrite HLS and relay where needed |
| `RedirectOnly` | Redirect without HLS manifest rewriting |
| `RelayOnly` | Stream through Emby, consuming server bandwidth |
| `Native` | Keep native Emby playback |

After redirect handoff, the reader's connections, retries and CDN responses are outside plugin control. Relay capacity protection is not a thumbnail-versus-playback priority scheduler.

### Setup

Follow [INSTALL.md](docs/INSTALL.md) to verify the package, install the single DLL and restart Emby. Select participating libraries and trusted redirect hosts, then run extraction and test playback. An empty library selection processes nothing.

STRM files must be regular local files containing one HTTP(S) URL. Dynamic sources and sources requiring extra upstream headers retain native playback. Avoid competing plugins that short-circuit the same video entry point.

Missing-only extraction, recovery snapshots and evidence-based fast positioning default to enabled. Disabling snapshots does not prevent successful technical updates to Emby. Direct redirect caching defaults to 20 seconds, accepts 0–60, and must be set to 0 for one-use, Range-bound or shorter-lived signed addresses. Fast positioning can be disabled independently.

**Known thumbnail compatibility issue:** client-generated seek previews add media reads and may stall playback when a source limits concurrency or request rate. If affected, try disabling seek thumbnails/live previews. See [compatibility](docs/COMPATIBILITY.md#client-generated-seek-thumbnails).

### Development and documentation

Use the .NET SDK specified by `global.json`, Git, ripgrep and zip/unzip. Run `./scripts/verify.sh` for checks or `./scripts/package.sh` to verify and package in one step. Mutable build/test state stays in `.local/`; ZIP and checksum files are produced in `artifacts/`.

See [installation and administrator APIs](docs/INSTALL.md), [security](docs/SECURITY.md), [design](docs/STRM_BRIDGE_DESIGN.md), [testing](docs/TESTING.md) and [changelog](CHANGELOG.md). The project uses the [MIT License](LICENSE); bundled dependency notices are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

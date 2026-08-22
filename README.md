# Emby.StrmBridge

[中文](#中文) | [English](#english)

## 中文

`Emby.StrmBridge` 是一个面向本地 `.strm` 文件的 Emby Server 插件，每个文件的内容为一个 HTTP(S) URL。插件可以提取技术媒体信息，并通过同一 Emby Server 进程内的网关路由所选 STRM 媒体的播放请求。

**本项目源于作者的个人使用需求。**项目主要用于改善个人 Emby STRM 媒体库的技术信息提取和播放兼容性。代码公开在 GitHub，主要用于留存项目，也希望能为有类似需求的用户提供参考或直接使用。实际效果可能随 Emby 版本、客户端和上游媒体服务而变化；项目将随作者自身需求不定期更新，目前没有固定的维护计划或功能路线图。

### 播放模型

Emby 先生成原生 PlaybackInfo 响应。对于 Emby Server 4.9.5.x，插件通过经过版本校验的 Harmony 补丁衔接静态播放生命周期中的三个位置：

- PlaybackInfo 中匹配媒体源的 `DirectStreamUrl` 使用服务器相对网关路由，并保留原生 `Path` 与 `ProbePath`；
- 客户端采用 Emby 标准静态视频路由时，插件按项目和媒体源 ID 验证目标 STRM，并在原生代取开始前交给同一个网关处理。
- Emby 启动 FFmpeg 转码前，插件再次验证转码作业中的项目、媒体源 ID 和当前 STRM 来源，仅把该作业的输入改为 Emby 运行时提供的回环网关地址。

```text
{当前-api-路径前缀}/StrmBridge/Playback/v2/{随机票据}/stream{扩展名}
```

该路由自动沿用客户端访问 Emby 时选择的 Origin，适用于本地访问、远程访问、HTTPS 反向代理和 Emby 路径前缀，无需在 STRM 文件中保存固定服务器地址。

服务端转码输入地址由 Emby 的本地 API 地址接口生成，不配置也不写死主机名或端口。媒体源数量、顺序、ID、名称、原生路径、技术流和播放能力保持不变；只有匹配 STRM 的客户端播放地址及单次 FFmpeg 作业输入进入网关。

标准静态视频适配仅接受 GET/HEAD、`Static=true`、参与处理媒体库中的 `.strm` 项目以及精确 ID 匹配的静态媒体源。其余视频请求继续使用 Emby 原生处理。路由判断不包含播放器名称或厂商标识。

播放模式：

- `Adaptive`：跟随已获信任的重定向链，通过 Emby 中继最终文件或 HLS 流。
- `RedirectOnly`：解析已获信任的重定向链，以 HTTP 302 返回最终地址。
- `RelayOnly`：通过 Emby 中继所选来源。
- `Native`：保持 Emby PlaybackInfo、标准静态视频和 FFmpeg 输入行为不变。

默认模式为 `Adaptive`。上游请求会保留客户端 User-Agent 和 Range 请求头，从而支持与实际播放请求绑定的临时地址。

文件中继会为每个播放票据和标准化请求上下文保存一个有界、仅内存、有效期 30 秒的重定向租约。首次 Range 请求完成可信重定向解析后，临近的 Range 请求从已验证的有效地址开始。首次重定向链或租约目标返回 401、403、404、410 时，会释放该响应并从 STRM 原始地址重新解析一次；第二次结果直接返回。

### 媒体信息

插件提供计划任务提取与恢复功能：

- **提取缺失的 STRM 媒体信息**
- **清理 STRM Bridge 存量媒体信息**

提取流程使用 Emby 的媒体探测 API，并保存不含 URL 的快照。刷新内部视频、音频和字幕信息时会保留已有外置流。关闭 **仅提取缺失的媒体信息** 后，会重新探测每个已选 STRM 并替换对应快照。

### 配置

至少选择一个参与处理的媒体库。留空表示提取和播放的处理范围均为空。

跨主机重定向由管理员信任规则控制，支持：

- 精确 DNS 主机名
- 精确 IP 地址
- CIDR 范围
- `*.example.com` 形式、受 DNS 标签边界约束的子域规则

检测到的重定向主机在保存后继续显示并标记当前信任状态。新增信任后会排队执行一次有界提取重试。插件日志和检测主机目录不保存完整 URL、路径、查询值、签名、请求头、票据、媒体标题或用户名。

### 构建、测试和打包

```sh
./scripts/verify.sh
./scripts/package.sh
```

所有可变构建和测试状态均位于 `.local/` 下。发布压缩包生成到 `artifacts/`，包含一个自包含的 `Emby.StrmBridge.dll`、当前文档和许可证声明。Harmony 运行库以经过身份校验的资源嵌入插件 DLL，安装时无需复制第二个 DLL。

### 管理员 API

- `POST /StrmBridge/Maintenance/Extract`
- `POST /StrmBridge/Maintenance/Restore`
- `POST /StrmBridge/Maintenance/Cleanup`
- `POST /StrmBridge/Maintenance/Clear`

维护路由要求 Emby 管理员权限。播放路由使用高熵、仅内存的能力票据；客户端提供认证信息时，还会校验当前 Emby 用户。

### 文档

- [播放网关设计](docs/PLAYBACK_GATEWAY_DESIGN.md)
- [架构](docs/ARCHITECTURE.md)
- [安装](docs/INSTALL.md)
- [安全](docs/SECURITY.md)
- [兼容性](docs/COMPATIBILITY.md)
- [测试](docs/TESTING.md)

### 许可证

STRM Bridge 使用 MIT 许可证。内嵌 Harmony 运行库的许可证声明请参阅 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

---

## English

`Emby.StrmBridge` is an Emby Server plugin for local `.strm` files whose content is one HTTP(S) URL. It extracts technical media information and routes selected STRM playback through an in-process gateway on the same Emby server.

**This project originates from the author's personal use requirements.** It primarily improves technical media-information extraction and playback compatibility for the author's personal Emby STRM library. The source is published on GitHub for project preservation and as a reference or directly usable option for people with similar needs. Results may vary with Emby versions, clients, and upstream media services. Updates follow the author's own needs, with no fixed maintenance schedule or feature roadmap.

### Playback model

Emby builds the native PlaybackInfo response first. On Emby Server 4.9.5.x, version-gated Harmony patches integrate at three points in the static-playback lifecycle:

- an exact matching source in PlaybackInfo receives a server-relative `DirectStreamUrl`, while its native `Path` and `ProbePath` remain unchanged;
- when a client uses Emby's standard static-video route, the plugin validates the item and media-source ID before handing the request to the same gateway ahead of native upstream retrieval.
- immediately before Emby starts FFmpeg, the plugin revalidates the job's item, media-source ID, and current STRM source, then changes only that job's input to the loopback gateway address reported by the running Emby instance.

```text
{current-api-path-base}/StrmBridge/Playback/v2/{random-ticket}/stream{extension}
```

The route automatically uses the Emby Origin selected by the client. It supports local access, remote access, HTTPS reverse proxies, and an Emby path prefix without storing a fixed server address in STRM files.

The server-side transcode input comes from Emby's local-API helper, with no configured or hard-coded hostname or port. Source count, order, IDs, names, native paths, technical streams, and playback capabilities stay unchanged; only the client playback address and the input of an exact matching FFmpeg job enter the gateway.

The standard static-video adapter accepts only GET/HEAD requests with `Static=true`, a `.strm` item in a participating library, and an exact static-media-source ID match. All other video requests continue through Emby's native implementation. Routing decisions contain no player names or vendor identifiers.

Playback modes:

- `Adaptive`: follows approved redirect chains and relays the resulting file or HLS stream through Emby.
- `RedirectOnly`: resolves an approved redirect chain and returns the final URL as HTTP 302.
- `RelayOnly`: streams selected sources through Emby.
- `Native`: keeps Emby's PlaybackInfo, standard static-video, and FFmpeg input behavior unchanged.

`Adaptive` is the default. It preserves the client User-Agent and Range headers during upstream requests, which supports temporary URLs bound to the actual playback request.

File relay keeps a bounded 30-second, memory-only redirect lease for each playback ticket and normalized request context. After the first Range request resolves the approved chain, nearby Range requests start at the validated effective address. A fresh redirect chain or leased target ending in 401, 403, 404, or 410 releases that response and resolves once from the STRM source; the second result is returned directly.

### Media information

The plugin provides scheduled extraction and recovery:

- **Extract missing STRM media information**
- **Clear stored STRM Bridge media information**

Extraction uses Emby's media probe APIs and saves a URL-free snapshot. Existing external streams remain present when internal video, audio, and subtitle information is refreshed. Turning off **Only extract missing media information** performs a fresh probe for every selected STRM and replaces the matching snapshot.

### Configuration

Select at least one participating media library. An empty selection defines an empty processing scope for extraction and playback.

Cross-host redirects use administrator trust rules. Supported entries are:

- exact DNS hostname
- exact IP address
- CIDR range
- label-bounded subdomain rule such as `*.example.com`

Detected redirect hosts remain visible after saving and show their current trust state. Saving new trust queues a bounded extraction retry. Full URLs, paths, query values, signatures, headers, tickets, media titles, and user names stay outside plugin logs and the detected-host catalog.

### Build, test, and package

```sh
./scripts/verify.sh
./scripts/package.sh
```

Mutable build and test state stays below `.local/`. Release archives are written to `artifacts/` and contain one self-contained `Emby.StrmBridge.dll`, current documentation, and license notices. The Harmony runtime is an identity-checked embedded resource, so installation does not require copying a second DLL.

### Administrator APIs

- `POST /StrmBridge/Maintenance/Extract`
- `POST /StrmBridge/Maintenance/Restore`
- `POST /StrmBridge/Maintenance/Cleanup`
- `POST /StrmBridge/Maintenance/Clear`

Maintenance routes require an Emby administrator. Playback routes use high-entropy, memory-only capability tickets and also verify the current Emby user when the client supplies authentication.

### Documentation

- [Playback gateway design](docs/PLAYBACK_GATEWAY_DESIGN.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Installation](docs/INSTALL.md)
- [Security](docs/SECURITY.md)
- [Compatibility](docs/COMPATIBILITY.md)
- [Testing](docs/TESTING.md)

### License

STRM Bridge is MIT licensed. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for the bundled Harmony runtime notice.

# STRM Bridge 播放网关设计 / Playback Gateway Design

[中文](#中文) | [English](#english)

## 中文

### 1. 目标

STRM Bridge 是一个面向本地 `.strm` 文件的 Emby Server 插件，每个文件包含一个 HTTP(S) 媒体 URL。插件提供两个相互配合的功能：

- 通过 Emby 的媒体探测和存储库 API 提取技术媒体信息；
- 通过沿用当前 Origin 的进程内网关路由播放请求。

插件以只读方式使用媒体库文件。Emby 继续负责媒体库组织、元数据刮削、权限、媒体版本选择、PlaybackInfo 生成和外部播放器启动行为。

### 2. 支持的宿主

插件面向 `netstandard2.1` 和 Emby Server 4.9.5.x。运行时 ABI 校验确认两个 PlaybackInfo 服务方法、渐进式请求执行方法、FFmpeg 启动方法及其请求和响应结构后，播放路由才会启用。播放 ABI 校验失败时，媒体信息提取和维护功能仍可独立使用。

Harmony 以经过身份校验的程序集资源嵌入 `Emby.StrmBridge.dll`，因此发布包只需安装一个插件 DLL。

### 3. 配置模型

管理员可配置：

- 插件启用状态；
- 一个或多个参与处理的媒体库；
- 播放模式：`Native`、`RedirectOnly`、`Adaptive` 或 `RelayOnly`；
- 提取计划、强制重新探测行为和持久化；
- 提取与网关超时；
- 重定向跳数与中继并发上限；
- 跨主机重定向信任规则。

媒体库选择留空表示处理范围为空。信任规则支持精确主机名、精确 IP 地址、CIDR 范围和受 DNS 标签边界约束的通配规则。`*.example.com` 可匹配其子域名，同时排除裸后缀和外观相似的主机名。

提取流程维护一个有界的跨主机重定向检测目录。配置页面展示检测结果、标记已信任项目、允许管理员选择待确认的精确主机，同时也接受手工输入的规则。保存新增信任后，会排队执行一次有界提取重试。

### 4. STRM 来源约束

`StrmSourcePolicy` 接受同时满足以下条件的来源：

- 以 `.strm` 结尾的本地绝对路径；
- 解析后的祖先路径链中不存在重解析点的普通文件；
- 最大 16 KiB；
- 严格 UTF-8 文本，可带 BOM；
- 仅包含一个非空 HTTP(S) URL；
- 读取过程中长度和最后修改时间保持稳定。

来源身份由 HMAC 派生的存储键和指纹、本地文件大小及修改时间组成。原始路径和 URL 不进入持久化键或日志字段。

### 5. 播放请求集成

Emby 首先生成原生 `PlaybackInfoResponse`，随后由经过版本校验的后置补丁处理 GET 和 POST PlaybackInfo 结果。

对于每个匹配的 STRM 媒体源，`PlaybackInfoProcessor` 执行：

1. 通过 GUID 或宿主内部数字 ID 解析所属媒体项目；
2. 确认项目属于参与处理的媒体库；
3. 读取当前 STRM 身份；
4. 验证响应中的媒体源仍映射到同一静态来源；
5. 签发一个 256 位、仅内存的播放票据；
6. 克隆媒体源，将 `DirectStreamUrl` 设为相对网关路由，并保留原生 `Path` 与 `ProbePath`；
7. 仅在运行时代际仍有效时提交完整媒体源数组。

路由格式：

```text
{api-路径前缀}/StrmBridge/Playback/v2/{票据}/stream{扩展名}
```

相对路由使每个客户端沿用获取 PlaybackInfo 时使用的 Emby Origin 和可选 API 路径前缀。媒体源数量、顺序、ID、名称、原生路径、流元数据及播放能力字段与原生响应保持一致。

部分客户端使用媒体源 ID 调用 Emby 标准静态视频接口。`NativeVideoStreamProcessor` 在原生上游代取开始前验证该请求：

1. 请求方法为 GET 或 HEAD，并且 `Static=true`；
2. 请求项目和精确媒体源 ID 均可解析；
3. 媒体源所属项目位于参与处理的媒体库中，并以本地 `.strm` 文件作为来源；
4. 静态媒体源无需打开令牌、动态打开流程或附加 HTTP 请求头；
5. 静态媒体源地址与当前 STRM 内容完全一致；
6. 当前 Emby 授权身份用于绑定临时播放票据；
7. 同一 HTTP 请求、Range 上下文和响应管线交给 `GatewayService` 执行。

满足全部条件的标准静态请求进入网关，其余请求继续执行 Emby 原生方法。所有集成入口共用票据、权限、来源指纹、重定向信任和传输实现，路由决策仅依据请求与媒体来源属性。

Emby 创建服务端转码状态后，`TranscodeInputProcessor` 在 `StartFfMpeg` 执行前处理单次作业：

1. 从作业请求中解析项目与精确媒体源 ID，并要求该 ID 等于作业当前媒体源 ID；
2. 确认请求项目和来源项目均属于参与处理的媒体库，来源项目为本地 `.strm` 文件；
3. 重新读取当前 STRM，验证宿主静态媒体源与作业媒体源均对应同一来源；
4. 签发单次作业使用的内存票据；
5. 通过 Emby 的 `GetLocalApiUrl(IPAddress.Loopback)` 获取运行时本地 API Origin，并结合当前请求的 API 路径前缀生成绝对回环网关地址；
6. 仅修改当前 `StreamState` 的媒体源副本、媒体路径和协议，使 FFmpeg 从网关读取；
7. 任一生成或写入步骤失败时恢复原始作业状态并撤销票据。

该入口不修改媒体库项目、STRM 文件、持久化媒体源或 PlaybackInfo 的原生路径。非 STRM、本地媒体文件、未参与媒体库、不匹配媒体源及 `Native` 模式继续使用 Emby 原生转码输入。

### 6. 票据模型

票据是由 256 位随机数支持的 Base64URL 承载能力，其仅内存载荷包含：

- 票据作用域；
- 媒体项目和媒体源身份；
- 当前 STRM 来源身份；
- 上游 URI；
- 运行时代际；
- 可选的 HMAC 用户绑定；
- 签发、预览、活动和绝对过期时间；
- HLS 嵌套深度。

播放票据具有 10 分钟预览窗口。完成授权兑换后，其活动期按媒体时长加重连宽限期延长，绝对上限为 24 小时。HLS 子票据继承根播放票据的边界。同一播放票据下对相同绝对 HLS 资源的重复引用会复用同一个子票据；撤销根票据时会一并撤销其子票据。

### 7. 网关授权

`GatewayService` 在建立上游连接前依次检查：

1. 路由文件名和票据语法；
2. 票据作用域及根/子票据关系；
3. 已启用的播放模式和已初始化的运行时；
4. 可选认证用户的播放权限及票据绑定；
5. 运行时代际；
6. 当前媒体库参与范围和认证用户的项目可见性；
7. 未发生变化的 STRM 文件身份。

外部播放器无需转发 Emby 认证信息即可兑换高熵票据。请求携带 Emby 认证信息时，网关会校验用户绑定。所有校验失败统一返回资源不可用响应。

### 8. 重定向策略

网关关闭 HTTP 客户端的自动重定向并自行跟随重定向。每一跳都必须在连接前通过 `RedirectPolicy`。

可接受的目标：

- 使用 HTTP 或 HTTPS；
- 不包含用户信息、片段或控制字符；
- 当前跳为 HTTPS 时保持 HTTPS；
- 保持同一主机，或匹配管理员信任规则；
- 位于配置的重定向跳数上限内。

相对重定向基于当前跳解析。HLS 清单引用在签发子票据前也经过同一策略。

### 9. 传输行为

网关转发固定请求头集合：`Range`、`If-Range`、`If-None-Match`、`If-Modified-Since`、`Accept`、`Accept-Language` 和 `Cache-Control`。它保留实际播放请求的 User-Agent，请求 identity 编码，并且不使用共享 Cookie 容器。

分类器通过 MIME 类型、有效地址的 `.m3u8` 路径或 `#EXTM3U` 内容签名识别 HLS。用于识别的内容前缀会重新交给下游消费者，因此分类过程不会丢失媒体字节。

模式行为：

| 模式 | 结果 |
| --- | --- |
| `Native` | 保持原生 PlaybackInfo 和标准视频服务行为。 |
| `RedirectOnly` | 网关以 HTTP 302 返回经过验证的有效地址。 |
| `Adaptive` | 重写并中继 HLS；其他响应通过网关流式中继。 |
| `RelayOnly` | 通过网关流式中继经过验证的响应。 |

普通中继保留状态码、内容类型、有效内容长度、字节范围元数据、验证器和安全的内容处置字段。响应携带 `private, no-store`、`Pragma: no-cache`、`nosniff` 和 `no-referrer`。请求取消和读取空闲超时在整个响应体传输期间持续生效。

### 10. 重定向租约

成功完成重定向的文件请求会创建一个仅内存租约，其键由以下内容的摘要组成：

- 播放票据；
- 标准化的 GET 或 HEAD 方法；
- 标准化 User-Agent；
- `Accept` 和 `Accept-Language`。

Range 值不进入租约键，因此临近字节请求可共享同一个已验证目标。按键单飞门会合并同时发生的首次解析。缓存目标每次使用前都会重新通过策略校验，有效期为 30 秒；到期、超时、传输拒绝或返回 401、403、404、410 时会被清除。首次重定向链的最终目标返回上述状态时也会释放响应。两种情况均允许从 STRM 原始地址重试一次，并直接采用第二次结果。运行时失效使用代际校验，阻止较晚完成的在途解析重新写入已清空的租约。

### 11. HLS 中继

网关读取清单时执行以下边界限制：

- 编码内容最大 2 MiB；
- 最多 20,000 行；
- 最多 10,000 个 URI 引用；
- 最大嵌套深度 8。

媒体行和 `URI=` 属性会被改写为子网关路由：

```text
{api-路径前缀}/StrmBridge/Playback/v2/{根票据}/hls/{子票据}/resource{扩展名}
```

主清单、媒体清单、分片、密钥、初始化段、音频和字幕资源使用同一套授权、来源完整性、重定向策略和传输管线。

### 12. 媒体信息提取

提取流程仅选择参与处理媒体库中的 STRM 项目。它使用 Emby 的媒体源和探测 API，保留内嵌视频、音频、字幕流及已有外置流，并通过 Emby 媒体流存储库写入结果。

持久化快照仅包含技术字段：

- 容器、大小、码率和时长；
- 默认音轨和字幕索引；
- 有界的视频、音频和字幕流字段。

图像流和附件流不进入快照。URL、路径、请求头、凭据、签名、标题、媒体库名称和用户名也不进入快照。

启用 **仅提取缺失的媒体信息** 时，会跳过信息完整且来源未变化的项目。关闭该选项后，会执行新的远程探测并替换当前技术快照。维护清理操作会删除所选范围内的持久化快照和由插件写入的内部流数据，同时保留外置流和媒体文件。

### 13. 生命周期与容量

运行时状态使用单调递增的代际。敏感配置变化和关闭流程会清除票据、重定向租约、运行时检测主机及待处理工作。PlaybackInfo 提交、标准静态请求转交和网关票据兑换均要求当前代际有效。

容量限制：

- 播放票据：4,096；
- HLS 票据：20,000；
- 重定向租约：4,096；
- 中继并发：1–16；
- 重定向跳数：1–8；
- 网关超时：10–180 秒；
- 提取并发：1–2；
- 提取超时：30–180 秒。

网关达到容量上限时返回 503，并附带简短的重试提示。上游读取空闲超时返回 504。PlaybackInfo 映射、补丁或票据处理失败时保留 Emby 原生响应；标准静态请求在完成全部匹配检查前保持 Emby 原生执行。

### 14. 持久化与隐私

持久化文件位于 Emby 插件配置目录下：

```text
Emby.StrmBridge/
├── identity.key
├── mediainfo/
│   ├── <hmac-存储键>.json
│   └── <hmac-存储键>.json.bak
└── state/
    ├── extraction-state.json
    └── extraction-state.json.bak
```

写入过程使用唯一命名的临时文件和原子替换，并限制序列化大小、项目数量和对象图规模。

日志使用固定事件名，仅在需要时包含异常类型、ABI 版本、目标数量、汇总数量和缩短的项目身份。URL、路径、主机、查询值、签名、请求头、User-Agent 值、票据、标题、媒体库名称和用户名不进入插件日志。

### 15. 发布验收

发布候选版本需满足以下检查：

- Release 构建无警告完成；
- 格式、单元、集成、隐私和打包测试全部通过；
- 发布包包含一个插件 DLL、当前文档和许可证声明；
- 内嵌 Harmony 程序集名称和版本符合预期，NuGet 包匹配锁定的内容哈希；
- 在 Emby 4.9.5.x 上，GET 和 POST PlaybackInfo 保持媒体源身份并生成相对网关路由；
- 在 Emby 4.9.5.x 上，标准静态视频 GET 和 HEAD 对精确 STRM 媒体源进入同一网关，其他请求保持原生执行；
- 在 Emby 4.9.5.x 上，精确 STRM 转码作业在 FFmpeg 启动前使用运行时回环网关输入，其他作业保持原生输入；
- 直出响应、重定向文件、Range/HEAD、拖动、重连和 HLS 播放通过宿主矩阵；
- 本地、远程、HTTPS 代理和 API 路径前缀访问均沿用客户端的 Emby Origin；
- 发布产物不包含测试凭据、个人路径或针对特定播放器厂商的路由逻辑。

---

## English

### 1. Purpose

STRM Bridge is an Emby Server plugin for local `.strm` files containing one HTTP(S) media URL. It provides two coordinated functions:

- technical media-information extraction through Emby's media-probe and repository APIs;
- playback routing through a current-Origin, in-process gateway.

The plugin keeps media-library files read-only. Emby remains responsible for library organization, metadata scraping, permissions, media-version selection, PlaybackInfo generation and external-player launch behavior.

### 2. Supported host

The plugin targets `netstandard2.1` and Emby Server 4.9.5.x. Playback routing activates after a runtime ABI check confirms two PlaybackInfo service methods, the progressive request executor, the FFmpeg startup method, and their request and response shapes. Media-information extraction and maintenance remain independently available when the playback ABI check fails.

Harmony is embedded as an identity-checked assembly resource in `Emby.StrmBridge.dll`. The release package therefore installs as one plugin DLL.

### 3. Configuration model

The administrator configures:

- enabled state;
- one or more participating media libraries;
- playback mode: `Native`, `RedirectOnly`, `Adaptive` or `RelayOnly`;
- extraction scheduling, fresh-probe behavior and persistence;
- extraction and gateway timeouts;
- redirect hop and relay-concurrency limits;
- cross-host redirect trust rules.

An empty media-library selection defines an empty processing scope. Exact hostnames, exact IP addresses, CIDR ranges and label-bounded wildcard rules are accepted. A rule such as `*.example.com` covers subdomains while keeping the bare suffix and lookalike names outside the rule.

Extraction records a bounded catalog of detected cross-host redirect names. The configuration page shows detected names, marks trusted entries, lets the administrator select pending exact hosts and also accepts manually entered rules. Saving newly trusted hosts queues a bounded extraction retry.

### 4. STRM source contract

`StrmSourcePolicy` accepts a source that satisfies all of these conditions:

- rooted local path ending in `.strm`;
- regular file with no reparse point in its resolved ancestor chain;
- maximum size of 16 KiB;
- strict UTF-8 text with optional BOM;
- one non-empty HTTP(S) URL;
- stable length and last-modified time during reading.

The source identity contains an HMAC-derived storage key and fingerprint plus local file size and modification time. Raw paths and URLs stay outside persistent keys and log fields.

### 5. Playback request integration

Emby first creates its native `PlaybackInfoResponse`. A version-gated postfix then processes GET and POST PlaybackInfo results.

For every matching STRM media source, `PlaybackInfoProcessor`:

1. resolves the owning library item from GUID or numeric host item ID;
2. confirms that the item belongs to a participating library;
3. reads the current STRM identity;
4. verifies that the response source still maps to the same static media source;
5. issues a 256-bit, memory-only playback ticket;
6. clones the source, sets `DirectStreamUrl` to a relative gateway route, and preserves the native `Path` and `ProbePath`;
7. commits the complete source array only while the runtime generation remains current.

The route shape is:

```text
{api-path-base}/StrmBridge/Playback/v2/{ticket}/stream{extension}
```

A relative route lets each client reuse the Emby Origin and optional API path prefix through which it obtained PlaybackInfo. Source count, order, IDs, names, native paths, stream metadata, and playback capability fields remain identical to the native response.

Some clients call Emby's standard static-video endpoint with a media-source ID. `NativeVideoStreamProcessor` validates that request before native upstream retrieval begins:

1. the method is GET or HEAD and `Static=true`;
2. the requested item and exact media-source ID both resolve;
3. the source owner belongs to a participating library and uses a local `.strm` file;
4. the static source requires no open token, dynamic opening flow, or additional HTTP headers;
5. the static source address exactly equals the current STRM value;
6. the current Emby authorization identity binds the temporary playback ticket;
7. the same HTTP request, Range context, and response pipeline execute through `GatewayService`.

A standard static request satisfying every condition enters the gateway. Remaining requests continue through Emby's native method. All integration points share ticket, permission, source-fingerprint, redirect-trust, and transport implementations. Routing decisions use request and media-source properties only.

After Emby creates server-side transcode state, `TranscodeInputProcessor` handles the individual job before `StartFfMpeg` executes:

1. it resolves the item and exact media-source ID from the job request and requires that ID to equal the job's current media-source ID;
2. it confirms that both the requested item and source owner belong to participating libraries and that the source owner is a local `.strm` file;
3. it rereads the current STRM and verifies that both the host static source and job media source represent that same source;
4. it issues a memory-only ticket for the job;
5. it obtains the runtime local API Origin through Emby's `GetLocalApiUrl(IPAddress.Loopback)` and combines it with the active API path prefix to create an absolute loopback gateway URL;
6. it changes only the current `StreamState` media-source clone, media paths, and protocols so FFmpeg reads from the gateway;
7. any route-generation or state-write failure restores the original job state and revokes the ticket.

This entry point does not modify library items, STRM files, persisted media sources, or native PlaybackInfo paths. Non-STRM and local media, out-of-scope libraries, unmatched media sources, and `Native` mode retain Emby's native transcode input.

### 6. Ticket model

Tickets are Base64URL bearer capabilities backed by 256 random bits. Their memory-only payload includes:

- ticket scope;
- item and media-source identity;
- current source identity;
- upstream URI;
- runtime generation;
- optional HMAC user binding;
- issue, preview, active and absolute-expiry timestamps;
- HLS nesting depth.

Playback tickets use a ten-minute preview window and extend during authorized redemption according to media runtime plus reconnect grace, capped at 24 hours. HLS child tickets inherit the root playback bounds. Repeated references to the same absolute HLS resource under one playback ticket reuse one child ticket. Revoking the root revokes its children.

### 7. Gateway authorization

`GatewayService` performs these checks before opening an upstream connection:

1. route filename and ticket syntax;
2. ticket scope and root/child relationship;
3. enabled mode and initialized runtime;
4. optional authenticated user's playback permission and ticket binding;
5. runtime generation;
6. current participating-library membership and authenticated item visibility;
7. unchanged STRM file identity.

External players may redeem a high-entropy ticket without forwarding Emby authentication. When Emby authentication is present, the user binding is verified. Validation failures return one uniform unavailable response.

### 8. Redirect policy

The gateway follows redirects itself with automatic client redirects disabled. Every hop passes `RedirectPolicy` before connection.

Accepted targets:

- use HTTP or HTTPS;
- contain no userinfo, fragment or control characters;
- preserve HTTPS when the current hop uses HTTPS;
- remain on the current host or match an administrator trust rule;
- remain inside the configured redirect-hop limit.

Relative redirects resolve against the current hop. HLS manifest references pass through the same policy before child-ticket issuance.

### 9. Transport behavior

The gateway forwards a fixed request-header set: `Range`, `If-Range`, `If-None-Match`, `If-Modified-Since`, `Accept`, `Accept-Language` and `Cache-Control`. It preserves the actual playback User-Agent, requests identity encoding and uses no shared cookie jar.

The classifier recognizes HLS from MIME type, effective `.m3u8` path or the `#EXTM3U` content signature. Prefix inspection is replayed to the consumer, so classification consumes no media bytes.

Mode behavior:

| Mode | Result |
| --- | --- |
| `Native` | Native PlaybackInfo and standard-video behavior remain unchanged. |
| `RedirectOnly` | The gateway returns the validated effective address as HTTP 302. |
| `Adaptive` | HLS is rewritten and relayed; other responses are streamed through the gateway. |
| `RelayOnly` | Validated responses are streamed through the gateway. |

Ordinary relay preserves status, content type, content length when valid, byte-range metadata, validators and safe content disposition. Responses carry `private, no-store`, `Pragma: no-cache`, `nosniff` and `no-referrer` headers. Request cancellation and an idle read timeout remain active for the full response body.

### 10. Redirect leases

A successful redirected file request creates a memory-only lease keyed by a digest of:

- playback ticket;
- normalized GET or HEAD method;
- normalized User-Agent;
- `Accept` and `Accept-Language`.

Range values stay outside the key so nearby byte requests can share one validated target. A keyed single-flight gate merges simultaneous first resolutions. Each cached target is policy-validated again before use, expires after 30 seconds and is evicted on expiry, timeout, transport rejection, or status 401, 403, 404 or 410. The same statuses on the final target of a fresh redirect chain release that response. Both cases allow one resolution from the STRM source and return the second result directly. Runtime invalidation uses a generation check so late in-flight resolutions cannot repopulate cleared leases.

### 11. HLS relay

The gateway reads manifests within these bounds:

- 2 MiB encoded content;
- 20,000 lines;
- 10,000 URI references;
- nesting depth 8.

It rewrites media lines and `URI=` attributes to child gateway routes:

```text
{api-path-base}/StrmBridge/Playback/v2/{root-ticket}/hls/{child-ticket}/resource{extension}
```

Master playlists, media playlists, segments, keys, maps, audio and subtitle resources use the same authorization, source-integrity, redirect-policy and transport pipeline.

### 12. Media-information extraction

Extraction selects STRM items only from participating libraries. It uses Emby's media-source and probe APIs, keeps embedded video/audio/subtitle streams, preserves existing external streams, and writes the result through Emby's media-stream repository.

Persistent snapshots contain technical fields only:

- container, size, bitrate and runtime;
- default audio and subtitle indexes;
- bounded video, audio and subtitle stream fields.

Image and attachment streams stay outside snapshots. URLs, paths, headers, credentials, signatures, titles, library names and user names stay outside snapshots.

`Only extract missing media information` skips complete unchanged items. Clearing that option performs a fresh remote probe and replaces the current technical snapshot. The maintenance clear operation removes stored snapshots and plugin-written internal stream data for the selected scope while preserving external streams and media files.

### 13. Lifecycle and capacity

Runtime state has a monotonically increasing generation. Sensitive configuration changes and shutdown clear tickets, redirect leases, detected runtime hosts and pending work. PlaybackInfo commits, standard static-request handoff, and gateway redemption require the current generation.

Capacity limits:

- playback tickets: 4,096;
- HLS tickets: 20,000;
- redirect leases: 4,096;
- relay concurrency: 1–16;
- redirect hops: 1–8;
- gateway timeout: 10–180 seconds;
- extraction concurrency: 1–2;
- extraction timeout: 30–180 seconds.

Gateway saturation returns 503 with a short retry hint. Upstream idle timeout returns 504. PlaybackInfo mapping, patch, or ticket failures retain Emby's native response. A standard static request keeps native execution until all matching checks complete.

### 14. Persistence and privacy

Persistent files live below Emby's plugin configuration directory:

```text
Emby.StrmBridge/
├── identity.key
├── mediainfo/
│   ├── <hmac-storage-key>.json
│   └── <hmac-storage-key>.json.bak
└── state/
    ├── extraction-state.json
    └── extraction-state.json.bak
```

Writes use uniquely named temporary files and atomic replacement. Serialization sizes, item counts and object graphs are bounded.

Logs use fixed event names with exception type, ABI version, target count, aggregate count and shortened item identity where needed. URLs, paths, hosts, query values, signatures, headers, User-Agent values, tickets, titles, library names and user names stay outside plugin logs.

### 15. Release acceptance

A release candidate satisfies all of these checks:

- Release build completes without warnings;
- formatting, unit, integration, privacy and package tests pass;
- the package contains one plugin DLL, current documentation and license notices;
- the embedded Harmony assembly has the expected name and version, and its NuGet package matches the locked content hash;
- GET and POST PlaybackInfo preserve source identity and emit relative gateway routes on Emby 4.9.5.x;
- standard static-video GET and HEAD route exact STRM media-source matches through the same gateway while other requests retain native execution on Emby 4.9.5.x;
- exact STRM transcode jobs use Emby's runtime-derived loopback gateway input before FFmpeg starts, while other jobs retain native input;
- direct-body, redirected file, Range/HEAD, seek, reconnect and HLS playback pass the host matrix;
- local, remote, HTTPS proxy and API path-prefix access reuse the client's Emby Origin;
- release artifacts contain no fixture secrets, personal paths or vendor-specific player routing.

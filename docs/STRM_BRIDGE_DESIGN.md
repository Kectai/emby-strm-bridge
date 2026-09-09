# STRM Bridge 设计规范 / STRM Bridge Design Specification

[中文](#中文) | [English](#english)

本文描述 `0.2.3` 的实现契约。安装与操作见[INSTALL.md](INSTALL.md)，当前验证证据及发布验收见[TESTING.md](TESTING.md#release-readiness)。

---

## 中文

### 1. 产品目标

STRM Bridge 是运行在 Emby Server 进程内的轻量插件。它为指定媒体库中的本地 `.strm` 项目提取音频或视频技术信息，并只为视频 STRM 提供播放路由：

1. 使用 Emby 的媒体探测与存储接口提取并持久化技术媒体信息；
2. 在保持 Emby 项目、媒体版本、权限和客户端播放流程的基础上，为 HTTP(S) STRM 来源提供安全、稳定、可定位的播放路由。

插件使用现有 Emby Origin。内网、外网、域名、反向代理和 Emby URL Base 均由客户端当前连接决定。插件生成相对客户端路由，并仅为 Emby 进程内 FFmpeg 生成回环地址。

### 2. 功能范围

播放接管要求媒体同时满足以下条件；媒体信息提取另支持音频 STRM，见第 18 节：

- 项目属于管理员明确选择的媒体库；
- 项目类型为视频；
- 项目对应本地普通 `.strm` 文件；
- 文件内容是一个合法 HTTP(S) URL；
- Emby 请求中的项目、媒体源和当前 STRM 来源保持一致。

媒体库选择留空表示处理范围为空。

插件保留 Emby 原生职责：

- 媒体库组织与元数据刮削；
- 用户认证、项目可见性和播放权限；
- 媒体版本选择；
- 客户端能力协商；
- Direct Play、Direct Stream、转封装和转码决策；
- 播放会话与进度上报。

### 3. 支持基线

当前运行与构建基线为：

- Emby Server `4.9.5.x`；
- Emby 4.9.5 自带的 FFmpeg `5.1-emby`；
- 插件目标框架 `netstandard2.1`；
- 单 DLL 安装包，`Lib.Harmony 2.4.2` 作为校验后的回退资源嵌入。

播放补丁安装前执行 ABI 校验，覆盖五处生命周期集成、六个方法：PlaybackInfo GET/POST、标准静态视频、`StartFfMpeg`、`FfmpegRunner.Start`、`DeletePartialStreamFiles`。校验内容包括类型、方法签名、程序集版本、关键命令模型属性和清理所需的作业／文件系统成员。ABI 不匹配时，播放路由进入 `Native` 行为，媒体信息提取和维护任务继续可用。

生产程序集不静态引用 `0Harmony`。启动先按 Harmony 2.x 所需 API 结构检查所有已加载程序集，并使用每个候选自己的 detour 注册表区分实际活动的 MonoMod 实现；公共 patch 元数据可能跨 Harmony 程序集共享，不能单独证明 detour 归属。唯一活动候选优先；没有活动候选但只有一个兼容实现时复用该实现；多个活动候选或多个非活动候选均因歧义失败关闭；零候选才加载内嵌回退。选定后，六个补丁的创建、所有权检查、prefix 诊断和按 `PatchId` 卸载始终通过同一个反射适配器完成。选择逻辑不包含插件名、平台、架构、客户端或来源规则，也不修改外部 MonoMod 开关。

### 4. 研究依据与已验证事实

#### 4.1 Emby 官方流程

Emby 的 PlaybackInfo 响应包含 `MediaSources`。客户端依据媒体源的容器、码率、流信息和播放能力选择 Direct Play、Direct Stream 或 Transcode。`MediaSource.Id`、`Path`、`DirectStreamUrl`、`TranscodingUrl`、`RunTimeTicks` 和 `RequiredHttpHeaders` 均参与播放流程。

静态视频接口以 `static=true` 提供原始文件字节，客户端通过 HTTP Range 自行定位。转码播放通过 `StartTimeTicks` 开始新的服务器转码任务。

参考：

- [Emby Playback Guidelines](https://dev.emby.media/doc/restapi/Playback-Guidelines.html)
- [Emby Video Streaming](https://dev.emby.media/doc/restapi/Video-Streaming.html)
- [Emby PlaybackInfo API](https://dev.emby.media/reference/RestAPI/MediaInfoService/getItemsByIdPlaybackinfo.html)

#### 4.2 Emby 4.9.5 运行时调用链

对 Emby 4.9.5.0 本机程序集的只读检查确认以下调用链：

```text
PlaybackInfo GET/POST
  -> Emby 构造原生 PlaybackInfoResponse
  -> 客户端选择精确 MediaSource

静态播放
  -> BaseProgressiveStreamingService.ProcessRequest
  -> static=true 时从 StreamState.DirectMediaPath 返回可 Range 的静态响应

服务器转码/HLS
  -> BaseStreamingService.GetState
  -> AttachMediaSourceInfo
     -> MediaPath = EncoderPath ?? Path
     -> MediaProtocol = MediaSource.Protocol
     -> RemoteHttpHeaders = RequiredHttpHeaders
  -> BaseStreamingService.StartFfMpeg
  -> GetCommandLineArguments
  -> FfmpegRunner.Start
  -> ValidateEncoderOutput
```

`StartFfMpeg` 在命令构造后创建由调用取消令牌和 15 秒启动令牌组成的链接取消令牌。`FfmpegRunner.Start` 必须在该窗口内产生 Emby 认可的首个输出。硬件转码启动失败时，Emby 可再执行一次软件转码尝试。

动态 HLS 拖动会停止不匹配的旧转码任务，以目标分片推导新的 `StartTimeTicks`，随后启动新的 FFmpeg 任务。因此每次拖动都是独立作业边界，插件状态必须按作业隔离。

15 秒是已验证的 Emby 4.9.5 ABI 行为。实现把它作为验收上限，不把该数值当作跨版本 API 保证。

#### 4.3 FFmpeg HTTP 与定位流程

FFmpeg 5.1 的 HTTP 协议在资源可定位时发送 `Range: bytes=<offset>-`。发生 `avio_seek` 时，HTTP 层重新建立连接；若当前地址来自重定向，FFmpeg 会先恢复原始输入 URI，再应用其协议上下文中的重定向缓存。

FFmpeg 5.1 接受 301、302、303、307 和 308。307 保持请求方法。重定向缓存依赖有效期限；301 和 308 在缺少显式期限时被视为长期有效。插件为 DirectClient 把合法来源重定向统一为带有界私有期限的 302，使同一协议上下文中的 Range 能稳定复用首跳；ServerFfmpeg 采用独立的短期 307 作业绑定。

MPEG-TS/M2TS demuxer 提供 `read_timestamp`。通用定位器通过时间插值、二分和必要的线性搜索反复调用它；每次调用可能触发一次 `avio_seek` 和新的 HTTP Range。远程 TS/M2TS 的一次时间定位因而可能产生多次 CDN 请求。

FFmpeg 的输入级 `-ss` 先定位到目标之前的可用位置，转码时再解码并丢弃到精确目标；输出级 `-ss` 顺序解码并丢弃。插件的快速定位先把 HTTP 输入放到经验证的字节锚点，再以短距离输出级 `-ss` 完成精确定位。

参考：

- [FFmpeg command documentation](https://ffmpeg.org/ffmpeg.html)
- [FFmpeg protocol documentation](https://ffmpeg.org/ffmpeg-protocols.html)
- [FFmpeg 5.1 HTTP source](https://ffmpeg.org/doxygen/5.1/http_8c_source.html)
- FFmpeg 5.1 `libavformat/seek.c` 与 `libavformat/mpegts.c`

#### 4.4 HTTP 语义

播放网关遵循字节范围、重定向和缓存语义：

- Range 使用零起点、首尾均包含的字节区间；
- 206 必须携带与请求和正文一致的单段 `Content-Range`；
- 416 使用 `Content-Range: bytes */<length>` 表示当前表示长度；
- 307 保持 GET 或 HEAD 方法；
- 来源响应的 `no-store` 不进入通用服务端 HTTP 缓存；管理员显式设置的 DirectClient 票据级 Location 复用是独立、有界的应用状态；
- DirectClient 正 TTL 响应使用私有 `max-age`、`must-revalidate`、`Expires` 和画像 `Vary`，0 秒或不足一秒时使用 `private, no-store`；
- 插件的作业绑定属于同一播放动作内的应用状态，仅存在于内存，并以独立回环路由返回短期私有 307。

参考：

- [RFC 9110: HTTP Semantics](https://www.rfc-editor.org/rfc/rfc9110.html)
- [RFC 9111: HTTP Caching](https://www.rfc-editor.org/rfc/rfc9111.html)

### 5. 总体架构

```text
                    ┌──────────────────────────┐
                    │ Emby PlaybackInfo        │
                    │ 原生媒体源与权限结果       │
                    └────────────┬─────────────┘
                                 │ 后置处理
                                 ▼
┌───────────────┐       ┌──────────────────────────┐
│ Emby/第三方客户端 │──────▶│ 相对播放网关路由 + 能力票据 │
└───────────────┘       └────────────┬─────────────┘
                                     │
                      ┌──────────────┴──────────────┐
                      ▼                             ▼
             客户端直放控制面                 Emby FFmpeg 控制面
             实际 UA + Range                  回环作业票据 + 实际请求画像
                      │                             │
                      └──────────────┬──────────────┘
                                     ▼
                         ┌────────────────────────┐
                         │ GatewayTransport       │
                         │ 首跳或服务端逐跳校验     │
                         └────────────┬───────────┘
                                      ▼
                         ┌────────────────────────┐
                         │ 最终来源 / CDN          │
                         └────────────────────────┘

服务器 TS/M2TS：
StartFfMpeg 前准备 ──▶ 有界时间—字节目标计划 ──▶ 作业精确绑定
                                      │
                                      ▼
FfmpegRunner.Start 前纯内存命令变换 ──▶ offset + 非搜索输入 + 短预滚
```

系统分为五个边界：

1. **选择边界**：确认媒体库、项目、媒体源和 STRM 来源；
2. **授权边界**：签发和兑换仅内存高熵票据；
3. **解析边界**：DirectClient 快速交接校验权威来源的首个 `Location`；需要服务器读取的路径按实际请求画像逐跳解析并验证来源；
4. **传输边界**：选择直达、清单改写、中继或原生；
5. **定位边界**：为服务器 TS/M2TS 任务生成可证明的字节锚点。

`GatewayTransport` 统一负责 DirectClient 的来源首跳交接，以及 Adaptive HLS 改写／中继、其他中继、媒体信息探测和服务端 FFmpeg 的逐跳 HTTP 处理；媒体信息提取不再保留独立的首跳预检通道。`TranscodeJobCoordinator` 管理符合条件的启动冷却、临时作业资源和插件作业输出目录所有权。

### 6. 播放模式

| 模式 | 普通文件客户端直放 | 服务器 FFmpeg 普通文件 | HLS | 用途 |
| --- | --- | --- | --- | --- |
| `Adaptive` | 已知文件交接权威来源首个重定向；未知类型首次完整跟随分类 | 作业回环路由短期交接最终地址 | 清单中继改写，资源按能力交接或中继 | 默认模式 |
| `RedirectOnly` | 全部 DirectClient 交接权威来源首个重定向 | 作业回环路由交接最终地址 | 不改写，客户端按来源重定向处理 | 来源与请求方可直接互通 |
| `RelayOnly` | Emby 流式中继 | Emby 流式中继 | 清单和资源均中继 | 请求方无法访问来源或来源要求服务器上下文 |
| `Native` | Emby 原生 | Emby 原生 | Emby 原生 | 诊断与完整原生行为 |

`Adaptive` 的核心规则：

- 普通文件的媒体正文由实际读取方直接访问最终来源；
- Emby FFmpeg 仍负责需要服务器处理的转封装和转码；
- HLS 清单由插件改写，以保持密钥、初始化段、字幕和子清单的授权边界；
- 传输能力不足或包含不可安全交接的敏感跨主机请求头时，当前资源使用流式中继；
- 已知普通文件的 DirectClient 在首次重定向交接时打开权威 STRM 来源，接受 301/302/303/307/308，校验首个 `Location` 后统一输出 302；插件不连接该媒体目标。正的 `DirectRedirectCacheSeconds` 允许后续网关请求在同一播放票据／请求画像内直接复用该已校验、未读取的地址；0 秒则每次重新打开来源；
- 未知重定向类型的首次缓存缺失完整跟随并分类；确认 `FileBody` 后，同一请求在同一 direct gate 与已经计时的 header budget 内重新打开权威来源，只交付未被分类消耗的新鲜首跳，再按配置写入票据级行为／地址状态。若已知文件提示或刷新首跳明确为 `.m3u8`，Adaptive 推翻文件判断、清除已有普通文件行为／首跳状态，并改走无缓存完整跟随／HLS 改写；
- 每张 PlaybackInfo 播放票据拥有独立的普通文件行为状态与在途控制状态；新票据不继承旧票据状态，同时遵守同一来源画像的共享退避；
- 授权、资源缺失、限流和服务器错误作为当前请求结果返回，并按 §14.3 更新独立的来源失败协调状态，错误响应不进入行为决策或媒体正文缓存；
- 决策按资源和作业完成，不写入播放器专用分支。

### 7. STRM 来源与身份

`StrmSourcePolicy` 只接受：

- 本地绝对路径；
- 扩展名为 `.strm`；
- 文件本身为普通文件，文件及祖先目录均不经过重解析点；
- 文件最大 16 KiB；
- 严格 UTF-8，可带 BOM；
- 恰好一个非空 HTTP(S) URL；
- 读取前后的大小和修改时间一致。

来源身份包含：

- HMAC 派生的存储键；
- HMAC 派生的来源指纹；
- STRM 文件大小与修改时间；
- 运行时代际。

原始 URL、查询参数、本地路径、标题、媒体库名和用户名不进入身份日志或持久化键。

### 8. PlaybackInfo 集成

插件在 Emby 完成 GET/POST PlaybackInfo 后处理响应。每个候选媒体源依次验证：

1. 请求项目可解析；
2. 媒体源属于该项目；
3. 项目位于参与媒体库；
4. 项目是本地 STRM；
5. Emby 静态媒体源与当前 STRM URL 完全一致；
6. 当前用户具备项目可见性和播放资格；
7. 运行时代际保持有效。

处理成功时：

- 克隆当前 `MediaSourceInfo`；
- 保留媒体源数量、顺序、ID、名称、Path、ProbePath、流信息、默认流索引、时长和播放能力；
- 仅把 `DirectStreamUrl` 指向相对网关路由；
- 签发记录项目、媒体源、来源、可选用户绑定、用于会话／租约隔离的设备摘要和代际的客户端票据；
- 原子替换完整媒体源数组。

相对路由格式：

```text
{当前 API 路径前缀}/StrmBridge/Playback/v3/{ticket}/stream{extension}
```

相对路由使客户端自动沿用当前 Emby Origin。插件不生成固定 LAN 地址、公网地址或反向代理地址。

### 9. Emby 标准静态接口集成

部分客户端直接调用 `/Videos/{Id}/stream.*?static=true`。插件在 Emby 打开原始 STRM 前验证：

- 方法为 GET 或 HEAD；
- `static=true`；
- 项目和精确 `MediaSourceId` 一致；
- 来源属于参与媒体库；
- 当前 STRM 未变化；
- 媒体源是无需动态打开的普通静态来源；
- 当前请求已通过 Emby 授权；可用的用户标识用于核对授权绑定，设备摘要只用于会话与租约隔离。

通过后，同一 HTTP 请求、Range 和响应管线交给播放网关。其他请求继续执行 Emby 原生方法。

客户端使用 `DirectStreamUrl` 或标准 `stream.*` 接口均可进入同一处理管线；标准接口适配在进程内调用网关，不额外发起一次到插件 URL 的 HTTP 请求。DirectClient 接受权威来源的 301/302/303/307/308，校验 `Location` 后统一输出 302。正 TTL 响应添加私有 `max-age`、`must-revalidate`、`Expires` 与 `Vary: User-Agent, Accept, Accept-Language`；0 秒响应使用 `private, no-store`。播放器可在期限内直接复用该重定向，也可在到期或主动刷新后重新请求原 Emby 入口。

标准接口复用第 6 节的模式决策和第 14.2 节的首跳缓存契约，不额外读取已经交接的媒体目标。诊断时分别统计来源解析、目标打开与实际媒体读取；出现 `stream.*` 请求或特定后缀不等于中继或转码，应结合 `Static`、Emby 协商结果和服务器作业判断。

稳定的 `Location` 可以减少来源解析和连接抖动，但 DirectClient 交接后，客户端的 DNS、后续重定向、CDN 状态、读取取消和连接并发都不再受插件控制。插件不能合并或限制播放器自行建立的 CDN 连接。`RelayOnly` 使媒体正文经 Emby 转发并接受传输容量保护，但没有缩略图与主播放优先级调度，降低并发可能产生 503。预览读取导致卡顿时的临时方案见[缩略图兼容说明](COMPATIBILITY.md#client-generated-seek-thumbnails)。

### 10. 服务端 FFmpeg 作业交接

#### 10.1 三段边界

服务端作业分为控制准备、网关解析和命令执行三个边界。

**控制准备**位于 `BaseStreamingService.StartFfMpeg` 入口，发生在 Emby 构造 FFmpeg 命令和创建 15 秒启动令牌之前。它可以执行有界网络 I/O：

1. 解析项目和精确媒体源；
2. 验证媒体库、STRM 和媒体源一致性；
3. 为 TS/M2TS 非零起播准备绑定强 ETag、完整长度与实际探测画像的目标定位计划；
4. 签发服务端作业票据；
5. 生成 Emby 回环网关 URL；
6. 原子更新当前 `StreamState` 副本。

后续拖动或重试复用已改写的媒体源时，入口核验回环地址、有效服务端票据、用户、项目、媒体源、STRM 文件版本及运行时代际。核验通过后，为当前目标签发新的作业地址并绑定对应计划。同一目标和视频轨可复用准备缓存，旧作业地址保留原有绑定供在途请求完成。入口依据票据身份识别已路由来源，匹配规则对媒体源对象的复制保持有效。每次启动尝试精确拥有本次临时票据和快速定位绑定：准备、登记、启动失败或取消时立即回收；启动成功后保留到确认该作业不再活动的清理阶段，并以幂等方式只回收一次。旧作业清理不撤销后来占用同目录的新活动作业资源。

**网关解析**发生在 FFmpeg 第一次访问回环 URL 时。网关直接读取该请求真实携带的方法、User-Agent、`Accept`、`Accept-Language` 和 Range，随后逐跳解析并验证来源。可直接读取的普通文件返回短期 307，HLS 和不能安全交接的响应进入对应中继策略。

**命令变换前缀**位于 `FfmpegRunner.Start` 入口。它只允许：

- 读取内存作业绑定；
- 校验命令结构和精确输入 URL；
- 原子应用已准备的命令字段；
- 记录不含敏感数据的结构化结果。

命令变换前缀只使用短时内存锁完成状态读取与原子提交；DNS、HTTP、文件读取、等待 I/O／异步任务及新计划计算均位于前缀之外。后缀观察原启动任务，宿主返回 false 且已完成失败进程停机后可恢复完整原命令并在同一启动预算内提交一次原生重试；提交受代际和取消检查保护，任务等待在锁外进行。

#### 10.2 实际请求画像

服务端最终地址解析以 FFmpeg 到达回环网关的实际请求为准。缓存画像包含归一化的方法、User-Agent、`Accept` 和 `Accept-Language`。Range 不进入复用键，使同一画像的邻近 Range 可以共享解析结果；插件每次实际打开或复用最终地址租约时仍验证 `206 Content-Range`、长度和表示一致性。307 交接后由 FFmpeg 直接产生的读取不经过插件，不在该逐次验证承诺内。

插件不向 `MediaSourceInfo.RequiredHttpHeaders` 写入推测的 FFmpeg 请求头，也不猜测 FFmpeg 的默认 User-Agent。只有应用已验证快速定位计划的作业，才通过实际 FFmpeg HTTP 命令属性明确设置探测 User-Agent、`Accept-Encoding: identity` 和绑定强 ETag 的 `If-Match`。原命令已有自定义 User-Agent 或请求头时保留完整原生命令，不覆盖其画像。

快速定位使用无凭据探测画像，样本必须具有一致的强 ETag 与完整长度，计划同时保存这些表示约束。正式输入在交接前必须匹配计划的 ETag、长度及请求条件；数值偏移不能脱离这些约束跨画像使用。无强 ETag、弱 ETag、样本变更或命令画像冲突时保留原生定位。探测和正式请求的最终地址仍分别在当前画像及作业作用域内解析，不把探测地址写入命令。

插件接管范围限定为 `RequiredHttpHeaders` 为空、无需动态打开的静态 STRM 媒体源。需要 `Authorization`、`Cookie`、`Referer`、代理认证或其他自定义来源请求头的媒体源保留 Emby 原生流程，避免凭据跨主机转发。

#### 10.3 回环路由

回环地址通过 Emby 的 `GetLocalApiUrl(IPAddress.Loopback)` 和当前 API 路径前缀生成。该地址只供同一 Emby 进程启动的 FFmpeg 使用。

作业身份由 `TicketStore` 中的能力票据承载，包含用途、项目和媒体源 ID、可选用户绑定摘要、用于会话／租约隔离的设备摘要、STRM 来源与代际，以及签发时间、当前有效期、绝对最长有效期和播放寿命。`FastSeekCoordinator` 另以精确作业回环 URL 绑定可选数值计划。票据和绑定只保存在内存；票据也保留后续打开所需的原始来源 URI。它们由对应 `TranscodeJobCoordinator` 尝试持有并按上述启动／结束生命周期精确回收，不依赖最长票据期限兜底积累。

最终 URI 位于按实际请求画像隔离的短期内存租约中，不属于控制准备产生的作业绑定，也不进入日志、配置、快照、命令行或持久化文件。

#### 10.4 FFmpeg 短期重定向绑定

FFmpeg 第一次访问回环路由时，插件以实际请求画像解析来源。当最终资源是可直接读取的普通文件时，回环路由返回：

- `307 Temporary Redirect`；
- `Location: <已验证最终 URI>`；
- 短期 `Expires`；
- 以 `max-age` 开头的短期私有 `Cache-Control`；
- `Referrer-Policy: no-referrer`；
- `X-Content-Type-Options: nosniff`。

响应有效期取当前票据期限、已验证来源租约期限和返回时起 20 秒上限的最小值；向下取整，不足一秒时不建立可缓存交接。FFmpeg 在同一 HTTP 协议上下文及该期限内可复用重定向，格式 demuxer 后续 Range 直接访问最终来源。期限结束或 HTTP 上下文重建后，可以再次访问网关。同一来源只有在方法、User-Agent、`Accept` 和 `Accept-Language` 一致时才复用解析租约。票据撤销只约束后续网关访问，不能撤回已经交接的读取。

网关观察到租约失效或新重定向目标拒绝时，每次上游打开最多进行一次权威重新解析，受该次打开的预算和代际约束；这是单次调用上限，不是整个 FFmpeg 作业的累计次数上限。已交接到最终地址的 FFmpeg 请求位于插件数据路径之外，其后续错误不会自动触发插件刷新或退避；只有重新到达网关的请求能应用当前网关策略。

### 11. 重定向、DNS 与 SSRF 防护

插件关闭 HttpClient 自动重定向。对插件实际跟随的服务端路径，每一跳在连接前验证：

- scheme 为 HTTP 或 HTTPS；
- URI 不含用户信息、片段、控制字符和歧义主机；
- HTTPS 来源保持 HTTPS；
- 主机相同，或匹配管理员信任规则；
- 直连的 DNS 解析结果符合允许范围；
- 直连目标与已验证地址一致，代理连接使用系统选定的代理；
- 跳数位于配置上限；
- 响应状态与本次用途兼容。

直连通过运行时 `SocketsHttpHandler.ConnectCallback` 获取 DNS 结果，先验证全部候选地址，再连接已验证的 IP；HTTP Host、TLS SNI 和证书校验仍使用原域名。直连域名解析到私网、回环、链路本地或特殊用途地址时，管理员通过 IP/CIDR 明确授权；域名通配信任仅授权域名。被拒绝的 IP 会进入检测目录，支持从界面确认。STRM 中明确填写的 IP，以及通过主机策略验证的精确 IP 重定向，构成显式地址授权。

每次请求遵循 .NET 进程的默认系统/环境代理选择及绕过规则。选定代理后，将该代理固定传给原生 HTTP 处理器，由代理处理目标解析和出口，包括 Fake-IP 环境。代理配置属于管理员信任边界，代理后的目标 IP 访问限制由代理端实施；插件实际跟随重定向时，逐跳主机信任、URI 和 HTTPS 保护继续生效。代理选择或连接失败时终止本次请求。直连与代理使用独立连接池；规范化信任规则改变后，新直连请求轮换连接池并重新校验地址，规则的等价重排不触发轮换。连接寿命有界，每个传输实例最多保存 32 个代理路由处理器并在停止时释放。代理凭据交给 .NET 处理，仅用于代理认证。

连接失败返回 502，连接阶段的地址信任拒绝返回 403，并记录安全原因代码；无效票据保持资源不可用处理。媒体地址、代理地址和凭据均保持在进程内。

信任规则支持：

- 精确 DNS 主机；
- DNS 标签边界内的通配后缀，例如 `*.example.com`；
- 精确 IP；
- CIDR。

通配后缀匹配一个或多个子域标签，不匹配裸后缀或外观相似域名。运行时检测目录有界；保存配置时，检测结果以规范化的精确主机名或 IP 写入插件配置，重启后继续展示并重新计算当前信任状态。持久化检测值不含协议、端口、路径、查询、片段或凭据。用户可从检测结果添加信任，也可手工输入通用规则；禁用插件并保存会清空展示的检测目录。

上述逐跳连接校验适用于插件实际跟随的 HLS、流式中继、媒体探测和 ServerFfmpeg 路径。DirectClient 首跳交接只连接权威 STRM 来源并校验其第一个 `Location` 的 URI、主机信任和 HTTPS 降级，不解析或连接该目标。客户端接手后续重定向后，其 DNS/IP、后续跳数、连接、读取取消和响应状态位于服务端 SSRF 与取消边界之外；部署者必须同时考虑客户端所在网络的访问策略。

### 12. HTTP Range 契约

网关接受单段 bytes Range。用于建立或刷新租约的范围响应验证：

- 状态为 206；
- `Content-Range` 单位为 bytes；
- 起点等于请求起点；
- 终点位于请求范围内；
- 完整长度为正且在同一作业内稳定；
- `Content-Length = end - start + 1`；
- 内容编码为 identity；
- ETag 或 Last-Modified 在同一表示中保持一致。

播放器直放时由客户端负责最终媒体 Range。DirectClient 首跳交接不打开 CDN，因此插件不声称校验 CDN 的 `206 Content-Range` 或表示；上面的完整 Range 契约适用于插件实际读取的 HLS、中继、探测、ServerFfmpeg 和 Adaptive 未知类型首次分类。固定文件在这些服务端路径中检测到长度或表示标识变化时，当前请求停止交付，相关服务端最终地址租约与该来源的数值定位计划失效。来源未提供验证器时，仅能核验已知长度；相同长度且没有验证器的内容变化属于上游契约的限制。

无 Range 的 200 用于完整读取或来源能力识别。单段范围请求收到 206 时，首次和重新权威解析必须经过同样的一致性门禁，后缀范围按完整长度换算；错误响应至多重试一次，仍错误时返回 502。范围请求收到合法的完整 200 或条件 304 不构造虚假的部分响应。范围请求收到不兼容的响应时，该响应不能建立已确认 Range 租约；缓存目标失配触发一次权威重新解析。当前 416 按响应返回并保留可转发的 `Content-Range`，不作为可复用租约，也不据此写入新的持久长度。它表示本次范围不可满足，不形成来源级退避。

### 13. TS/M2TS 有界快速定位

#### 13.1 目标

快速定位把远程 TS/M2TS 的 FFmpeg 格式级多次二分搜索转换为：

1. 准备阶段的少量、有界、可合并 Range 探测；
2. 执行阶段的一次字节锚点设置；
3. 从锚点到目标的短距离顺序解码。

“启用有证据的快速定位”默认开启。关闭时不准备或应用上述计划，远程 TS/M2TS 保留 Emby 原生服务端定位；普通网关路由、直达、中继和 HLS 行为不受影响。该开关按能力隔离问题，不读取或匹配客户端名称。

#### 13.2 计划键与表示一致性

目标计划键包含：

- 来源 HMAC 指纹；
- 媒体源 ID；
- Emby 选中视频轨索引及完整内部视频轨的索引/编码序列；
- 精确目标时间；
- 时长；
- 运行时代际。

`FastSeekPlan` 保存目标时间、完整长度、字节偏移、包步长、媒体时长、相对定位时长、探测次数、运行时代际、选中视频索引、到期时间及强 ETag/长度/受控画像的表示绑定。PID、PCR、估算字节率及随机访问分析属于准备时的临时证据，不作为计划字段持久保存。头部与目标样本必须返回相同强 ETag 与完整长度；STRM 来源、配置或运行时代际变化会使相关计划失效。成功计划有效期为两分钟。计划值不包含 URL、路径、标题、用户名、任意请求头、响应正文或凭据；强 ETag 作为条件请求验证器仅驻内存，不能写入插件日志或快照；精确作业 URL 单独保存在内存绑定中。

#### 13.3 包与节目识别

解析器支持 188、192 和 204 字节步长，并通过多个连续同步字节确认对齐。对齐后解析：

- TS header；
- adaptation field；
- PCR；
- `random_access_indicator`；
- PAT；
- PMT；
- PMT 中声明的视频流类型。

时间戳按 33 位回绕规则归一化。跨样本的时间与字节斜率必须为正，并与全局长度/时长估计处于有界误差内。

视频随机访问扫描维护每个视频 PID 的 PES 起点和少量尾部字节。H.264/H.265/MPEG-2 起始码跨越相邻 TS 包时，解析器把证据归属到完整访问单元的起始包，避免因为 188 字节包边界漏判或从 NAL 中部开始读取。

随机访问标记必须绑定到有效 PUSI/PES 起点。采样从 PES 中途开始、连续计数缺口或头部不完整时，解析器等待新的有效起点。

视频选择取自 Emby `StreamState.VideoStream`，与命令构造器选择 `-map` 的索引一致。Emby 的 `MediaStream` 提供索引与编码信息；FFmpeg MPEG-TS demuxer 按 PMT 声明顺序创建视频流。对单节目输入，解析器核验完整视频轨数量、编码序列及选中索引，再按视频序号关联 PMT 视频 PID 与该节目的 PCR PID。PMT 顺序保持原样，包括 PID 数值不递增、多视频和独立时钟 PID 的输入。

PAT/PMT 采用 CRC、current-next 标志、完整单节结构和 ES 长度校验。头部与目标范围中出现的节目映射必须一致，关键帧仅来自选中视频 PID。多节目、缺少匹配元数据或节目结构变化等无法建立可靠映射的输入使用原生定位，并记录 `reason=streamselection`；确定节目后缺少对应时钟时记录 `reason=pcrmissing`。命令应用前再次核对直连映射或滤镜连接中的输入视频索引。

#### 13.4 随机访问证据

锚点按以下证据选择：

1. adaptation field 的随机访问标记；
2. PMT 识别后的视频 PID；
3. H.264 IDR；
4. H.265 IRAP；
5. MPEG-2 I-picture。

分支依据标准流类型和码流结构，不依据文件名、域名、播放器或媒体标题。未识别的视频编码使用协议随机访问标记；证据不足时不生成快速定位计划。

#### 13.5 计划生成

一个未缓存的精确目标按总预算执行：

1. 读取头部样本，确定包步长、PAT/PMT、所选视频 PID 和关联起始时钟；
2. 依据完整长度、时长和四秒预滚估算目标字节；
3. 依据平均字节率把四秒时间跨度换算为目标扫描字节数；
4. 根据已测响应延迟、正文读取速率和剩余准备时间调整不超过 8 MiB 的连续 Range 分块，直到获得局部 PCR 与随机访问证据或耗尽窗口；
5. 使用实测样本之间的时间/字节斜率执行最多三次校正；已有上下界时约束到该区间，重复字节位置立即结束；
6. 在样本内选择已验证随机访问点；
7. 使用同一节目中包围锚点的 PCR 约束锚点时间，时钟区间最多 100 毫秒，整个区间均须位于目标前 0.25–12 秒；平均码率仅用于寻找窗口和校正字节位置；
8. 生成 `ByteOffset`、`AnchorTime`、`TargetTime` 和 `RelativeSeek`。

预算集中在 `FastSeekBudgetPolicy`，区分媒体码率与网络读取速度：

- 媒体平均字节率 `b = Content-Range 完整长度 / 媒体时长`，单位为字节／秒；不使用某一条视频流的码率冒充整个容器码率。
- 头部样本 `H = 512 KiB`；单个扫描窗口 `W = clamp(b × 4 秒, 512 KiB, 24 MiB)`，向下按 188／192／204 字节包步长对齐。
- 总字节预算 `B = min(32 MiB, H + W × 4)`，覆盖首次目标窗口与最多三次纠偏窗口。找到可靠锚点即停止，不要求读完预算。
- 每次候选 Range `C` 不超过 8 MiB、窗口剩余量、来源剩余量及总字节剩余量。有有效测速时，实际 Range 为 `alignDown(min(C, max(512 KiB, 0.5 × v × max(0, R − L))))`，其中 `v` 为有效样本累计正文字节／累计正文读取秒数，`L` 为最近一次打开到响应头可用的耗时，`R` 为共享期限的真实剩余秒数。

`L` 包含排队、解析、重定向及响应头等待，和正文读取分开计时；`v` 是应用层读取速率估计，套接字预取可能使其偏高，最多恢复原有 8 MiB 候选上限。缺少有效观测时保持候选 Range；预测时间不足时仍允许有界最小尝试，不仅凭预测判定失败。尾部不足 512 KiB 时优先遵守剩余量，不能扩大读取。

七秒仍是从共享准备创建时开始、单调计时且不可续期的绝对上限，32 MiB 仍是流量安全上限；它们目前保留为集中定义的代码常量，没有新增配置项，也没有声称经过全平台对比测得最优。四秒窗口来自预滚搜索跨度，四个窗口来自最多三次纠偏；0.5 系数为网络波动和本地解析留余量，512 KiB 下限避免小样本测速噪声导致过碎请求。这些是工程边界，不是媒体码率能推导出的最佳网络超时。后续若调整上限，应比较实测定位成功率、读取量和等待分位数。媒体码率只决定证据搜索量，最终位置仍由 PCR 和随机访问标记验证；高码率、长 GOP 或较慢网络下触达上限可回退原生定位。

相同来源、媒体源、完整视频选择、时长、代际和精确目标在两分钟内复用同一成功计划；并发准备使用 single-flight 合并。每个调用登记为一个 waiter：单个 waiter 离开时不影响仍在等待的调用；最后一个 waiter 在任务完成前离开时，在协调器锁内核对 pending 实例并移除，随后取消该实例的上游 Range 探测，同 target 的新调用可立即建立新实例。确定性的封装、视频选择、时钟、时间线、字节率或随机访问证据失败在同一精确键下负缓存 30 秒，使 Emby 对同一目标进行 remux/transcode 回退时不会重复探测。取消、运行时代际变化和临时网络异常不形成负缓存；实际因网络估计缩小过分块的准备失败也不作负缓存，避免把分块边界漏证据误判为媒体长期不支持。每段仍独立解析，跨段证据不足时保守回退，不拼凑未经验证的锚点。读取后、解析后和提交前检查共享取消及单调期限，超过期限的晚返回不能存入成功或失败缓存。不同目标各自执行有界准备，避免用未经验证的跨目标插值影响播放正确性。

#### 13.6 命令变换

执行阶段只处理同时满足以下条件的命令：

- 输入 URL 精确等于作业回环 URL；
- 输入协议为 HTTP；
- 输出为 Emby 预期的 segment/HLS 结构；
- 输入存在非零 `ss`；
- 时间线满足 Emby 原生 `segment_time_delta` 契约，或满足 `copyts + start_at_zero + segment_start_number` 可证明契约；
- 目标计划与输入 URL、代际和目标时间一致；
- 相关命令字段仍处于预期原值。

原子变换：

```text
HTTP offset             = plan.ByteOffset
HTTP seekable           = false
input ss                = null
output ss               = plan.RelativeSeek
output timestamp offset = plan.TargetTime
segment time delta      = 经证明的目标时间线值
```

同时显式设置经过验证的 HTTP User-Agent、identity 编码和绑定强 ETag 的 If-Match；没有这些可写 ABI 属性或原命令已存在自定义画像时不应用计划。任一属性缺失、值冲突或写入失败时恢复全部原值。命令处理器不执行网络请求。

#### 13.7 失败路径

计划缺失或证据不足时，首次作业保留回环网关输入与 FFmpeg 原生定位；实际输入读取仍按当前路由模式处理。原生定位仍由 Emby 的启动窗口验收。

已应用优化的 FFmpeg 启动返回 false（宿主已完成失败进程停机）时，在外部启动取消令牌仍有效的前提下，恢复完整命令快照，包括输入 seek、HTTP offset/seekable、输出 seek/时间轴及 HTTP 画像，禁用本次计划后至多重试一次原生启动。两次尝试共享 Emby 原启动预算，不延长截止时间，不重复应用优化，不建立无限回退。该处理同样覆盖 307 交接后来源拒绝 If-Match、错误不再经过网关的情况。宿主启动任务抛出异常时不自动重启，保留 Emby 清理路径，避免旧进程尚未停止就启动第二个进程；恢复期间的取消或配置变更禁止重试提交。

服务器启动保护适用于 `Adaptive`、TS/M2TS 候选来源、目标至少十秒，且具有设备 ID 或播放会话 ID 的作业。其他模式、来源、早期目标及两个标识均缺失的请求不进入此冷却。键以来源版本、媒体源、用户、设备、播放会话、选中视频、向下取整的秒级目标和运行时代际隔离。

同键同时只放行一个启动，等待期间重复请求收到 `503` 及一秒重试提示。成功启动清除状态；启动任务失败进入 30 秒冷却，重复请求返回剩余 `Retry-After`，不创建 FFmpeg 作业。观察到取消时，耗时不足八秒允许重试，达到八秒进入冷却；这是避免慢失败连续重启的启发式阈值，不是 Emby 15 秒令牌已触发的证明。计时包含入口准备。冷却从失败计时，拒绝不延长它。新目标、会话或来源版本独立处理；代际变更在下一次协调器访问时清除旧重试状态。

插件路由作业的输出目录使用作业所有权栅栏。每次启动在 Emby 创建输出前取得该目录的新所有权，清理通过作业注册时的媒体源快照关联所有者。延迟清理在实际删除时重新核对所有权，并与新作业取得所有权互斥；旧作业的清理跳过已复用的目录，最新作业结束后的清理负责回收目录。插件只处理自己路由且成功登记所有权的作业，使用 Emby 已提供的文件系统及精确输出目录。目录清理不依赖会被后续拖动改写的 StreamState。

清理还核对 Emby 当前是否仍有使用该路径的活动作业。未登记所有权的作业采用原生清理；不同目录分别同步。所有权记录使用弱引用，保留在途清理所需的生命周期，不能把它等同于票据缓存的即时清空。

定位诊断记录校正轮次、探测数量、剩余预算和数值时间误差。启动与清理日志记录固定原因码和等待时间，不记录源 URL、输出路径、用户或播放会话。

### 14. 缓存模型

#### 14.1 服务端最终地址租约

- 范围：来源指纹、实际资源 URI 摘要、方法、归一化 User-Agent、`Accept` 和 `Accept-Language`；HLS 各子资源单独隔离；
- 内容：最终 URI、响应能力、表示验证信息和期限；
- 存储：内存；
- 共享：请求画像完全一致的服务端作业；
- 期限与容量：租约 30 秒，最终地址租约容量上限 4,096；
- 清理：租约到期、容量淘汰、配置代际变化、插件停止、来源拒绝或表示失配。共享来源租约有独立寿命，实际请求仍须先通过自身票据校验。

#### 14.2 客户端行为／首跳决策状态

- 范围：不透明播放票据、项目、媒体源、来源、可选用户绑定摘要、设备隔离摘要和请求画像；
- 内容：已确认的 `SourceTransportBehavior`、是否应从权威 STRM 来源执行首跳交接，以及可选的已校验但插件未打开正文的首跳 `Location`。无重定向来源可复用票据本已携带的权威 URI；
- 存储：内存；
- 新 PlaybackInfo 票据始终形成新范围，不跨播放动作复用行为状态；
- Range 不进入键，使同一播放动作的邻近请求复用分类／首跳决策；
- `DirectRedirectCacheSeconds` 默认 20 秒、有效范围 0–60；正值只在当前播放票据和请求画像内复用首跳地址，实际期限不超过配置、票据和路由剩余期限；默认值用于覆盖常见起播／seek 的短时请求突发，并保持在 30 秒内部路由租约内，它是可配置的性能折中，不是由码率或响应自动推导；无法自动识别的一次性、按 Range 绑定或更短有效期地址必须配置为 0；
- Adaptive 中已由媒体源元数据确认的普通文件，以及 RedirectOnly 中全部 DirectClient，在首次解析或强制刷新时打开权威 STRM URL；插件接受来源 301、302、303、307 或 308，校验首个 `Location` 后统一以 302 交接，不打开目标正文；
- 无重定向的直接 200/206 来源首次确认后可建立 30 秒免预读状态；后续命中直接生成指向权威来源 URI 的 302，插件源站和 CDN 打开数均为零；
- Adaptive 未知重定向类型的首次缓存缺失仍完整跟随全部重定向、读取必要前缀并分类。确认 `FileBody` 后，同一请求在原 direct gate 和剩余 header budget 内重新打开权威 STRM URL，只保存并交接未被分类消耗的新鲜首跳；成功提交后才写入行为／首跳决策；
- 若已知文件提示或刷新取得的首个 `Location` 明确为 `.m3u8`，Adaptive 推翻 `FileBody` 判断，清除已有普通文件行为／首跳状态，禁止继续刷新循环，并无缓存地完整跟随和执行 HLS 改写；RedirectOnly 仍按模式保留首跳；
- 正 TTL 的 302 响应添加 `Cache-Control: max-age=<实际秒数>, private, must-revalidate`、RFC 1123 `Expires` 与 `Vary: User-Agent, Accept, Accept-Language`；0 秒或剩余期限不足一秒时添加 `private, no-store`；
- `DirectRedirectCacheSeconds=0` 时不保存首跳地址，每次网关请求都向权威来源取得新 `Location`，用于一次性签名地址；
- 客户端 `Cache-Control: no-cache`、`no-store`、`max-age=0` 或 `Pragma: no-cache` 会绕过尚未到期的首跳地址并重新解析；
- 首跳地址仅进入有界票据级内存状态，不进入配置、恢复快照、日志或持久化，插件也不为 DirectClient 首跳快捷路径向该地址发出 GET；
- 权威来源本身的错误不写入行为状态；下一张票据有独立状态，但同来源画像的退避仍适用。

客户端和插件共享同一有界 DirectClient 期限：客户端可在私有缓存期限内直接使用 `Location`，再次访问网关时也可命中同一票据级地址。插件内存项会随 TTL、配置代际或停机清理；已返回客户端的私有缓存由客户端自行管理，插件无法主动清除，`must-revalidate` 只约束期限后的复用。稳定地址降低重复解析和连接抖动，但两者都不合并播放器随后对媒体目标发出的 Range，也不让插件观察、取消或限制客户端侧连接。

标准静态视频路由按用户、设备、媒体源和 `PlaySessionId` 复用同一播放票据。缺少会话参数时，可关联同用户、设备最近一次 PlaybackInfo 生成的票据；新 PlaybackInfo 更新该关联。无法建立可靠关联的请求使用请求级票据，普通响应交接后释放，HLS 根票据保留供子资源使用。原生路由关联以摘要保存，随票据清理，容量由票据上限约束。

#### 14.3 来源失败协调

- 范围：来源 URI 的单向摘要、方法和归一化 User-Agent；Range、票据、用户、设备和最终 URI 不进入键；
- 内容：最近一次可退避状态、连续失败次数、恢复时间和状态保留期限；
- 存储：有界内存，不保存原始 URI、响应正文或凭据；
- 同一来源画像的上游打开串行协调，并在进入中继容量计数前等待；失败后的并发等待者共享退避结果，正常成功请求仍可能各自验证其范围；
- 服务端最终地址候选被拒绝时，本次打开只执行一次权威 STRM 刷新；刷新后仍为 `401/403/404/408/410/429/5xx` 时启动来源级退避，权威来源直接返回这些状态也适用；
- 无 `Retry-After` 时按 2、4、8、16、30 秒退避；有效 `Retry-After` 优先使用并限制在五分钟内；
- 收到上述集合以外的响应会清除失败状态，包括成功响应；连续失败计数保留至本次退避结束后五分钟，再按访问和容量清理；
- 退避命中保留原状态码并返回剩余 `Retry-After`，不占用上游或中继并发槽；
- 已知来源退避淘汰同画像的客户端行为／首跳决策，恢复后必须重新请求权威 STRM 来源；
- 播放器跟随重定向后直接访问来源产生的响应不经过插件，插件无法根据该响应建立退避状态。

播放及探测网关的连接异常与 `408` 进入各自的有界失败处理；网络连接异常对外归类为 502。请求主动取消、运行时代际取消和地址信任拒绝不写入网关来源故障状态。媒体信息协调器另行保存 30 秒至 24 小时的远程探测退避。

#### 14.4 TS/M2TS 目标计划

- 范围：同一稳定来源、媒体源、完整视频选择、时长、代际和精确目标；
- 内容：时间—字节锚点、协议元数据、强 ETag、完整表示长度和受控请求画像；
- 存储：有界内存；
- 共享：不同用户和作业可共享相同来源/视频选择的候选计划，但正式使用必须建立相同表示与请求条件，不因计划仅含数字而跳过校验；
- 失效：两分钟到期、表示验证失败，或来源指纹、媒体源、视频选择、时长、目标或代际变化；
- 确定性失败在同一精确键下保留 30 秒；瞬态网络异常及取消不进入该负缓存。

#### 14.5 媒体正文

`Adaptive` 与 `RedirectOnly` 的普通文件不建立插件媒体正文缓存。`RelayOnly` 和需要中继的资源采用有界流式转发，不落盘。

### 15. HLS

HLS 清单按可变资源处理，正常的 ETag、长度、媒体序列和签名 URI 更新不触发固定文件表示变更错误。清单改写前取得完整正文，必要时移除范围及条件头重新 GET；改写后的完整响应使用 200，移除上游 Content-Range、Accept-Ranges、ETag 和 Last-Modified。HEAD 返回与完整改写表示一致的元数据且不发送正文。文件与分片的字节范围中继保持原样。

上游请求声明 `Accept-Encoding: identity`。对插件实际打开并分类的正文，除 `RedirectOnly` 外，GET 等响应若仍返回非 identity 的 `Content-Encoding`，网关会在内容分类前保守拒绝，不会把编码正文作为普通文件中继。在这些服务端读取路径上，该规则同时覆盖已声明的 HLS 类型和 `application/octet-stream` 等不透明类型，防止清单借编码绕过改写。HEAD 不交付正文，继续透传上游 `Content-Encoding`。

HLS 清单最大编码体积 2 MiB、最多 20,000 行、最多 10,000 个 URI、最大嵌套深度 8。插件改写：

- 主清单与媒体清单；
- 分片；
- `EXT-X-MAP`；
- `EXT-X-KEY`；
- 音频、字幕和 iframe 资源；
- 相对 URI 和绝对 URI。

每个资源获得绑定根票据、父资源、深度和绝对 URI 的子票据。同一根票据内相同资源复用子票据。清单失败时只回滚本次新建票据。成功后按清单记录资源引用和最长保留窗口；动态资源退出所有清单引用并经过宽限期后回收。宽限期至少 120 秒，且不短于该清单历次最长时长加最长分片时长；静态 VOD/ENDLIST 资源保持根票据寿命以支持拖动。重兑旧票据不会延长退役期限。

HLS 子票据上限为全局 20,000、单根 12,000；引用上限为全局 40,000、单根 24,000。退役引用同步清理。容量不足返回可重试 503，仅回滚当前事务新建的票据，不驱逐其他有效会话。静态大清单、持续增长 EVENT 和并发会话都受这些上限约束。

清单事务取得根票据互斥锁后先执行一次全局过期清理，URI 循环中的普通清理最多每秒一次；容量压力立即触发回收，显式维护不受限频影响。每次资源分配和复用仍核验根、父清单和目标票据的期限，不因清理限频延长授权。不得逐 URI 扫描所有票据和引用。

Adaptive 的 HLS 改写／中继路径及其子资源不继承普通文件的首跳快捷提示；它们按响应类型和状态选择服务端完整解析后的地址或流式中继，并应用逐跳重定向、DNS、票据和容量策略。RedirectOnly 不改写清单，未分类的 DirectClient 首个来源重定向可直接交给客户端。分类依据协议、来源元数据、通用路径扩展名、MIME 与内容证据，不匹配特定媒体标题、播放器或来源厂商。

### 16. 并发与资源边界

实现遵循：

- 网络 I/O 不持有全局锁；
- 来源失败协调等待不占用上游或中继并发容量；
- single-flight 仍有其他等待者时，一个等待者取消不会取消共享任务或其他等待者；
- 共享快速定位准备拥有独立、不可续期的七秒截止时间，等待者同时观察自身取消与共享截止；单个等待者取消不影响其他 waiter，最后一个 waiter 在完成前离开会原子移除 pending 身份并取消上游探测；代际变化或共享超时同样阻止旧结果提交；
- 全局解析、单来源解析、流式中继和快速定位分别有上限；
- 待处理项、票据、租约、作业绑定、来源状态和索引均有容量上限；
- 容量策略按状态区分：可缓存条目清理到期项后按各自策略淘汰；活动准备和作业所有权达到上限时拒绝新登记，保留现有所有者；
- 配置代际变化会阻止旧任务提交结果；
- 相同媒体的多个用户共享匹配视频选择的数值计划，各自拥有独立客户端票据和票据级行为／首跳状态；其中可选地址只是已校验、未打开正文的首跳 `Location`，不跨票据共享。匹配画像的服务端解析租约及来源失败退避可跨会话共享；
- 不同媒体之间不共享来源地址、验证器或作业状态。

媒体信息提交使用独立读写栅栏：提交期间持有读侧，配置失效和停机获取写侧，完成已经进入提交阶段的写入后切换代际。磁盘、媒体数据库和快照 I/O 在全局运行时锁外执行，播放读取配置和创建操作可继续进行。所有网关请求及交接后的中继流绑定传输代际取消令牌；失效时关闭活动上游连接并释放并发容量。

快速定位的成功计划、确定性失败和作业 URL 绑定各最多 512 项，进行中的准备最多 64 项。启动重试与输出目录登记分别最多 1,024 项；目录所有者为弱引用，清理回收不再被作业引用的记录，保留活动所有权。代理处理器上限为每个传输实例 32 个，随实例释放。以上上限分别约束各自状态，不代表整台服务器的并发播放数。

### 17. 超时与重试

控制请求共享一个预算，正文与快速定位准备各自有独立生命周期：

| 阶段 | 预算与取消范围 |
| --- | --- |
| 网关控制阶段 | 从串行入口等待开始，共享一次 `GatewayTimeoutSeconds`，覆盖来源协调、解析锁、权威来源连接、适用路径的服务端逐跳响应头、响应分类、清单变换排队、完整清单获取和一次权威重新解析 |
| 无外部控制预算的传输调用 | `GatewayTransport.OpenRequestAsync` 创建自身的 `GatewayTimeoutSeconds`，供独立调用及探测使用 |
| 正文探查与流式读取 | 每次读取单独使用 `GatewayTimeoutSeconds` 空闲超时，正文寿命仍受请求与传输代际取消约束 |
| TS/M2TS 计划准备 | 按容器平均字节率计算窗口和总量，按实测延迟／读取速率调整 Range；全部样本和最多三次校正共享不可续期的七秒及 32 MiB 安全上限 |
| Emby FFmpeg 启动 | Emby 4.9.5 自身的 15 秒窗口；插件的定位准备位于该窗口之前 |

播放来源退避覆盖 `401/403/404/408/410/429/5xx` 和非取消、非信任拒绝的连接异常。连接失败按 502、地址信任拒绝按 403、网关等待／打开超时按 504 处理；正文已交付后发生超时会中止流，无法回写已经发送的状态码。416 保留本次响应语义，不刷新持久长度，也不进入来源退避。租约失配恢复成功时继续按有效结果交付。

每次服务端完整上游打开至多进行一次权威重新解析。不同请求各自具有该上限，并受共享来源退避约束；DirectClient 首跳交接后由读取方直接产生的请求不受网关重试计数控制。取消向网关工作与中继流传播；共享数值准备仍有其他 waiter 时，单个取消只结束该等待，最后一个 waiter 在完成前离开则取消上游探测。

统一控制预算、播放网络故障协调和中继正文的独立生命周期各有回归测试，见[验收状态](TESTING.md#release-readiness)。

### 18. 媒体信息提取

提取任务遍历参与媒体库中的 STRM 项目，查询设置 `EnforceExtraType=false`，覆盖 Emby 已索引的正片与附加视频（例如预告片、花絮等）中的合格 STRM。未被 Emby 索引的文件不在枚举范围内。它调用 Emby 媒体源探测接口，并通过 Emby 媒体流存储库写入：

- 容器、大小、码率和时长；
- 默认音频与字幕索引；
- 视频流；
- 音频流；
- 内嵌与既有外置字幕流。

图像、附件和数据流不进入插件快照。外置字幕在合并时保持现有路径和交付属性。读取替换前状态及应用／清理失败的回滚状态时，存储库流与已水合项目流按身份合并；存储库即使只返回外置字幕，也不会覆盖已水合的内部视频／音频。

Emby 标准探测的实际输入使用短期回环网关票据：最长与本次探测预算一致，只有回环请求可访问，强制中继并逐跳验证实际探测及 HLS 子资源，结束后撤销。标准探测若在打开输入前被宿主链拦截或短路，本轮切换为独立启动 Emby 当前配置的 ffprobe；它读取同一个回环票据，受同一取消和并发边界约束，并把有界 JSON 结果交给 Emby 的 `ProbeResultNormalizer`。因此后备不会绕过逐跳重定向、DNS/IP、信任或 HLS 校验，也不需要识别第三方扩展、播放器、操作系统或来源域名。

权威 STRM 来源由实际网关请求打开，不在签票前重复预检；跳转遭拒时，拒绝类别从网关回传至提取协调器，产生待确认主机与管理员通知。该探测通道独立于播放路由模式。默认音频和字幕索引在合并后的流集合中重新验证，失效内部索引清空，有效外置选择保留。

启用“仅提取缺失的媒体信息”时，技术信息完整的项目始终跳过，即使 STRM 内容已经变化；刷新完整项目必须关闭该选项或显式强制。完整性在项目、探测结果和快照三处使用同一条件：音频来源至少一个非外置音频流，视频来源至少一个非外置视频流，并且时长不短于一秒、容器非空且不为 `strm`。读取项目时同时考虑 Emby 存储库流与已水合项目流；已水合的所需内部音频或视频流可直接证明完整，不要求再次读取存储库，存储库暂时为空也不会把已有内部流误判为缺失，外置流不能单独满足完整性。缺少已水合所需内部流且存储库读取失败时，该项记为本地失败，整轮继续，并且不会启动远程探测或写入来源退避。任务总数和进度包含全部候选；遍历或 `skipped` 不表示启动了远程 ffprobe。

快照 schema 3 的技术白名单包含 HDR/Dolby Vision 主/子类型、旋转、CodecTag、参考帧、AVC/NAL 参数、流开始时间、变形宽高比、听障标记和合法有理数 TimeBase；不保存路径、URL、标题、凭据或任意 Extradata。schema 2 不恢复；仍缺失的项目重新探测，完整项目遵守仅缺失规则，需显式刷新。成功探测可原子替换结构有效的旧 schema 快照，损坏文件保护仍适用。

快照通过 schema、STRM 内容 HMAC 和长度匹配；文件修改时间只作为诊断与操作内版本栅栏，不会让字节完全相同的快照失效。反序列化还验证完整性必需字段、流类型与唯一非负索引、默认流引用、有限非负数值、技术字符串及 256 条内部流上限；主文件结构无效时仅使用通过同样校验的备份，主备都无效时按缓存未命中处理且不会覆盖原文件。应用前仍重新读取并比较内容、长度和修改时间，防止提取期间文件变化。匹配快照的本地恢复先于远程探测退避；快照无法保存不会把已经成功写入 Emby 的技术信息改报为失败。探测结果若不满足统一完整性条件，会记录失败而不会写入快照、成功状态或 Emby 技术字段。

相同来源内容的连续远程探测失败按 30 秒、2 分钟、8 分钟、32 分钟、2 小时 8 分钟、8 小时 32 分钟，最终 24 小时递增退避；同一轮中同一路径和内容版本的共享失败最多推进一次，内容相同但路径不同的项目仍独立记录。STRM 内容变化会让仍然缺失的项目立即重新具备资格，显式强制探测绕过退避。状态文档完整验证键、指纹、失败次数、重试时间、重复项和对象图规模，主文件无效时仅接受有效备份。状态最多保存 32,768 个来源；容量满时先复用成功基线或已过期失败的槽位，绝不驱逐仍在生效的退避。若全部槽位都处于有效退避，新增失败暂不持久化并在后续运行保持可尝试，直到有槽位可用；这是有界状态下避免级联驱逐的保守取舍。清理孤立项后会重新开放容量。快照转换、Emby 数据库提交、网关或票据容量、媒体流存储库读取以及 STRM 本地复验等本地失败不写来源退避；退避期间也不会为重定向主机发现额外放行远程探测。

每张提取票据记录回环网关是否真正收到探测输入请求。任何新探测成功都必须有当前输入打开证据，即使宿主直接返回完整字段；宿主未打开输入即返回或抛错时，在原预算内尝试独立后备，后备也必须独立证明打开输入。标准 Emby 探测未打开输入时先激活当前提取运行的后备，后续候选直接使用独立 ffprobe；宿主已经打开输入但返回不完整结果时，只为该项尝试独立后备，不把整轮切换到后备。所有独立后备进程先经过一个协调器级串行门，防止两个提取 worker 各自的分段请求争用探测中继容量；明确的 TS/M2TS/MTS 输入使用 200 秒分析时长和 128 MiB 探测大小上限，覆盖默认窗口内尚无完整编解码和时长证据的传输流；实际 Audio 项目始终按音频处理，即使来源 URL 没有扩展名；其他项目按 Emby 公共 MIME 映射判断，`audio/*` 来源要求内部音频流，其余要求内部视频流；共享探测按所需流类型隔离；Emby 将音频 STRM 归类为视频项目时也能写入正确技术信息。后备不可用或也未打开输入时累计未打开计数，真正打开输入会立即清零该计数，不等待慢探测结束。后备已经打开输入，但其 JSON 反序列化或 Emby `ProbeResultNormalizer` 在本地失败时，另行累计连续结果失败；一次完整的后备结果会清零这项计数。任一计数连续达到三次都会打开当前提取运行的熔断；后续候选继续执行本地完整性跳过和快照恢复，但不再启动远程探测，并发越界限制在活动 worker 数内。这两类宿主后备故障都不写入来源级失败退避；熔断不伪造成功状态，下一轮仍可重新验证。

关闭“仅提取缺失的媒体信息”或显式强制时，每个目标音频或视频 STRM 执行新的远程探测，并以新结果替换插件管理的技术信息。“保存恢复快照”只控制插件自己的新快照；关闭该选项仍会把成功探测得到的技术字段写入 Emby。仍然缺失的项目因来源变化而重新探测时也使用替换语义：新结果缺失或无效的大小、码率在 Emby 项目中清为 0，快照保留未知状态，恢复后仍为 0；不能残留旧来源值。

清除技术信息（Clear）删除所选范围的插件快照和插件写入的内部技术流，保留媒体文件、STRM、刮削元数据和既有外置字幕。孤立清理（Cleanup）只删除孤立快照和状态，其判断使用已索引 STRM 的规范路径键，不要求当时能读取或解析文件内容；只要有一个候选路径无法安全构建键，本轮就不执行任何孤立状态或快照删除。

### 19. 日志、隐私与安全

允许的日志字段：

- 固定事件代码；
- 宿主 ABI；
- 模式；
- 状态分类；
- 项目 ID 的前八位十六进制短标识（不是 HMAC）；
- 耗时、字节数、包步长、预滚毫秒数；
- 并发与容量计数；
- 异常类型名。

日志禁止包含：

- 完整或部分 URL；
- 查询参数和签名；
- 本地路径和文件名；
- 用户名、设备名和媒体标题；
- API key、Token、Cookie、Authorization、Referer；
- 完整 User-Agent；
- FFmpeg 完整命令行；
- 最终 CDN 主机名。

这里的约束适用于插件自己写出的日志。带票据的 `/StrmBridge/Playback/` 请求路径及重定向响应的 `Location` 仍可能进入 Emby 或反向代理访问／响应头日志；部署者必须对两者脱敏并限制保留时间。

票据使用 256 位随机数。用户和设备摘要使用运行期 HMAC；提供认证身份时核对用户绑定，设备摘要不作为 bearer 票据兑换条件，只用于会话关联与租约隔离。比较敏感摘要时使用固定时间比较。停止时撤销票据、取消传输并清理租约及定位缓存；作业目录所有权由弱引用和在途清理持有必要生命周期，启动重试状态在下一次代际检查时清理，实例最终随生命周期释放。

### 20. 配置与国际化

配置页面使用 Emby 官方 Generic UI 生命周期、SDK 本地化属性和嵌入资源提供简体中文、繁体中文和英文。Emby 依据 `ClientLocale` 设置请求的 `CurrentUICulture`，插件在该请求的编辑器模型上设置显示名称和说明，不修改全局属性描述缓存。播放模式枚举保持 `Adaptive / RedirectOnly / RelayOnly / Native`；配置显示文字跟随语言，协议状态、固定日志原因码及部分 HTTP 错误文本保持机器诊断用途。此流程没有 UI Harmony 补丁。

配置验证执行：

- 数值上下限；`DirectRedirectCacheSeconds` 默认 20、允许 0–60，0 表示每次 DirectClient 网关请求都重新解析权威首跳；默认值覆盖常见起播／seek 的短时请求突发且不超过内部 30 秒路由租约，它不是由码率或响应自动推导，不能自动适配一次性、Range 绑定或更短有效期地址；
- 媒体库 ID 去重；
- 主机规则规范化；
- 精确主机、通配后缀、IP 和 CIDR 语法；
- 保存后的运行时代际切换；
- 清空媒体库选择时立即停止新处理。

### 21. 实现模块

<a id="implementation-modules"></a>

路径均相对于 `src/Emby.StrmBridge/`。本表也是英文部分使用的实现映射。

| 模块 / Module | 职责 / Responsibility |
| --- | --- |
| `Playback/HarmonyPatchHost.cs` | 六个方法的 ABI 校验、安装与卸载 / Six-method ABI validation and patch lifecycle |
| `Playback/HarmonyRuntimeAdapter.cs` | 单运行时选择及反射式补丁元数据适配 / Single-runtime selection and reflective patch metadata adaptation |
| `Playback/PlaybackInfoProcessor.cs` | 原生响应后处理 / Native response post-processing |
| `Playback/NativeVideoStreamProcessor.cs` | 标准 static 接口接管 / Standard static-route adaptation |
| `Playback/TranscodeInputProcessor.cs` | 作业准备、回环票据与精确绑定 / Job preparation and loopback binding |
| `Playback/TranscodeJobCoordinator.cs` | 启动冷却、输出目录所有权与清理 / Startup cooldown and output ownership |
| `Playback/FfmpegCommandProcessor.cs` | 原子命令变换与一次原生启动恢复 / Atomic command mutation and one native startup recovery |
| `Api/GatewayService.cs` | 票据兑换、302/307、中继和 HLS 响应 / Ticket redemption and transport responses |
| `Playback/GatewayTransport.cs` | 逐跳 HTTP、Range、租约与来源退避 / HTTP, Range, leases and source backoff |
| `Playback/FastSeekCoordinator.cs` | 数值计划、共享准备与作业 URL 绑定 / Numeric plans, shared preparation and job bindings |
| `Playback/FastSeekRepresentation.cs` | 强 ETag、长度和正式读取画像一致性 / Strong validator, length and reader-profile consistency |
| `Playback/FastSeekVideoSelection.cs` | Emby 视频轨与节目映射输入 / Selected-video metadata mapping |
| `Playback/TransportStreamClockParser.cs` | TS/M2TS 封装、时钟及随机访问证据 / Framing, clock and random-access evidence |
| `Playback/HlsPlaylistRewriter.cs` | HLS 资源授权改写 / Authorized HLS resource rewriting |
| `Playback/TicketStore.cs` | 能力票据、静态会话关联和 HLS 子票据 / Capabilities, static-session associations and HLS tickets |
| `Playback/GatewayRouteBuilder.cs` | 相对客户端与 Emby 回环地址 / Relative client and loopback routes |
| `Playback/SourceBehavior.cs` | 请求用途及响应能力路由决策 / Purpose- and capability-based routing |
| `Policy/StrmSourcePolicy.cs` | 本地 STRM 读取边界 / Local STRM read boundary |
| `Policy/StaticMediaSourcePolicy.cs` | Emby 媒体源一致性 / Static-source identity checks |
| `Policy/RedirectPolicy.cs` | URI、主机与地址信任 / URI, host and address trust |
| `Policy/PinnedHttpHandler.cs` | DNS 地址固定、默认代理与连接池 / Validated-IP connections, default proxies and pools |
| `Extraction/ExtractionCoordinator.cs` | 提取、替换、恢复、清理及附加视频枚举 / Extraction, recovery, clearing and extras enumeration |
| `Extraction/IndependentFfprobeMediaInfoProbe.cs` | 宿主探测短路后的同网关 ffprobe 后备与 Emby 结果归一化 / Same-gateway ffprobe fallback and host result normalization |

### 22. 测试与维护

正式回归代码位于 `tests/Emby.StrmBridge.Tests/`；可变构建、测试输出、诊断和历史审查记录位于忽略的 `.local/`，发布包位于 `artifacts/`。测试范围、当前二进制证据与实机验收集中维护在 [TESTING.md](TESTING.md)。修改实现契约时同步更新相关测试，发布包不包含历史审查过程或私人日志。

## English

### Architecture and scope

STRM Bridge extracts technical information for selected local audio/video STRM items and routes eligible video playback inside Emby. It preserves Emby's item identity, media versions, permissions, client negotiation and transcoding decisions. Audio playback remains native. Dynamic or header-dependent sources and mismatched versions remain on the native path.

The six patches cover PlaybackInfo GET/POST, standard static video, per-job FFmpeg preparation, command execution and delayed output cleanup. Runtime ABI validation precedes installation. One compatible loaded Harmony implementation is selected for the entire lifecycle; the embedded fallback is used only when no compatible candidate exists. Ambiguous runtimes fail closed. See the bilingual [module map](#implementation-modules) for source ownership.

Client routes are relative to the existing Emby origin/URL Base. Server jobs and extraction use Emby's reported loopback origin. Tickets bind purpose, item, exact source, file version and runtime generation. A standard static request enters the gateway in-process without an extra loopback HTTP request.

### Transport contracts

| Mode | Contract |
| --- | --- |
| Adaptive | Prefer direct file handoff, classify unknown resources, rewrite HLS and relay where required |
| RedirectOnly | Hand off redirects without HLS rewriting |
| RelayOnly | Stream through Emby under transport capacity limits |
| Native | Preserve native playback |

Known file DirectClient handoff validates the first authoritative Location and normalizes accepted 301/302/303/307/308 responses to 302 without opening the target. Unknown Adaptive resources may require classification reads, followed by a fresh authoritative first hop. An explicit HLS extension invalidates a file shortcut in Adaptive.

First-hop caching is ticket/profile scoped, defaults to 20 seconds and accepts 0–60. Positive responses carry private max-age, must-revalidate, Expires and profile Vary, bounded by ticket and route expiry. Zero disables address reuse; client cache-bypass directives force resolution. One-use, Range-bound and shorter-lived URLs require zero because the plugin cannot infer their semantics.

Server-read final-address leases are separate, bounded to 30 seconds and 4,096 entries, and require successful response validation. Server-FFmpeg 307 lifetime is the minimum of ticket expiry, validated source-lease expiry and 20 seconds; sub-second remaining lifetimes do not establish cacheable handoff. A target must tolerate validation and subsequent reads. No plugin cache stores media bodies.

Each server open permits at most one authoritative refresh. Single-range 206 validation covers first reads, cache hits, refreshes and suffix ranges; mismatches are not delivered or cached. Valid 200/304/416 retain their HTTP meanings. Mutable HLS is separate from fixed-file representation validation.

Observed upstream failures coordinate by source digest, method and normalized User-Agent, not by ticket/user/device. Backoff grows through 2/4/8/16/30 seconds or honors bounded Retry-After up to five minutes. Client/runtime cancellation, trust rejection and local control-budget expiry are not upstream failure evidence. After handoff, the reader's downstream responses and concurrency are outside this coordinator. Relay capacity is not a playback-versus-thumbnail priority scheduler.

### HLS and lifecycle

Manifest rewriting requires complete input and emits 200 with the rewritten length, without origin range/validator metadata. Except in RedirectOnly, non-identity body encoding is rejected before classification; HEAD remains bodyless. Limits are 2 MiB, 20,000 lines, 10,000 URIs and nesting depth 8.

Each successful manifest transaction atomically commits resource references. Dynamic resources retire after all references leave their windows and a grace period of at least 120 seconds, no shorter than the longest observed playlist plus its longest segment. VOD/ENDLIST resources retain root lifetime. Redeeming retired resources cannot extend their retirement deadline.

Resource quotas are 20,000 globally and 12,000 per root; reference quotas are 40,000 globally and 24,000 per root. Capacity failure returns retryable 503 and rolls back only newly created tickets. A manifest mutation sweeps once after taking its root gate; ordinary issuance cleanup is limited to once per second, with immediate pressure/maintenance cleanup. Every root, parent and reused ticket is still checked for expiry. Issuance must never scan all state per URI.

Control queueing, source opens, redirects, classification, complete-manifest retrieval and mutation waiting share one control deadline. Returned body streams have independent per-read idle timing and own their cancellation links until disposal. Generation changes prevent stale commits. Attempt-owned tickets and seek bindings are released once on failed/cancelled startup or confirmed completion. Output cleanup verifies exact path ownership and active jobs before deletion; old cleanup cannot remove a newer job's output.

### Evidence-based TS/M2TS positioning

Preparation uses transport framing, PAT/PMT mapping, selected-video PCR and random-access evidence. It requires a single reliable program mapping and consistent strong ETag/length across samples. Native and probe HTTP profiles must match; a guarded If-Match is applied for formal reads. Custom profiles or missing evidence retain native positioning.

For container byte rate `b`, head sample `H = 512 KiB`, scan window `W = clamp(b × 4 seconds, 512 KiB, 24 MiB)` and total bytes `B = min(32 MiB, H + 4W)`. Packet-aligned Range chunks are capped at 8 MiB and adapt to measured latency, read rate and remaining time. All samples and up to three corrections share a non-renewable seven-second deadline. These are engineering bounds, not universally optimal timeout values.

The anchor must have selected-video random-access evidence and a PCR interval no wider than 100 ms, entirely 0.25–12 seconds before the target. Bitrate estimates locate search windows, not the final playback timestamp. Successful plans last two minutes and remain representation-bound. Same-target preparation is shared; cancelling the last waiter removes the pending identity and cancels upstream reads.

Preparation runs before the verified Emby 4.9.5 startup window. The command prefix performs memory-only validation and atomic mutation. If optimized startup returns false after host process shutdown, it can restore the complete native command and retry once within the original budget. Faulted tasks do not launch another process. Adaptive transport-stream starts with a target of at least ten seconds and a device/session identity have bounded same-target startup coordination; failure or cancellation observed after eight seconds opens a non-sliding 30-second cooldown.

### Extraction and recovery

Extraction enumerates indexed STRM, including eligible extras. Actual Audio items always require internal audio; other items use Emby's MIME classification. Completeness requires the appropriate internal stream, a non-STRM container and duration of at least one second. Missing-only skips complete items even after a source change; explicit refresh performs a fresh probe.

Every successful fresh probe requires current loopback-input access evidence. A host probe that returns or fails before opening input triggers an independent configured ffprobe fallback within the same budget and gateway boundary. Repeated fallback unavailability, unopened inputs or local result failures stop later remote probes for that run, while local skips and snapshot recovery continue.

Fresh results replace managed technical fields, clear obsolete unknown size/bitrate values, retain existing external streams and validate default selections. Schema 3 stores whitelisted HDR/Dolby Vision, rotation and negotiation fields as well as basic technical metadata. Older schemas are ignored; valid legacy files may be replaced after a successful fresh probe, while corrupt-file protection remains. Snapshot saving is optional and does not control successful writes to Emby.

Remote extraction failure backoff is separate from playback backoff and grows from 30 seconds to 24 hours. Local persistence/normalization/capacity failures do not penalize the source. Clear removes managed technical information and snapshots in scope; orphan cleanup removes only orphaned state. See [installation](INSTALL.md), [security](SECURITY.md), [compatibility](COMPATIBILITY.md) and [testing](TESTING.md) for operator guidance and current validation status.

# STRM Bridge 播放网关设计 / Playback Gateway Design

[中文](#中文) | [English](#english)

远程 MPEG-TS/M2TS 非零起播的字节定位、命令变换和验收约束由 [STRM 远程传输流快速定位设计](FAST_SEEK_DESIGN.md) 定义。

## 中文

### 1. 目标

STRM Bridge 是一个面向本地 `.strm` 文件的 Emby Server 插件，每个文件包含一个 HTTP(S) 媒体 URL。插件提供两个相互配合的功能：

- 通过 Emby 的媒体探测和存储库 API 提取技术媒体信息；
- 通过沿用当前 Origin 的进程内网关路由播放请求。

插件以只读方式使用媒体库文件。Emby 继续负责媒体库组织、元数据刮削、权限、媒体版本选择、PlaybackInfo 生成和外部播放器启动行为。

### 2. 支持的宿主

插件面向 `netstandard2.1` 和 Emby Server 4.9.5.x。运行时 ABI 校验确认两个 PlaybackInfo 服务方法、渐进式请求执行方法、转码状态创建方法、最终 FFmpeg 命令启动方法及其请求和响应结构后，播放路由才会启用。播放 ABI 校验失败时，媒体信息提取和维护功能仍可独立使用。

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
5. 签发一个带有客户端直放用途的 256 位、仅内存播放票据；
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
4. 对 `Adaptive` 模式中大于 10 秒的 TS/M2TS 作业，在 5 秒总时限内读取两个最大 512 KiB 的稀疏 Range 样本；估算预滚不在 0.25–6 秒窗口时增加一个校正样本并生成字节定位计划；
5. 签发带有服务端 FFmpeg 用途的单次作业内存票据；
6. 通过 Emby 的 `GetLocalApiUrl(IPAddress.Loopback)` 获取运行时本地 API Origin，并结合当前请求的 API 路径前缀生成绝对回环网关地址；
7. 仅修改当前 `StreamState` 的媒体源副本、媒体路径和协议，使 FFmpeg 从网关读取；
8. 将匹配的字节定位计划绑定到该作业的精确回环输入 URL；
9. 任一生成或写入步骤失败时恢复原始作业状态并撤销票据。

`FfmpegCommandProcessor` 在进程启动前只接受该精确回环 URL、HTTP 输入和 `segment` 输出组合。首次起播使用自适应两/三样本计划；HLS 分片请求改变起播时间时，每个新目标先读取一次有界 PCR 样本，结果超出安全预滚窗口时最多追加一次校正。同一来源、媒体源、目标和运行时代际的已验证计划可跨播放会话共享；命令仍严格绑定到各自的回环输入 URL。处理器接受 Emby 原生 `segment_time_delta=-ss` 时间线，或由 `copyts`、`start_at_zero`、禁用负时间戳改写、无其他时间偏移以及 `segment_start_number × segment_time ≈ ss` 共同证明的完整转码时间线。处理器原子设置校准后的字节偏移、禁止再次搜索，清空输入级绝对起播时间，用输出级短预滚相对时间顺序裁剪，将输出时间戳偏移恢复到原目标时间，并把首段时间容差增加半个实际分片时长且最多增加 3 秒。Emby 生成的分片起始编号与后续分片节奏保持不变。任一验证或修改失败时恢复全部原生命令字段。

该入口不修改媒体库项目、STRM 文件、持久化媒体源或 PlaybackInfo 的原生路径。非 STRM、本地媒体文件、未参与媒体库、不匹配媒体源及 `Native` 模式继续使用 Emby 原生转码输入。

### 6. 票据模型

票据是由 256 位随机数支持的 Base64URL 承载能力，其仅内存载荷包含：

- 票据作用域；
- 客户端直放或服务端 FFmpeg 用途；
- 媒体项目和媒体源身份；
- 当前 STRM 来源身份；
- 上游 URI；
- 运行时代际；
- 可选的 HMAC 用户绑定；
- 可选的运行期 HMAC 设备绑定；
- 签发、预览、活动和绝对过期时间；
- HLS 嵌套深度。

播放票据具有 10 分钟预览窗口。完成授权兑换后，其活动期按媒体时长加重连宽限期延长，绝对上限为 24 小时。HLS 子票据继承根播放票据的边界，并在每次访问和后代发行时精确校验其登记的根票据。同一播放票据、相同资源深度下对相同绝对 HLS 资源的重复引用会复用同一个子票据；不同深度使用独立子票据，使合法有向引用可继续处理，并让循环引用按深度递增后在第 8 层终止。同一根下的清单改写、票据发行和失败回滚按根串行提交，避免失败请求撤销并发成功响应已复用的票据。撤销根票据时会一并撤销其子票据。

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
| `Adaptive` | 普通客户端文件直放使用经过验证并可复用的 HTTP 302；地址表现不稳定时在当前直放上下文中短期改用中继；服务端 FFmpeg 文件通过网关中继；HLS 重写并中继。 |
| `RelayOnly` | 通过网关流式中继经过验证的响应。 |

客户端直放仍先通过票据、权限、媒体库、来源身份和逐跳重定向验证。一个新直放上下文先按实际请求验证地址；同一地址再正确响应一次复用请求后，后续请求在短期有效期内直接取得 302，媒体正文只由客户端读取。普通中继保留状态码、内容类型、有效内容长度、字节范围元数据、验证器和安全的内容处置字段。响应携带 `private, no-store`、`Pragma: no-cache`、`nosniff` 和 `no-referrer`。请求取消和读取空闲超时在整个响应体传输期间持续生效。需要服务器 IP 或服务器侧上下文的来源使用 `RelayOnly`。

### 10. 重定向租约

传输层为每次重定向保留一个仅内存验证租约，其键由以下内容的摘要组成：

- 播放票据；
- 标准化的 GET 或 HEAD 方法；
- 标准化 User-Agent；
- `Accept` 和 `Accept-Language`。

Range 值不进入租约键，因此临近字节请求可共享同一个已验证目标。每次 Range 复用都要求候选响应返回与请求起止范围、文件总长度和响应正文长度一致的单段 `206 Content-Range` 与 identity 编码。按来源或票据建立的单飞门会合并同时发生的首次解析。只有最终状态为 200 或 206 的地址会写入租约。缓存目标每次使用前都会重新通过策略校验，有效期为 30 秒；到期、传输拒绝、范围不匹配或返回 401、403、404、410 时会被清除并在当前总超时预算内从 STRM 原始地址解析；耗尽总超时预算时当前请求返回超时，下一请求从原始地址解析。首次重定向链的最终目标返回上述状态时也会释放响应并从 STRM 原始地址重试一次。运行时失效使用代际校验；单飞门和上游打开操作携带同一代际，阻止清理前开始的等待者或在途解析重新写入已清空的租约。

客户端直放另有一个仅内存直放上下文，其摘要包含票据层级、实际上游资源、项目、媒体源、STRM 来源指纹、授权用户绑定和 Emby 报告设备标识的运行期 HMAC；方法、User-Agent、`Accept` 与 `Accept-Language` 继续参与最终键，Range 不参与。原始设备标识不进入键、持久化或日志。没有设备标识时，上下文自动收窄到单张播放票据。不同 HLS 资源、用户或设备彼此隔离，同一用户和设备中新生成的播放票据可共享结果。首次并发解析通过引用计数、代际绑定的单飞门合并，等待者不占用中继并发槽。新重定向地址至少经过两步确认：首次从 STRM 原始地址解析并验证，下一次复用时再以实际请求验证状态与 Range；只有直接来源或完成复验的重定向地址才进入 30 秒快速交接。快速交接仍执行重定向策略校验，但不再由插件预读 CDN，客户端因此不会为每个 M2TS Range 同时触发一次插件校验请求和一次播放器正文请求。

`Adaptive` 在地址复验发生拒绝重试，或最终响应为 401、403、404、410、429、5xx 时，为同一直放上下文记录 30 秒中继决策。该窗口内的新 Range 直接按中继路径打开原始 STRM 来源，避免继续把不稳定地址交给播放器；成功的中继响应不会续期该决策，窗口到期后重新尝试直达。范围错误 416 不会污染该上下文。`RedirectOnly` 保持显式重定向语义，但不会交接状态非 200/206 的最终响应。直放上下文、快速交接地址、降级决策和单飞状态均不持久化，不包含原始用户或设备标识，也不写入日志。

快速定位探测还会创建来源指纹范围内的 30 秒候选租约。候选键包含方法、`Accept` 和 `Accept-Language`，仅服务端 FFmpeg 正式媒体请求会使用自己的 User-Agent 复验候选；客户端直放不会读取该候选。候选响应必须返回与请求起止范围、文件总长度和正文长度一致的单段 `206 Content-Range` 与 identity 编码。状态、范围或编码不匹配时清除候选并从 STRM 原始地址解析。候选地址始终重新执行重定向策略校验。

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
- 直放上下文决策：4,096，30 秒，仅内存；
- 中继并发：1–16；大于 1 时快速定位探测至少为播放正文保留 1 个配置槽位；
- 重定向跳数：1–8；
- 网关超时：10–180 秒；
- 快速定位：初始 2 个样本，估算预滚不在 0.25–6 秒窗口时增加第 3 个样本；每个新目标先使用 1 个校正样本，超出安全窗口时最多增加第 2 个样本；每个样本最大 512 KiB，每次准备时限 5 秒，两级内存索引各 512 条，每个绑定最多 16 个目标计划，绝对有效期 2 分钟；
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
- 符合条件的远程 TS/M2TS 非零起播作业产生快速定位就绪和已应用事件；每个新 HLS 目标最多一次有界校正探测，相同目标不重复探测，每个 FFmpeg 媒体正文只使用一个连续 Range；
- 直出响应、重定向文件、Range/HEAD、拖动、重连和 HLS 播放通过宿主矩阵；
- 本地、远程、HTTPS 代理和 API 路径前缀访问均沿用客户端的 Emby Origin；
- 发布产物不包含测试凭据、个人路径或针对特定播放器厂商的路由逻辑。

---

## English

Byte calibration, command transformation, and acceptance rules for non-zero remote MPEG-TS/M2TS playback are defined by the [remote transport-stream fast seek design](FAST_SEEK_DESIGN.md).

### 1. Purpose

STRM Bridge is an Emby Server plugin for local `.strm` files containing one HTTP(S) media URL. It provides two coordinated functions:

- technical media-information extraction through Emby's media-probe and repository APIs;
- playback routing through a current-Origin, in-process gateway.

The plugin keeps media-library files read-only. Emby remains responsible for library organization, metadata scraping, permissions, media-version selection, PlaybackInfo generation and external-player launch behavior.

### 2. Supported host

The plugin targets `netstandard2.1` and Emby Server 4.9.5.x. Playback routing activates after a runtime ABI check confirms two PlaybackInfo service methods, the progressive request executor, transcode-state creation, final FFmpeg command startup, and their request and response shapes. Media-information extraction and maintenance remain independently available when the playback ABI check fails.

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
5. issues a 256-bit, memory-only ticket with direct-client purpose;
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
4. for an `Adaptive` TS/M2TS job beyond ten seconds, it reads two sparse Range samples of at most 512 KiB within one five-second deadline, adds a third corrected sample when estimated pre-roll falls outside the preferred 0.25-to-6-second window, and prepares the byte plan;
5. it issues a memory-only ticket with server-FFmpeg purpose for the job;
6. it obtains the runtime local API Origin through Emby's `GetLocalApiUrl(IPAddress.Loopback)` and combines it with the active API path prefix to create an absolute loopback gateway URL;
7. it changes only the current `StreamState` media-source clone, media paths, and protocols so FFmpeg reads from the gateway;
8. it binds a matching byte plan to that job's exact loopback input URL;
9. any route-generation or state-write failure restores the original job state and revokes the ticket.

Immediately before process startup, `FfmpegCommandProcessor` accepts only that exact loopback URL with an HTTP input and `segment` output. It uses the adaptive initial plan; each previously unseen later HLS target receives one bounded PCR correction and at most one bounded retry when the result falls outside the safe pre-roll window. A validated plan is shared across playback sessions only when source fingerprint, media-source ID, target, timeline identity and runtime generation all match; each command remains bound to its own exact loopback input. It accepts Emby's native `segment_time_delta=-ss` timeline or a full-transcode timeline proven jointly by `copyts`, `start_at_zero`, disabled negative-timestamp rewriting, absent competing offsets, and `segment_start_number * segment_time ~= ss`. It atomically sets the calibrated byte offset, disables further seeking, clears the input-side absolute seek, applies the short relative pre-roll as an output-side sequential trim, restores the output timestamp offset to the original target, and advances the first-segment tolerance by half the actual segment duration with a three-second cap. Emby's segment start number and later cadence remain unchanged. Any failed validation or mutation restores every native command field.

This entry point does not modify library items, STRM files, persisted media sources, or native PlaybackInfo paths. Non-STRM and local media, out-of-scope libraries, unmatched media sources, and `Native` mode retain Emby's native transcode input.

### 6. Ticket model

Tickets are Base64URL bearer capabilities backed by 256 random bits. Their memory-only payload includes:

- ticket scope;
- direct-client or server-FFmpeg purpose;
- item and media-source identity;
- current source identity;
- upstream URI;
- runtime generation;
- optional HMAC user binding;
- optional runtime-HMAC device binding;
- issue, preview, active and absolute-expiry timestamps;
- HLS nesting depth.

Playback tickets use a ten-minute preview window and extend during authorized redemption according to media runtime plus reconnect grace, capped at 24 hours. HLS child tickets inherit the root playback bounds, and every access and descendant issuance verifies their registered root exactly. Repeated references to the same absolute HLS resource at the same resource depth under one playback ticket reuse one child ticket. A different depth receives a distinct child, allowing valid directed references while making cycles advance to the depth-eight limit. Manifest rewriting, ticket issuance and failure rollback are committed serially per root so a failed request cannot revoke a shared child already returned by a concurrent successful response. Revoking the root revokes its children.

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
| `Adaptive` | Ordinary client files use a validated reusable HTTP 302; an unstable address temporarily selects relay for that direct-play context; server FFmpeg files are relayed; HLS is rewritten and relayed. |
| `RelayOnly` | Validated responses are streamed through the gateway. |

Client direct play still passes ticket, permission, library, source-identity and per-hop redirect validation. A new direct-play context validates the address with the actual request; after the same address correctly serves one reuse request, later requests receive a direct 302 for the short context lifetime and only the client reads the media body. Ordinary relay preserves status, content type, content length when valid, byte-range metadata, validators and safe content disposition. Responses carry `private, no-store`, `Pragma: no-cache`, `nosniff` and `no-referrer` headers. Request cancellation and an idle read timeout remain active for the full response body. Sources that require the server IP or server-side context use `RelayOnly`.

### 10. Redirect leases

The transport keeps a memory-only validation lease for each redirect, keyed by a digest of:

- playback ticket;
- normalized GET or HEAD method;
- normalized User-Agent;
- `Accept` and `Accept-Language`.

Range values stay outside the key so nearby byte requests can share one validated target. Every Range reuse requires a single identity-encoded `206 Content-Range` whose requested start and end, total file length and response body length are consistent. A source- or ticket-keyed single-flight gate merges simultaneous first resolutions. Only a final status of 200 or 206 is stored as a lease. Each cached target is policy-validated again before use, expires after 30 seconds and is evicted on expiry, transport rejection, range mismatch, or status 401, 403, 404 or 410, then resolves from the STRM source within the current total timeout budget. Exhausting that budget times out the current request and makes the next request resolve from the source. The same statuses on the final target of a fresh redirect chain release that response and permit one retry from the source. Runtime invalidation uses generation checks; the single-flight gate and upstream open carry the same generation so a waiter or in-flight resolution that began before clearing cannot repopulate cleared leases.

Direct-client delivery also has a memory-only direct-play context. Its digest contains the ticket depth, actual upstream resource, item, media source, STRM source fingerprint, authorization binding, and a runtime HMAC of Emby's reported device identifier; method, User-Agent, `Accept` and `Accept-Language` remain part of the final key, while Range does not. The raw device identifier is absent from keys, persistence and logs. When no device identifier is available, the context narrows to one playback ticket. Different HLS resources, users and devices remain isolated, while newly issued playback tickets for the same user and device can share the result. A reference-counted, generation-bound single-flight gate merges concurrent first resolutions before they consume relay capacity. A new redirected address requires two-step confirmation: initial resolution and validation from the STRM source, followed by one actual reuse request that validates status and Range. Only a direct source or a redirected address that completes this reuse enters the 30-second fast handoff. Fast handoff still applies redirect-policy validation but performs no CDN pre-read, avoiding one plugin validation request plus one player body request for every M2TS Range.

When redirect reuse is rejected, or the final response is 401, 403, 404, 410, 429 or 5xx, `Adaptive` records a 30-second relay decision for the same direct-play context. New Range requests in that window open the original STRM source on the relay path instead of returning the unstable address to the player. Successful relay responses do not renew the decision, so direct delivery is retried after the original window expires. A request-specific 416 does not poison the context. `RedirectOnly` keeps its explicit redirect semantics but does not hand off a final response whose status is not 200 or 206. Direct-play contexts, fast-handoff addresses, fallback decisions and single-flight state are memory-only, contain no raw user or device identifier and are absent from logs.

Fast-seek probes also create a 30-second candidate lease scoped to the source fingerprint. The candidate key includes the method, `Accept`, and `Accept-Language`. Only server-FFmpeg media retrieval consumes the candidate and preserves its own User-Agent; direct-client delivery never reads it. Reuse requires a single identity-encoded `206 Content-Range` whose start, end, total length and body length match the request. A status, range, or encoding mismatch clears the candidate and resolves from the original STRM source. Redirect policy validation runs again before every candidate use.

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
- direct-play context decisions: 4,096 for 30 seconds, memory only;
- relay concurrency: 1–16; above one, fast-seek probes leave at least one configured slot available for playback delivery;
- redirect hops: 1–8;
- gateway timeout: 10–180 seconds;
- fast seek: 2 initial samples plus a third when estimated pre-roll falls outside 0.25–6 seconds; each new target uses 1 correction sample and at most a second when the result falls outside the safe window; samples are at most 512 KiB, each preparation has a 5-second deadline, each top-level memory index has 512 entries, each binding has up to 16 target plans, and plans have a 2-minute absolute lifetime;
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
- an eligible non-zero remote TS/M2TS job reports fast-seek ready and applied events, reuses its bound calibration after HLS segment seeks, and uses one continuous Range per FFmpeg media body;
- direct-body, redirected file, Range/HEAD, seek, reconnect and HLS playback pass the host matrix;
- local, remote, HTTPS proxy and API path-prefix access reuse the client's Emby Origin;
- release artifacts contain no fixture secrets, personal paths or vendor-specific player routing.

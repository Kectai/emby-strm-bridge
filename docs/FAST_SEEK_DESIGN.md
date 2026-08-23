# STRM 远程传输流快速定位设计

本文档是 STRM Bridge 远程 MPEG-TS/M2TS 非零起播优化的实现约束。实现、测试和发布说明以本文档定义的启用条件、数据边界、回退语义和验收指标为准。

## 1. 目标

当 Emby 为参与媒体库中的远程 STRM 传输流生成 HLS 作业时，插件把 FFmpeg 的远程时间搜索转换成经过校准的单次字节范围读取。该路径保留 Emby 的原生媒体源、转码配置、音轨、字幕、会话和 HLS 输出。

验收目标：

- 一个符合条件的非零起播作业对媒体正文建立一个连续的 HTTP Range 读取；
- FFmpeg 输入从目标时间前的短预滚位置开始；
- HLS 输出时间线与请求的起播位置一致；
- 计划生成或命令修改失败时，Emby 原生命令保持完整；
- 播放判定不包含播放器名称、厂商、媒体标题、固定域名或固定服务器地址；
- 最终重定向地址、查询参数、鉴权头和本地媒体路径仅存在于受控内存对象中，并且不进入插件日志。

## 2. 功能边界

快速定位路径处理同时满足以下条件的作业：

1. 插件启用并使用 `Adaptive` 播放模式；
2. 项目属于已选择的媒体库，来源项目是当前可验证的本地 `.strm`；
3. Emby 已选定媒体源并开始创建服务端 FFmpeg 作业；
4. 该转码作业携带大于 10 秒且位于媒体有效时长内的 `StartTimeTicks`；
5. 上游支持标准 HTTP 字节范围响应，并返回确定的文件总长度；
6. 数据内容符合 188、192 或 204 字节传输流包结构；
7. 稀疏样本中存在同一 PID 的有效 PCR 时钟；
8. Emby 最终生成分段 HLS FFmpeg 命令，输入是本次作业签发的回环网关路由；
9. 运行时 ABI 与经过验证的 Emby 4.9.5.x 命令模型一致。

其他作业沿用 Emby 原生处理。直接播放、直接串流、零位置起播、本地媒体、普通文件容器和 HLS 上游清单不创建快速定位计划。

## 3. 播放流程

```text
PlaybackInfo 请求
    │
    └─ Emby 生成原生媒体源和转码能力
             │
             ▼
Emby 为已选媒体源创建 StreamState
    │
    ├─ TranscodeInputProcessor 验证作业项目、媒体源和 StartTimeTicks
    ├─ Range 样本 A：识别 TS 包结构、PCR PID、文件长度和起始 PCR
    ├─ Range 样本 B：在按文件时长估算的位置读取 PCR
    ├─ 估算预滚不在 0.25–6 秒窗口时读取样本 C 并校正实际时间
    ├─ 生成短期 SeekPlan 并签发回环网关票据
    └─ 将 SeekPlan 精确绑定到该回环输入 URL
             │
             ▼
Emby 生成 FfmpegCommand
    │
    ├─ Harmony 前缀验证命令、输入 URL、HTTP 协议和 segment muxer
    ├─ 新目标执行一次有界 PCR 校正，超出窗口时最多追加一次；同目标复用共享计划
    ├─ 设置 HTTP offset 和 seekable=false
    ├─ 清空输入 ss，将输出 ss 设为计划内的相对预滚时间
    ├─ 设置 output_ts_offset 恢复绝对 HLS 时间轴
    └─ 验证原生或索引时间契约并有界调整首段 segment_time_delta
             │
             ▼
FFmpeg → 回环网关 → 已验证上游
    └─ 一个连续 Range 正文读取 → Emby 原生 HLS 输出
```

## 4. 组件职责

### 4.1 `TransportStreamClockParser`

- 从有限字节样本识别 188、192 和 204 字节包步长；
- 记录包起点、同步字节位置和传输流 PID；
- 解析 adaptation field 中的 42 位 PCR 值；
- 为后续样本选择与首个样本一致的 PCR PID；
- 处理 PCR 环回并输出单调的相对时间；
- 拒绝同步密度不足、包越界、PCR 缺失和时间线异常的数据。

解析器只读取调用方提供的内存，不执行网络访问，也不保留媒体内容。

### 4.2 `GatewayFastSeekProbeClient`

- 复用现有网关传输层的重定向策略、主机信任、并发和超时；
- 发送带有 `Accept-Encoding: identity` 的有界 Range 请求；
- 只接受 `206 Partial Content`、匹配的 `Content-Range` 和确定的总长度；
- 每个样本最多读取 512 KiB；
- 把经过策略验证且范围响应正确的最终地址写入 30 秒来源候选；
- 返回样本字节、实际范围起点和总长度；
- 响应释放后立即释放样本网络资源。

### 4.3 `FastSeekCoordinator`

- 根据媒体时长和请求时间先生成两个稀疏样本位置；
- 用 PCR 时间与绝对字节位置计算平均字节速率；
- 估算预滚位于 0.25–6 秒时直接生成计划，落在该窗口外时把第三个样本定位到目标前 4 秒附近；
- 验证校正后的相对预滚位于 0.25–12 秒；
- 生成、缓存、绑定和清理 `FastSeekPlan`；
- 为每个新 HLS 目标读取一次 PCR 校正样本，超出安全预滚窗口时最多追加一次校正；
- 仅在来源指纹、媒体源、目标、时间线身份和运行时代际一致时跨播放会话共享计划；
- 将所有异常收敛为固定原因码并保留原生回退。

### 4.4 `FfmpegCommandProcessor`

- 只接受由 `FastSeekCoordinator` 绑定的精确回环输入 URL；
- 要求输入协议键为 `http`，输出 muxer 键为 `segment`；
- 首次起播使用自适应两/三样本计划，后续新目标使用一/二次 PCR 校正后的计划；
- 要求原始 `ss` 与最终目标计划时间在 250 毫秒内一致；
- 接受两种可证明的时间线：原始 `segment_time_delta` 与 `-ss` 互为相反数；或完整转码命令同时满足 `copyts`、`start_at_zero`、`avoid_negative_ts=disabled`、不存在其他输入/分段偏移，并且 `segment_start_number × segment_time` 与 `ss` 一致；所有时间偏差不超过 250 毫秒；
- 原子保存并修改六个字段：`offset`、`seekable`、输入 `ss`、输出 `ss`、`output_ts_offset` 和 `segment_time_delta`；
- 保留 Emby 原生 `segment_start_number`，把首段时间容差调整为 `原生值 + min(segment_time / 2, 3 秒)`；
- 任一字段不可写或验证失败时恢复所有原值；
- 成功后保留 Emby 生成的其余命令参数。

### 4.5 `HarmonyPatchHost`

运行时验证以下第五个目标：

```text
Task<bool> FfmpegRunner.Start(FfmpegCommand, CancellationToken)
```

补丁在 FFmpeg 命令构建完成、进程启动之前执行。补丁所有权使用新的唯一版本标识。目标类型、程序集版本、返回类型或参数形状不匹配时，整组播放补丁保持原生状态。

## 5. 数据模型

`FastSeekPlan` 仅包含：

- HMAC 来源指纹；
- 媒体源 ID；
- 请求起播 ticks；
- 上游文件总长度；
- HTTP 字节偏移；
- TS 包步长、包起点和 PCR PID；
- 时间线原点的绝对包位置和 PCR 时钟；
- 有界平均字节速率和媒体总时长；
- 相对预滚时间；
- 运行时代际；
- 创建和过期时间。

计划不包含最终 URL、STRM 内容、查询参数、请求头、用户名、媒体标题和本地路径。

计划采用两级内存索引：

1. `(来源指纹, 媒体源 ID, StartTimeTicks, 媒体时长, 运行时代际)` 用于合并兼容播放会话的并发准备；
2. 精确回环输入 URL 用于 StreamState 与 FfmpegCommand 之间的绑定；每个绑定保存初始校准以及最多 16 个按目标时间索引的校正计划。

两级索引各自最多保存 512 个计划引用，有效期 2 分钟。配置失效、插件停用、运行时代际变化和插件释放都会清空计划。

## 6. 字节位置算法

定义：

- `D`：媒体总时长；
- `T`：请求起播时间；
- `L`：HTTP Content-Range 报告的文件总长度；
- `P`：4 秒目标预滚；
- `F`：首个样本的 PCR 与绝对字节位置；
- `E`：估算样本的 PCR 与绝对字节位置。

步骤：

1. 从文件起点读取样本 A，建立包格式并选择 PCR PID；
2. 计算初始估算 `L × (T - P) / D`，按包起点向下对齐；
3. 读取样本 B，通过 `F` 和 `E` 得到 PCR 相对时间及平均字节速率；
4. 用样本 B 起点到首个 PCR 的字节距离反推样本起点时间，并计算 `relativeSeek = T - sampleStartTime`；
5. `relativeSeek` 位于 0.25–6 秒时直接生成两样本计划；
6. 预滚落在 0.25–6 秒窗口外时，按 PCR 时间误差计算校正字节位置并按包起点向下对齐；
7. 读取样本 C，以首个匹配 PCR 反推样本起点时间；
8. 校正后的 `relativeSeek` 位于 0.25–12 秒且所有时间、字节和速率边界成立时生成三样本计划。

回环输入随后收到不同的 `ss` 时，协调器先用时间线原点和平均字节速率估算偏移，再读取一个最大 512 KiB 的样本。样本中的真实 PCR 决定该字节位置的媒体时间和准确相对预滚；若结果超出安全窗口，该 PCR 会生成一次最终校正位置。来源指纹、媒体源、目标、时间线身份和运行时代际均一致的播放会话共享成功计划。新目标最多增加两次控制面 Range 探测，FFmpeg 媒体正文仍只执行一次连续 Range 读取。目标时间、文件边界、PCR 或预滚边界不成立时保持 Emby 原生命令。

PCR 使用 27 MHz 时钟和 MPEG-TS 环回模数。算法采用受界 `double` 运算生成候选位置，并在写入计划前转换、对齐和检查为 `long`。

## 7. 命令变换

可接受的原生时间线语义：

```text
HTTP input at byte 0 + absolute -ss T + segment_time_delta -T
```

可接受的索引转码时间线语义：

```text
HTTP input at byte 0 + absolute -ss T
+ copyts + start_at_zero + avoid_negative_ts disabled
+ segment_start_number N + segment_time D, where N * D ~= T
```

快速定位命令语义：

```text
HTTP input at byte Offset, seekable=false
+ output-side -ss RelativeSeek
+ output_ts_offset T
+ segment_time_delta (-T + min(segment_time / 2, 3s))
```

`offset` 使 FFmpeg 从网关发出目标 Range；`seekable=false` 使 MPEG-TS demuxer 在该输入中顺序读取；清空输入级 `ss` 避免 demuxer 再次发起时间搜索；输出级相对 `ss` 通过顺序丢弃预滚内容完成精确裁剪。`output_ts_offset=T` 恢复原生绝对搜索对应的 HLS 输出时间轴。原生契约直接使用已经验证的 `segment_time_delta=-T`；索引契约根据已经验证的分片编号和实际分片时长合成同一基准值。Emby 生成的 `segment_start_number` 保持原值；基准值增加半个实际分片时长且最多增加 3 秒，使首个切割阈值提前到完整原生分片之前。首段因此在客户端启动窗口内完成，后续仍按原生 `segment_time` 切割，同时避免从零时间轴追赶产生大量极短分片。

## 8. 并发与生命周期

- 样本请求复用网关并发上限；并发上限大于 1 时至少为播放正文保留 1 个槽位；
- 同一来源、媒体源、起播时间、媒体时长和运行时代际的并发预检共享协调器拥有的任务；
- 同一来源、媒体源、目标、媒体时长和运行时代际的并发校正跨绑定共享同一个任务，完成后还会针对每个绑定复验文件长度、包格式、PCR PID 和时间线原点；
- 初始准备和绑定目标校正各自最多保留 64 个在途任务；
- 过期计划在读取和维护周期中清理；
- 绑定校准可服务同一 Emby 作业的安全重试和 HLS 分片跳转，并保持 2 分钟绝对期限；
- 运行时代际写入计划，旧代际计划不可绑定或应用；
- 单个请求取消只结束该请求的等待，不会取消其他会话正在共享的准备；协调器超时、运行时失效或释放会取消底层任务，并且不会留下半成品计划。

## 9. 安全与隐私

- 每一跳重定向继续执行现有 scheme、降级、userinfo、fragment 和信任主机策略；
- 样本请求仅访问当前 STRM 指纹绑定的来源；
- 初始两/三个样本共享一个 5 秒准备时限，每个新目标的一/二次校正样本也共享独立的 5 秒时限；
- 探测产生的来源候选仅在内存保留 30 秒；服务端 FFmpeg 正式请求携带自己的 User-Agent，并以准确的 `206 Content-Range` 验证候选，验证失败时从原始来源重新解析；客户端直放不会读取该候选；
- 计划键使用 HMAC 来源指纹、媒体源身份、目标、媒体时长和运行时代际，不使用原始地址、票据或用户身份；
- 日志只记录固定事件名、原因码、包步长、探测次数、相对预滚和时间线契约；
- 日志禁止记录 URL、Location、查询参数、Authorization、Cookie、User-Agent、文件路径和媒体标题；
- FFmpeg 输入保持回环网关 URL，最终签名地址不会进入 FFmpeg 命令行；
- 样本字节在解析完成后由托管内存释放，不写入磁盘。

## 10. 原生回退

以下结果直接结束快速路径，并保持 Emby 原生命令：

- HTTP 状态或 Range 元数据不符合要求；
- 重定向策略拒绝；
- 文件长度、时长或目标时间越界；
- TS 包结构、PCR PID或时间线无法稳定识别；
- 校正误差、字节速率或预滚超出边界；
- 来源指纹、媒体源 ID、运行时代际或回环输入 URL 不一致；
- 后续 HLS 目标的 PCR 校正无法在文件、时长和预滚边界内成立；
- 原生时间差契约与索引完整转码契约均不能证明输入绝对 `ss`；
- FFmpeg 命令不是 HTTP 输入加 segment muxer；
- Harmony ABI 检查失败；
- 请求取消、超时、容量限制或运行时释放。

快速路径不修改媒体库项目、STRM 文件、持久化媒体信息或全局 FFmpeg 配置。

## 11. 可观测性

固定事件：

- `STRM_BRIDGE_FAST_SEEK_READY`：计划创建完成；
- `STRM_BRIDGE_FAST_SEEK_APPLIED`：命令修改完成；
- `STRM_BRIDGE_FAST_SEEK_SKIPPED`：带固定 `reason` 的原生回退；
- `STRM_BRIDGE_PATCH_ABI_UNSUPPORTED`：运行时补丁模型不匹配。

成功事件、命令一致性失败、网络失败、媒体不匹配和普通回退使用 Debug 级别；ABI 不匹配使用 Warning 级别。

## 12. 验收矩阵

自动化测试覆盖：

- 188、192、204 字节包同步和 PCR 解析；
- PCR 环回、截断包、错误同步和多 PID 选择；
- 自适应两/三样本校准、边界拒绝、取消和过期；
- 在途任务容量、并发共享、清理期间防止迟到写回和精确绑定；
- FFmpeg 六字段原子修改、失败恢复和首段时间容差上界；
- 连续不同目标、相同目标缓存与可变码率 PCR 校正；
- 非 segment、非 HTTP、目标时间不一致和未知 URL 原生回退；
- 日志与发布 DLL 的 URL、凭据和绝对路径扫描；
- 现有直接播放、标准静态路由、HLS 重写、媒体信息提取和配置测试无回归。

真实 Emby 验收步骤：

1. 启动日志显示 Emby 4.9.5.x 和五个补丁目标；
2. 从大于 10 秒的位置播放远程 M2TS；
3. 日志依次出现 `FAST_SEEK_READY` 和 `FAST_SEEK_APPLIED`；
4. FFmpeg 命令的输入 URL 前无输入级 `-ss`，输出侧包含短预滚 `-ss`，且日志不出现时间搜索失败；
5. 上游监控中每个 FFmpeg 作业的媒体正文是一个连续 Range 请求，拖动后的作业继续命中快速定位；
6. 首个可播放 HLS 分段在客户端放弃窗口内生成，后续分段保持原生时长；
7. 起播画面和音频位置与 Emby 原生基准一致；
8. 从零播放、普通 MKV、本地文件和未选择媒体库保持原生行为。

---

# STRM Remote Transport-Stream Fast Seek Design

This document is the implementation contract for non-zero playback of remote MPEG-TS/M2TS sources. Code, tests, and release notes follow the activation rules, data bounds, fallback behavior, and acceptance criteria defined here.

## 1. Objective

For an HLS job created by Emby from an eligible remote STRM transport stream, the plugin converts FFmpeg's remote timestamp search into one calibrated byte-range read. Emby's native media source, transcoding profile, tracks, subtitles, session, and HLS output remain authoritative.

Acceptance requires one continuous media-body Range read, a short pre-roll before the requested position, an aligned HLS timeline, atomic native fallback, capability-based decisions without client or host names, and memory-only handling of sensitive addresses.

## 2. Activation contract

The fast path requires all of the following:

1. enabled `Adaptive` routing;
2. an exact current local STRM source in a participating library;
3. an actual Emby server-side FFmpeg job for the selected media source;
4. that job's `StartTimeTicks` greater than ten seconds and within the media duration;
5. a valid HTTP partial response with a known total length;
6. content recognized as 188-, 192-, or 204-byte transport-stream packets;
7. a stable PCR PID across sparse samples;
8. a segmented HLS command whose input is the exact loopback route issued for the job;
9. the validated Emby 4.9.5.x command ABI.

Every other request follows Emby's native behavior.

## 3. Architecture

`TransportStreamClockParser` recognizes packet framing and PCR clocks. `GatewayFastSeekProbeClient` performs bounded, policy-checked Range reads. After Emby has selected a source and created transcode state, `TranscodeInputProcessor` asks `FastSeekCoordinator` to calibrate a short-lived plan and binds it to the exact loopback job URL. A later HLS target receives one bounded PCR correction probe and at most one retry when the result falls outside the safe pre-roll window. A validated source-and-target plan can be shared across exact compatible playback bindings. Direct-play PlaybackInfo requests do not run these probes. `FfmpegCommandProcessor` atomically applies the target plan immediately before process startup. `HarmonyPatchHost` validates and owns the fifth patch target.

## 4. Plan data and bounds

A plan contains only the HMAC source fingerprint, media-source ID, requested ticks, duration, total length, byte offset, packet stride and origin, PCR PID, timeline-origin packet and PCR clock, bounded average byte rate, relative pre-roll, runtime generation, and timestamps. It contains no final URL, STRM text, query, header, user, title, or local path.

Plans use a preparation key `(source fingerprint, media-source ID, StartTimeTicks, duration, runtime generation)` to coalesce compatible preparation across sessions, followed by an exact loopback-input binding. The binding retains the calibration and up to sixteen corrected target plans. Shared reuse additionally verifies total length, duration, packet format, PCR PID and timeline origin before each binding stores the result. Each top-level index holds at most 512 entries for an absolute two-minute lifetime and clears during maintenance, runtime invalidation or disposal. Each probe reads at most 512 KiB; initial preparation normally uses two samples and adds a third when estimated pre-roll falls outside the preferred 0.25-to-6-second window; each previously unseen later target uses one sample and at most one bounded retry. Shared work has a coordinator-owned five-second deadline, while cancellation by one waiter does not cancel other waiters.

## 5. Calibration

The first sample establishes framing, PCR PID, initial PCR, and total length. The second sample is placed at `length × (target - four-second pre-roll) / duration`. Their byte and PCR deltas produce a bounded average byte rate. A relative seek between 0.25 and 6 seconds completes the two-sample plan. A larger estimate triggers a third sample at the PCR-corrected position; its PCR and distance from the sample boundary produce a corrected relative seek accepted between 0.25 and 12 seconds. A later target estimates an aligned offset from the saved timeline origin and byte rate, then reads one bounded sample. If its actual PCR places the target outside the 0.25-to-12-second window, that PCR produces a corrected byte position for one final bounded sample. A successful target plan enters the source-scoped shared index and each compatible exact input binding. Repeating the same target uses the plan without network I/O.

PCR calculations use the 27 MHz clock and MPEG-TS wrap modulus. Candidate calculations are bounded before conversion and packet alignment.

## 6. Command transformation

The processor accepts either of two proven timelines. The native contract has an HTTP input at byte zero, absolute input-side `ss=T`, and `segment_time_delta=-T`. The indexed full-transcode contract has no native delta and requires `copyts`, `start_at_zero`, disabled negative-timestamp rewriting, no competing input or muxer offsets, and `segment_start_number * segment_time ~= T`. Every time comparison uses a 250-millisecond tolerance. The fast path sets the HTTP protocol `offset`, sets `seekable=false`, clears the input-side `ss`, sets output-side `ss` to the calibrated relative pre-roll, and sets `output_ts_offset=T`. Moving the trim after input opening prevents the transport-stream demuxer from initiating another timestamp seek; it discards the short pre-roll sequentially instead. The output timestamp offset preserves the absolute HLS timeline. The native contract supplies the delta base directly; the indexed contract derives the equivalent base from its proven absolute target. Emby's original `segment_start_number` stays unchanged, while the segment delta becomes `-T + min(segment_time / 2, 3 seconds)`. This bounded shift closes the first segment within the startup window; later segments retain the native cadence, and the negative absolute-timeline delta prevents a burst of tiny catch-up segments. All remaining Emby options stay unchanged.

## 7. Safety and fallback

Redirect policy, source identity, media-source identity, target time bounds, runtime generation, loopback URL, protocol key, muxer key, one of the two timeline contracts, and writable command fields are checked before mutation. Indexed commands also require the exact timestamp flags, absent competing offsets, and segment-number arithmetic. All original field values, including an absent native delta, are restored if a mutation fails. Any probe, parser, timing, ABI, cancellation, timeout, or capacity failure retains the native command.

The initial two or three samples share a five-second preparation deadline, and each target's one or two correction samples share the same independent bound. Expiry immediately retains the native command. A successful probe seeds a 30-second, source-scoped redirect candidate. The server-FFmpeg media request retains its own User-Agent and accepts the candidate only when the response honors the exact requested byte range; otherwise it resolves from the original source. Direct-client requests never consume this candidate. When configured relay concurrency is above one, probes leave one slot available for playback delivery. The final upstream address remains behind the loopback gateway and never enters the FFmpeg command line. Logs contain fixed events and bounded diagnostics only; URLs, locations, queries, credentials, headers, local paths, titles, and player identifiers are excluded.

## 8. Verification

Unit and integration tests cover packet sizes, PCR wrap and corruption, multi-PID selection, adaptive calibration bounds, one/two-sample target correction, cross-session plan sharing, independent waiter cancellation, playback-slot reservation, same-target caching, variable-bitrate drift, cross-User-Agent candidate validation and fallback, absolute output-timestamp preservation, bounded first-segment delta, plan lifetime and binding, atomic command mutation, native fallback, privacy scans, and all existing playback and extraction behavior. Live acceptance requires five installed patch targets, initial and post-seek jobs reporting fast-seek application, no input-side `-ss` or timestamp-seek failure on an applied job, absolute and increasing HLS segment timestamps, a bounded first segment followed by native cadence, no burst of tiny catch-up segments, at most two bounded correction probes per new target, one continuous upstream media Range per FFmpeg job, HLS startup inside the client deadline, position accuracy, and unchanged behavior for zero-start, non-TS, local, and excluded content.

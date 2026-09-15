# 0.2.5 — Web 内嵌文字字幕

新增默认关闭的内嵌文字字幕功能：符合范围的 MKV STRM 在 Emby Web 中自动带出 ASS/SSA/SubRip 字幕，支持切轨和连续跳转；加载、重试与失败处理保持静默。

- 视频和字幕共用 Emby 的一个 FFmpeg 媒体输入，字幕从本地输出读取，不额外打开远程媒体。
- 使用 hls.js 当前连续段的实际时间原点，避免跳转后不同视频任务导致字幕偏移；补齐缓存回收、过期请求隔离及失败清理。
- 字幕补丁独立于主播放补丁，依赖不完整时不接管；第三方播放器继续使用原有播放方式。

字幕适配精确核对的 Emby **4.9.5.0 / 4.10.0.40 Web 资源**，仅接管宿主实际使用 hls.js 的播放。命中的 Web 视频经 Emby 服务端传输，音视频是否复制或编码由宿主能力及权限决定。原生 HLS 专用路径、图形字幕等保留 Emby 自身处理。范围、容量及长字幕限制见[字幕说明](https://github.com/Kectai/emby-strm-bridge/blob/v0.2.5/docs/SUBTITLES.md)。

停止 Emby，备份旧 DLL、插件配置及恢复数据，替换 DLL 后重启并强制刷新网页。从 0.2.4 或使用 schema 3 的预发布版本升级，无需额外快照迁移。首次使用需开启“内嵌文字字幕”并选择媒体库，详见[安装说明](https://github.com/Kectai/emby-strm-bridge/blob/v0.2.5/docs/INSTALL.md)。

636 项 C# 与 49 项前端测试、两版实际宿主检查及浏览器合成媒体回归通过；已核对验收构建的 Safari、Chrome、IINA 实片播放及多次跳转。已知的来源限流与客户端缩略图竞争仍适用[临时方案](https://github.com/Kectai/emby-strm-bridge/blob/v0.2.5/docs/COMPATIBILITY.md#client-generated-seek-thumbnails)。

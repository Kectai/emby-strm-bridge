# STRM Bridge 0.2.3

## 中文

本版为正式版本，适配 Emby Server 4.9.5.x。

### 主要改进

- 修复大 HLS 清单反复全局清理造成的性能问题，保留动态窗口回收、资源到期和多会话额度校验。
- 修复音频 STRM 提取、强制探测未读取输入却记成功，以及技术快照丢失 HDR／旋转等字段的问题。
- 修正服务端重定向有效期及首次 Range 响应校验，完善普通文件首跳缓存、定位恢复和作业资源回收。

### 升级与已知限制

停止 Emby，备份旧 DLL、插件配置及恢复数据，替换 DLL 后重启。快照采用 schema 3，旧快照不恢复；缺失项目会重新探测，已完整项目需显式刷新。从采用 schema 3 的预发布版本升级，无需额外快照迁移。详细步骤见[安装文档](docs/INSTALL.md)。

部分客户端生成进度条缩略图时会增加媒体读取，在来源限制并发或请求频率时可能持续缓冲、拖动卡住或被拒绝。遇到此类问题，可先关闭缩略图／实时预览。重定向交接后的客户端读取不受插件调度，切换中转也不保证解决。详见[兼容性](docs/COMPATIBILITY.md#client-generated-seek-thumbnails)。

本版经过自动化及实际宿主／FFmpeg 离线检查。部署后的实际播放检查及验证范围见[测试文档](docs/TESTING.md#release-readiness)。

## English

A stable release for Emby Server 4.9.5.x.

### Changes

- Remove repeated global cleanup from large HLS manifests while retaining window retirement, expiry checks and session quotas.
- Fix audio STRM extraction, false fresh-probe success without input access, and missing HDR/rotation snapshot fields.
- Correct server redirect lifetimes and initial Range validation; improve first-hop caching, native seek recovery and job-resource cleanup.

### Upgrade and limitations

Stop Emby, back up the previous DLL, plugin configuration and recovery data, replace the DLL and restart. Schema 3 rejects older snapshots: missing items are reprobed, while complete items require explicit refresh. Updating from prerelease builds already using schema 3 requires no further snapshot migration. See [installation](docs/INSTALL.md).

Client-generated seek thumbnails may add enough media reads to trigger source concurrency or request-rate limits. If affected, try disabling seek thumbnails/live previews. The plugin cannot schedule reads after direct handoff, and relay mode is not a guaranteed fix. See [compatibility](docs/COMPATIBILITY.md#client-generated-seek-thumbnails).

Automated and actual-host/FFmpeg offline checks have passed. Deployment checks and validation boundaries are documented in the [live matrix](docs/TESTING.md#release-readiness).

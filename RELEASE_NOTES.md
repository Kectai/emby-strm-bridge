# STRM Bridge 0.2.4

## 中文

本版新增 Emby Server 4.10.0.40 兼容，继续支持 4.9.5.x。

### 主要改进

- 修复升级到 4.10 后因版本门控拒绝安装播放补丁的问题，允许 4.10.0 分支的 40 及以后修订版。
- 保留六个补丁入口的精确签名、必要成员及补丁所有权校验；未知版本分支、早期 4.10 预览版和不兼容接口仍回退原生播放。
- 增加版本边界回归测试和可重复运行的实际宿主隔离检查。

### 升级与已知限制

停止 Emby，备份旧 DLL、插件配置及恢复数据，替换 DLL 后重启。从 0.2.3 升级无需调整配置或迁移快照。详细步骤见[安装文档](https://github.com/Kectai/emby-strm-bridge/blob/v0.2.4/docs/INSTALL.md)。

部分客户端生成进度条缩略图时会增加媒体读取，在来源限制并发或请求频率时可能持续缓冲、拖动卡住或被拒绝。遇到此类问题，可先关闭缩略图／实时预览。重定向交接后的客户端读取不受插件调度，切换中转也不保证解决。详见[兼容性](https://github.com/Kectai/emby-strm-bridge/blob/v0.2.4/docs/COMPATIBILITY.md#client-generated-seek-thumbnails)。

另有拖动后偶发 HTTP 403 的记录，升级前也曾出现，触发条件尚未确定，本版未将其列为已修复问题。

自动化及实际宿主离线验证范围、部署后的播放检查见[测试文档](https://github.com/Kectai/emby-strm-bridge/blob/v0.2.4/docs/TESTING.md#release-readiness)。后续 4.10.0 修订版仍需通过运行时校验；这不代表逐版完成实机验收。

## English

Adds Emby Server 4.10.0.40 compatibility while retaining 4.9.5.x support.

### Changes

- Fix playback patches being disabled by the version gate after upgrading to 4.10; admit revision 40 and later on the 4.10.0 line.
- Retain exact signatures, required-member checks and ownership verification for all six patch targets. Unknown release lines, early 4.10 previews and incompatible interfaces remain native.
- Add version-boundary regressions and repeatable isolated checks against actual host assemblies.

### Upgrade and limitations

Stop Emby, back up the previous DLL, configuration and recovery data, replace the DLL and restart. Upgrading from 0.2.3 requires no configuration or snapshot migration. See [installation](https://github.com/Kectai/emby-strm-bridge/blob/v0.2.4/docs/INSTALL.md).

Client-generated seek thumbnails may add enough media reads to trigger source concurrency or request-rate limits. If affected, try disabling seek thumbnails/live previews. The plugin cannot schedule reads after direct handoff, and relay mode is not a guaranteed fix. See [compatibility](https://github.com/Kectai/emby-strm-bridge/blob/v0.2.4/docs/COMPATIBILITY.md#client-generated-seek-thumbnails).

Intermittent HTTP 403 after seeking was also observed before the server upgrade. Its trigger remains undetermined and this release does not claim to fix it.

See [testing](https://github.com/Kectai/emby-strm-bridge/blob/v0.2.4/docs/TESTING.md#release-readiness) for automated/offline evidence and deployment checks. Later 4.10.0 revisions must still pass runtime validation; they have not each undergone live acceptance.

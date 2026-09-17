# 0.2.6 — 外挂字幕同步修复

修复 Emby 网页播放中外挂 ASS/SSA 字幕在续播、拖动后时间错位或残留旧字幕的问题，改善切换字幕和异常恢复。沿用原有字幕选择和手动延迟设置。

需开启插件的“内嵌文字字幕”开关，适用于已适配的 Emby 4.9 / 4.10 网页播放。具体范围见[字幕说明](https://github.com/Kectai/emby-strm-bridge/blob/v0.2.6/docs/SUBTITLES.md)。

升级：停止 Emby，备份并替换插件 DLL，重启后强制刷新网页。

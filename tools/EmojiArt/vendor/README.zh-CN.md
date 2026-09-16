# Emoji Kitchen 兼容表

`emoji-compatibility.js` 的数据来自 [MattFor/emoji-mixer](https://github.com/MattFor/emoji-mixer)，版本 1.3.1，固定提交 `86b13247f89f7b55edcaef9dabc1bd830f5fa1a1` 的 `compatibility.json`。原 JSON 外包一层 `globalThis.emojiCompatibility = …`，以支持不启动服务器、直接打开本地 HTML；未引入其运行时代码或 npm 依赖。许可随附于 `emoji-mixer-LICENSE.txt`。

`$e` 保存 emoji 码点，`$d` 保存图像发布日期；其他键的每行 `[emojiIndex, dateIndex]` 对应该键与 `$e[emojiIndex]` 的组合。查询检查双向并保留图像 URL 的原始顺序，选择表中最新日期。项目中已缓存的卡图优先保持已有版本。

需要更新兼容范围时，从明确的上游提交获取 `compatibility.json`，替换包装中的数据，同时更新本文件、脚本头部的提交号及许可；再运行 `node tools/EmojiArt/verify.mjs`。此操作不批量下载图片。

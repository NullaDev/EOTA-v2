# 随仓库共享的融合原图

将网页“本机融合缓存与离线分享 → 导出本机融合缓存”下载的 JSON 放在此目录，可按作者或批次重命名。这里保存融合原图，不限于已经制作成卡牌的组合。文件格式为 `eota-emoji-fusion-cache`，包含 emoji、来源 URL 和 PNG 数据。

运行 `node tools/EmojiArt/build-fusion-cache.mjs` 后，内容会合并进 `tools/EmojiArt/fusion-cache.js`。将本目录 JSON、更新后的 `fusion-cache.js` 以及工具代码一同提交。接收者下载完整仓库后，直接打开 `tools/EmojiArt/index.html` 即可离线使用这些组合，不需要你的浏览器缓存，也不需要自己重建。

浏览器不会自动写入本目录。未收进仓库的组合首次使用仍需联网；这个目录不是全部 Emoji Kitchen 图片的镜像。相同来源去重，内置卡牌已固定的图片版本优先保留。

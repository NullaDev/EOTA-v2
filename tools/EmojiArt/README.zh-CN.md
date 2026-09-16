# Emoji 卡图

用浏览器打开 [index.html](index.html)，输入 emoji 后下载 PNG。支持渐变卡图、透明图标、配方 JSON 导入和 Emoji Kitchen 融合图预览。无需启动服务器。游戏图标由独立配方维护，生成的 PNG 供客户端渲染。

批量生成需要 Node.js 22+ 和本机 Chrome／Edge：

```powershell
node tools/EmojiArt/export.mjs Content/Source/Art/emoji-recipes.json Content/Generated/Art
node tools/EmojiArt/export.mjs Content/Source/Art/ui-icons.json Content/Generated/Icons
```

批量导出优先读取 `Content/Source/Art/Fusions` 中已缓存的透明 PNG，因此不联网也能重建；没有 `fusion` 的配方必须只含一个 Unicode 字素，不允许把两个独立 emoji 并排当成卡图。网页直接载入配方时，会通过 `fusionSource` 在线预览融合图。

详见 [内容工具说明](../../Docs/Content/Authoring-Tools.zh-CN.md)。普通 emoji 外观跟随系统字体；融合结果使用缓存素材，跨系统保持一致。

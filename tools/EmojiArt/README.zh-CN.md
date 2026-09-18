# Emoji 卡图

用 Chrome／Edge 打开 [index.html](index.html)，无需启动服务器。支持单个 emoji、双 emoji 合并、渐变卡图、透明图标、卡图配置 JSON 导入和 PNG 下载。此工具仅供开发制作素材，不随 Godot 游戏发布包分发。

在“图案来源”中选择“双 emoji 合并”，分别输入两个 emoji，或从“可合并的第二个 emoji”列表选择，然后点击“合并 emoji”。预览完成后选择职业、尺寸和样式，点击“下载 PNG”。卡图背景严格跟随职业：守卫蓝、奥术师紫、工匠棕、猎人绿、中立灰棕，同职业所有类型与衍生卡保持一致。透明图标不绘制背景。组合不支持或网络失败时会显示原因，并禁用下载，避免误导出之前的预览。单个输入框支持带变体选择符或连接符的完整 emoji；输入两个独立符号需使用融合模式。

职业颜色统一定义在 `Content/Source/Art/profession-palettes.json`。调整职业颜色或新增卡牌后，运行 `node tools/EmojiArt/sync-profession-colors.mjs`，它按卡牌的真实 profession 更新配置，并生成网页使用的 `profession-palettes.js`。内容构建会拒绝职业或颜色不一致的配置；随后运行 `./tools/Build-Content.ps1 -WithPng` 更新游戏图像。两个职业配色文件及更新的配置、PNG 都应一并提交。

页面顶部“从已有卡图配置开始”用于导入 `emoji-recipes.json` 或 `ui-icons.json`。配置记录绘图所用的 emoji、融合来源、配色、样式和文件名；导入后选条目，会填入下方编辑区域。它不导入 PNG，也不导入游戏的卡牌内容包；无需加载配置即可从空白设置开始制作。

## 缓存在哪里

兼容表约有 14.7 万种组合，只保存可组合关系和原图地址，**没有下载全部图片**。图片按以下顺序读取：

| 位置 | 保存内容 | 是否随仓库分发 |
|---|---|---|
| `Content/Source/Art/Fusions` | 251 张现有卡牌融合原图，重复来源去重后为 235 张 | 是 |
| `tools/EmojiArt/shared-cache/*.json` | 从网页导出的其他组合原图，不限于现有卡牌 | 将文件提交后才会分发 |
| `tools/EmojiArt/fusion-cache.js` | 上述素材合并后的网页离线数据 | 是；接收者直接打开 HTML 即可使用 |
| 浏览器 IndexedDB：`eota-emoji-fusions-v1` → `images` | 当前浏览器首次联网获取或导入的其他组合 | 否；需显式导出 |

浏览器持久缓存由 Chrome／Edge 管理，可在开发者工具 Application → IndexedDB 中查看。重新打开同一浏览器、同一工具地址可复用；清理网站数据、隐私模式或存储回收可能使其丢失，因此重要素材应导出备份。持久存储不可用时，页面会说明只能临时使用，并保留 PNG 下载能力。浏览器不会自动写入源码目录。

“本机融合缓存与离线分享”可导出包含原图的 JSON，交给另一台电脑导入后即可离线使用。此文件与卡图配置不同，必须在缓存区导入；单次导入限 100 MiB，按来源 URL 合并。该功能不预下载全部组合。

## 让仓库下载者也能使用

提交完整的 `tools/EmojiArt` 目录，包含 `vendor` 兼容表、许可、全部脚本和 `fusion-cache.js`。接收者无需 npm 安装，直接打开 HTML；仓库已包含的组合可离线使用，未包含的组合首次使用需要能访问 `www.gstatic.com`。仅提交兼容表无法保证所有图片离线可用。

如需让自己使用过的新组合也随仓库提供：

1. 在网页缓存区点击“导出本机融合缓存”。
2. 将下载的 JSON 放到 `tools/EmojiArt/shared-cache/`。
3. 运行下面的重建命令。
4. 将该 JSON 和更新后的 `fusion-cache.js` 一起提交。下载者不会依赖你的本机存储。

更新卡图配置、融合 PNG 或共享缓存后，在仓库根目录重建网页缓存：

```powershell
node tools/EmojiArt/build-fusion-cache.mjs
```

兼容表快照及许可见 [vendor](vendor/README.zh-CN.md)。它包含可组合关系，图片按需加载，不需要 API Key 或 npm 安装。

批量生成需要 Node.js 22+ 和本机 Chrome／Edge：

```powershell
node tools/EmojiArt/export.mjs Content/Source/Art/emoji-recipes.json Content/Generated/Art
node tools/EmojiArt/export.mjs Content/Source/Art/ui-icons.json Content/Generated/Icons
```

批量导出优先读取 `Content/Source/Art/Fusions` 中已缓存的透明 PNG，因此不联网也能重建；没有 `fusion` 的配方必须只含一个 Unicode 字素，不允许把两个独立 emoji 并排当成卡图。

浏览器验收运行 `node tools/EmojiArt/verify.mjs`，覆盖离线缓存、新组合联网、实际 PNG 下载、配置导入、不支持的组合、网络失败与异步切换；还验证页面重载后的持久缓存、全新浏览器环境中的缓存导入，以及加入共享素材后的独立工具副本断网运行。需要本机 Chrome／Edge，在线用例需要访问图片服务；日志、截图与下载结果保存到 `artifacts/emoji-art-check-*`。

详见 [开发工具与内容制作](../README.zh-CN.md)。普通 emoji 外观跟随系统字体；融合结果使用缓存素材，跨系统保持一致。

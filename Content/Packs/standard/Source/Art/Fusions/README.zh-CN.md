# Emoji Kitchen 融合素材

本目录保存卡图生成器使用的透明融合 PNG。当前共 251 张，只对应 `emoji-recipes.json` 中带 `fusion` 的条目；其余 31 张卡牌使用单个 Unicode emoji，不再并排绘制两个独立符号。复杂卡名和复合效果优先使用融合图，例如困倦搬运工使用打哈欠的纸箱组合；白板随从、语义简单或缺少准确组合的卡继续使用单个 emoji，例如原野巨犀使用 `🦏`。五个职业的 50 张基础卡与 2 张衍生卡全部使用融合图；灵魂使的 52 张卡在后续补齐作图时逐张挑选组合，没有合适配方才退回单 emoji。融合图按卡牌 ID 命名，与配方的 `fusionSource` 一一对应，重复组合只下载一份原图。

融合兼容关系使用 [MattFor/emoji-mixer](https://github.com/MattFor/emoji-mixer) 1.3.1 核对。该查表工具采用 MIT 许可；图片来自 Google Emoji Kitchen 的 `www.gstatic.com/android/keyboard/emojikitchen` 路径。每张图的完整原始 URL 保存在相应配方的 `fusionSource` 字段。

这些文件是内部测试期的美术素材。批量构建只读取本目录，不自动联网更新；这样即使上游兼容表或图片发生变化，当前卡图仍可重复生成。公开发行前需单独复核 Emoji Kitchen 图片的发行许可。

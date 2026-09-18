# 卡表与 emoji 素材工具

卡牌字段、完整效果语法、内容包与协议的写法见 [Mod 开发者 JSON 编写指南](../CardJsonDesignGuide.zh-CN.md)。本页侧重素材生成、源码构建和客户端编辑器操作；可编译示例见 [Examples](Examples/README.zh-CN.md)。

当前 282 张卡按 [职业体系](CardSet-Redesign.zh-CN.md) 设计，平衡仍在迭代。规则 JSON、中文文本与卡图配方分别维护。卡图按卡名选择 emoji；新增卡没有合适符号时可使用职业默认图案，法术可用快速 ⚡／慢速 ⏳。

## 一键生成卡表

在仓库根目录运行：

```powershell
./tools/Generate-CardTable.ps1
```

结果是 [Docs/CardTable.zh-CN.md](../CardTable.zh-CN.md)。每次重新读取并编译 Content/Source/Cards、中文本地化和卡图配方，按来源／职业／类型／费用／ID 排序。修改数值或描述后再次运行即可；不要直接编辑生成的 Markdown。名称／描述缺失、重复卡牌、非法效果或无效配方会使生成失败。

同时更新内容构建、差异与能力报告：

```powershell
./tools/Build-Content.ps1
./tools/Build-Content.ps1 -WithPng
```

第一条需要 .NET 8，输出可重复构建的规则二进制、可重编译 JSON、表现清单、中文文本、SVG 预览和报告。第二条还使用 Node.js 22+ 及本机 Chrome／Edge 重新生成 PNG 卡图和透明图标。浏览器在后台启动临时配置并自动退出，不使用现有浏览器用户资料。融合图从 `Content/Source/Art/Fusions` 的缓存读取，批量构建不安装 npm 包，也不访问图片服务。

PNG 是游戏可用的素材；SVG 中的 emoji 文字仅用于浏览器预览，不能假设所有 SVG 游戏导入器支持 emoji 字体。

## 交互生成卡图与图标

卡图渐变背景统一由 `Content/Source/Art/profession-palettes.json` 定义：守卫蓝、奥术师紫、工匠棕、猎人绿、灵魂使青绿、中立灰棕。职业由卡牌的 profession 字段决定，同职业随从、法术、场地与衍生卡保持一致。网页选择职业后自动应用配色；更改色板或新增卡牌后运行 `node tools/EmojiArt/sync-profession-colors.mjs` 同步配置及网页色板，再通过 `Build-Content.ps1 -WithPng` 重建。内容编译会拒绝职业配色不匹配的卡图配置。

直接用 Chrome／Edge 打开 [Emoji 卡图工具](../../tools/EmojiArt/index.html)，无需启动服务器。在“图案来源”中选择“单个 emoji”或“双 emoji 合并”。合并模式分别输入两个 emoji，也可从“可合并的第二个 emoji”列表选取，点击“合并 emoji”预览。选择卡牌职业和 256／512／1024 尺寸后点击“下载 PNG”；支持带职业渐变背景的卡图，以及透明背景的图标。此工具保留在开发仓库，Godot 游戏发布包不包含素材制作工具。

页面顶部“从已有卡图配置开始”可导入 [卡图配置](../../Content/Source/Art/emoji-recipes.json) 或 [图标配置](../../Content/Source/Art/ui-icons.json)，选条目后填入下方的 emoji、融合来源、配色和文件名；这里不导入 PNG 或游戏内容包。文件名经过清理；输入作为文本绘制，不作为 HTML 执行。现有融合图由网页缓存优先提供，直接打开本地网页也能离线使用；其他组合按内置兼容表通过 HTTPS 获取，不支持的组合与下载失败会显示提示。新下载的原图写入浏览器 IndexedDB `eota-emoji-fusions-v1` 的 `images`，关闭页面后可复用；浏览器清理数据可能删除它，应导出备份。不会自动写入源码目录。批量导出始终使用配置中指定的本地 `fusion` 文件。

网页的 `tools/EmojiArt/fusion-cache.js` 从现有配置、`Content/Source/Art/Fusions` 和 `tools/EmojiArt/shared-cache/*.json` 生成，基础素材包含 187 张去重图片，对应 199 张融合卡图。网页缓存区可导出、导入其他组合的原图。要把新素材交给仓库下载者，将导出的 JSON 放进 `shared-cache`，运行 `node tools/EmojiArt/build-fusion-cache.mjs`，然后一并提交 JSON、生成的 `fusion-cache.js` 和完整工具目录。兼容表约有 14.7 万种组合，仓库并未下载所有原图；未收进仓库的组合首次使用仍需联网。兼容表来源和版本见 [工具说明](../../tools/EmojiArt/vendor/README.zh-CN.md)。

批量导出：

```powershell
node tools/EmojiArt/export.mjs Content/Source/Art/emoji-recipes.json Content/Generated/Art
node tools/EmojiArt/export.mjs Content/Source/Art/ui-icons.json Content/Generated/Icons
```

导出器自动寻找常见浏览器路径；其他安装位置可作为第三个参数传入，或设置 EOTA_EMOJI_BROWSER。所有批量条目均使用与网页相同的 canvas 绘制函数。

当前生成的图标是供客户端单独渲染的 PNG 素材，位于 Content/Generated。配方键即图标 ID，对应 Icons/{id}.png，P9 已接入客户端资源与界面：

| 文件 | 含义 | Emoji |
|---|---|---|
| Icons/fast-spell.png | 快速法术 | ⚡ |
| Icons/slow-spell.png | 慢速法术 | ⏳ |
| Icons/frozen.png | 路冻结：该路所有随从无法攻击 | ❄️ |
| Icons/locked.png | 路锁闭：该路无法进入或离开 | 🔒 |
| Icons/ether.png | 以太 | 🌊 |

冻结／锁闭作用于整条路、影响双方，图标显示在路的状态区域。P9 已补齐通用路状态，根据服务端公开状态显示和更新图标；语义见 [ADR-027](../ADR/027-lane-statuses.md)，接入及验证见 [P9 验收](../Review/P9-Godot-Review.zh-CN.md)。

当前 282 张卡中有 199 张使用 Emoji Kitchen 双 emoji 融合图，另外 83 张使用单个 Unicode emoji；不再把两个独立 emoji 并排缩小绘制。复杂卡名和复合效果优先寻找融合图，例如困倦搬运工使用打哈欠的纸箱组合；白板随从、语义简单或没有准确组合的卡保留单个 emoji，例如原野巨犀使用 `🦏`。融合只替换中央图案；职业渐变背景、圆形光环、卡面名称、说明和数值布局保持原样。

融合兼容关系通过 MIT 许可的 [emoji-mixer](https://github.com/MattFor/emoji-mixer) 核对，实际透明图由 Google Emoji Kitchen 提供并缓存到源码目录。每条配方保留原 emoji、缓存路径和原始 `gstatic.com` URL，便于审计或恢复。具体来源说明见 [融合素材目录](../../Content/Source/Art/Fusions/README.zh-CN.md)。这些融合图当前用于内部测试素材；公开发行前应再次确认 Emoji Kitchen 图像的发行许可。

普通 PNG 外观由当前系统 emoji 字体及浏览器决定，跨系统重新绘制未必逐像素相同；融合 PNG 已缓存，因此保持一致。规则包与卡表的可重复构建不依赖浏览器或字体。

开发环境中的客户端会直接读取 `Content/Generated` 下的当前 PNG，避免 Godot 继续显示旧 `.ctex` 导入缓存；发布后的资源包仍由 `ResourceLoader` 读取构建时导入的贴图。

卡图源素材为 512×512 方形，客户端按原比例显示；整张卡牌的插图区、名称、描述及数值布局由场景控制。

## 内容文件与构建产物

| 路径 | 用途 |
|---|---|
| Content/Source/Cards | 可直接维护的 282 份 V2 规则 JSON |
| Content/Source/Decks/archetypes.json | 十五套体系模板，构建后生成 archetype-decks.json 供客户端读取 |
| Content/Source/Localization/zh-CN.json | 564 个名称／描述条目 |
| Content/Source/Art/emoji-recipes.json | 卡名对应的 emoji 与配色，和规则分离 |
| Content/Source/Art/Fusions | 199 张已选择的透明融合图及来源说明 |
| Content/Source/Art/ui-icons.json | 五个透明图标的配方 |
| Content/Design/card-baseline.json | 实验卡的数值与文本设计基线；不参与运行规则 |
| Content/Generated/rules.bin | RuleContentPack 的完整 canonical 字节，SHA-256 等于 ruleHash |
| Content/Generated/cards.sources.json | 可直接重编译的源文档集合；不冒充二进制解码器 |
| Content/Generated/presentation.json | 名称键、描述键、PNG 路径和 SVG 预览路径 |
| Content/Generated/manifest.json | 数量、实验状态、规则哈希与含文本／配方的表现哈希 |
| Content/Generated/BaselineDiff.zh-CN.md | ID／数量及数值／文本的基线差异 |
| Content/Generated/Coverage.zh-CN.md | 编译后 IR／Intent 使用关系和未使用能力 |

实验卡可以继续平衡调整。差异报告帮助复核变化，不要求永远保持原型数值。规则描述由设计者维护，工具忠实生成卡表，不宣称从任意 IR 自动推导自然语言文本。

跨平台 CLI 入口：

```powershell
dotnet run --project tools/Eota.ContentCli -c Release -- check .
dotnet run --project tools/Eota.ContentCli -c Release -- build . artifacts/ContentBuild
dotnet run --project tools/Eota.ContentCli -c Release -- table . Docs/CardTable.zh-CN.md
dotnet run --project tools/Eota.ContentCli -c Release -- emoji "🐸" artifacts/frog.svg "#427451" "#15281b"
```

日常构建不运行 artifacts 中的一次性录入辅助脚本；规则 JSON、本地化和配方本身就是可编辑源文件。

## 游戏内卡牌编辑器（P10）

首页进入“卡牌编辑器”。左侧筛选或选择卡牌，中间编辑基本属性、效果 JSON 或完整 V2 JSON，右侧显示编译后的原型预览。修改约 0.4 秒后刷新，也可以点击“校验预览”。类型不同会显示攻击／最大生命、法术速度／范围或场地耐久等对应属性。基本属性页增加页签下方、表单四周和行间留白。

场地的耐久就是强度，仅有一个输入框；勾选永久场地立即禁用此框，保存时不写入耐久。禁止本路主动攻击、提供蓄能及关键词在“效果 JSON”中编辑，与充能触发效果放在同一对象中。例如场地可使用：

```json
{
  "effects": [],
  "keywords": [],
  "storedCharge": 2,
  "preventsActiveAttacksInLane": true
}
```

`storedCharge` 是提供的蓄能；充能能力的触发、条件与 `charge` 要求仍写在 `effects` 中。法术不提供实体蓄能或场地被动字段。完整 JSON 可查看全部 V2 属性。

“保存卡牌”写入用户目录 `content/draft.eotapack.json`，不覆盖 Content/Source 或 Content/Generated。新建／复制、删除和恢复内置版本均作用于编辑包；被其他卡引用的衍生卡不能直接删除。切换未保存卡牌时有放弃确认，普通菜单和对战切换保留本次运行中的编辑状态。

页首“导出卡牌”保存当前卡及其关联卡牌（包含递归衍生引用）和名称／说明，“导出内容包”保存全部编辑内容。先保存当前卡牌，再选择 `.eotapack.json` 路径。“内容包管理 → 导入并合并卡牌”按 ID 合并单卡包或整包，同 ID 替换，其他卡牌保留；导入后需单独启用。启用时编译并核对包的 RuleContentHash，复制为 `content/active.eotapack.json`，后续保存草稿不改变已启用版本。插图引用现有 `Content/Generated/Art/*.png`，暂不打包外部图片。

自定义内容同样可以用于本机、AI 和开服。联机仅要求双方卡组所有卡牌及其关联衍生卡的规则与服务器一致；不要求整个卡池相同。第二位玩家确认时会重新核对两个席位的完整对局范围。只修改名称／说明不改变规则哈希。规则一致性不等同于自动评估卡牌平衡性；服务器不会接受客户端上传的数值或效果来覆盖房间规则。

构筑限制与疲劳选择见 [当前玩法指南](../GameplayGuide.zh-CN.md)。

# 开发工具与内容制作

本页说明 `tools/` 下每个文件负责什么，内容与卡图如何从 `Content/Source` 编译成游戏读取的产物，以及游戏内卡牌编辑器的用法。工具只在开发仓库中使用，不随游戏发布包分发；输出写入 `Content/Generated`、`Docs/`、`exports/` 或 `artifacts/`（`artifacts/` 已被 `.gitignore` 排除，可随时删除）。

规则、卡牌 JSON 写法与客户端操作分别见 [架构文档](../Docs/Architecture.zh-CN.md)、[JSON 编写指南](../Docs/CardJsonDesignGuide.zh-CN.md) 和 [客户端功能与操作](../Docs/GameplayGuide.zh-CN.md)。

## 运行前提

| 用途 | 需要 |
|---|---|
| 内容编译、卡表、回放 | .NET SDK 8.0.4xx（`global.json` 固定 `8.0.400` + `latestPatch`） |
| 卡图与图标渲染 | Node.js 22+ 与本机 Chrome／Edge（headless，自动启动并退出） |
| 发布打包与验证 | 上述全部，外加 Godot 4.6.2 .NET |

## 常用入口

```powershell
./tools/Build-Content.ps1                                    # 编译内容 + 重生成卡表
./tools/Build-Content.ps1 -WithPng                           # 上述 + 重新渲染全部卡图与图标
./tools/Generate-CardTable.ps1                               # 只重生成 Docs/CardTable.zh-CN.md
./tools/Publish-LocalServer.ps1                              # 发布本机服务端到 Server/win-x64
./tools/Package-WindowsRelease.ps1 -GodotPath <godot.exe>    # 打 Windows 发布包
./tools/Test-WindowsRelease.ps1 -ArchivePath <zip>           # 验证发布包
```

## PowerShell 脚本

| 文件 | 作用 |
|---|---|
| `Build-Content.ps1` | 内容总入口。调用 ContentCli 编译内容、重生成卡表；加 `-WithPng` 时再调用 EmojiArt 导出器重新渲染卡图与图标。`-OutputDirectory` 可改输出目录（默认 `Content/Generated`）。 |
| `Generate-CardTable.ps1` | 只做卡表：调用 `Eota.ContentCli table`，默认输出 `Docs/CardTable.zh-CN.md`。 |
| `Publish-LocalServer.ps1` | 把 `Eota.Server.Host` 以 `--self-contained` 发布到 `Server/<Runtime>`（默认 `win-x64`），并写 `Server/.gdignore` 让 Godot 忽略该目录。游戏内“本机开服”运行的就是这份产物。 |
| `Package-WindowsRelease.ps1` | 打 Windows 发布包。校验版本号格式与 Godot 版本，复制 `Client`／`src`／工程文件到临时工程导入导出，发布 self-contained 服务端，复制 `Content/Generated`、`Docs`、`LICENSE`、引擎许可，写 `release-manifest.json`（含卡牌数量与规则哈希），最后压 ZIP 并生成 `.zip.sha256`。同版本已有输出时会拒绝覆盖。详见 [Windows Release 打包](../Docs/WindowsRelease.zh-CN.md)。 |
| `Test-WindowsRelease.ps1` | 验证已打包的 ZIP：核对 SHA-256、解压到新目录，用随包 `EOTA.exe` 依次跑检查项。`-Checks` 可选 `startup`／`local`／`scenes`／`ai`／`ui`／`decks`／`hosting`。刻意清空 `PATH`、`DOTNET_ROOT` 并指定独立用户数据目录，模拟没有开发 SDK 的机器。 |
| `Test-ManagedHosting.ps1` | 联机开服冒烟：用 `--smoke-p10-host` 起一个 Godot 实例开房并写出邀请文件，再用 `--smoke-p10-join` 起第二个实例加入，两端都要求 `P10_HOSTING_SMOKE_OK`；结束时会结束自己启动的进程。需要传 `-GodotPath`。 |

## 生成卡表

在仓库根目录运行：

```powershell
./tools/Generate-CardTable.ps1
```

结果是 [Docs/CardTable.zh-CN.md](../Docs/CardTable.zh-CN.md)。每次重新读取并编译 `Content/Source/Cards`、中文本地化和卡图配方，按来源／职业／类型／费用／ID 排序。修改数值或描述后再次运行即可；不要直接编辑生成的 Markdown。名称／描述缺失、重复卡牌、非法效果或无效配方会使生成失败。

## 内容文件与构建产物

同时更新内容构建、差异与能力报告：

```powershell
./tools/Build-Content.ps1
./tools/Build-Content.ps1 -WithPng
```

第一条需要 .NET 8，输出可重复构建的规则二进制、可重编译 JSON、表现清单、中文文本、SVG 预览和报告。第二条还使用 Node.js 22+ 及本机 Chrome／Edge 重新生成 PNG 卡图和透明图标。浏览器在后台启动临时配置并自动退出，不使用现有浏览器资料。融合图从 `Content/Source/Art/Fusions` 的缓存读取，批量构建不安装 npm 包，也不访问图片服务。

PNG 是游戏可用的素材；SVG 中的 emoji 文字仅用于浏览器预览，不能假设所有 SVG 游戏导入器支持 emoji 字体。

| 路径 | 用途 |
|---|---|
| `Content/Source/Cards` | 可直接维护的 282 份 V2 规则 JSON |
| `Content/Source/Decks/archetypes.json` | 十五套体系模板，构建后生成 `archetype-decks.json` 供客户端读取 |
| `Content/Source/Localization/zh-CN.json` | 564 个名称／描述条目 |
| `Content/Source/Art/emoji-recipes.json` | 卡名对应的 emoji 与配色，和规则分离 |
| `Content/Source/Art/Fusions` | 251 张已选择的透明融合图及来源说明 |
| `Content/Source/Art/ui-icons.json` | 六个透明图标的配方 |
| `Content/Design/card-baseline.json` | 实验卡的数值与文本设计基线；不参与运行规则 |
| `Content/Generated/rules.bin` | RuleContentPack 的完整 canonical 字节，SHA-256 等于 ruleHash |
| `Content/Generated/cards.sources.json` | 可直接重编译的源文档集合；不冒充二进制解码器 |
| `Content/Generated/presentation.json` | 名称键、描述键、PNG 路径和 SVG 预览路径 |
| `Content/Generated/manifest.json` | 数量、实验状态、规则哈希与含文本／配方的表现哈希 |
| `Content/Generated/archetype-decks.json` | 编译后的体系模板，供客户端读取 |
| `Content/Generated/BaselineDiff.zh-CN.md` | ID／数量及数值／文本的基线差异 |
| `Content/Generated/Coverage.zh-CN.md` | 编译后 IR／Intent 使用关系和未使用能力 |

实验卡可以继续平衡调整。差异报告帮助复核变化，不要求永远保持原型数值。规则描述由设计者维护，工具忠实生成卡表，不宣称从任意 IR 自动推导自然语言文本。

日常构建不运行 `artifacts/` 中的一次性录入辅助脚本；规则 JSON、本地化和配方本身就是可编辑源文件。

## 内容工具：`Eota.ContentCli`

把 `Content/Source` 编译成运行内容，并生成卡表与报告。

| 文件 | 作用 |
|---|---|
| `Program.cs` | 命令入口：`build`／`check`／`table`／`emoji`，参数错误返回 1，内容或 IO 错误返回 2。 |
| `ContentCatalog.cs` | 核心。读取卡牌 JSON、中文本地化、卡图配方、图标配方与职业配色，逐张校验（缺文本、缺配方、配色与职业不符、融合图缺失或未通过安全路径检查都会报错），并生成卡表、基线差异与能力覆盖报告。 |
| `EmojiArt.cs` | 生成 512×512 的 SVG：职业渐变卡图或透明图标，可内嵌融合 PNG。浏览器渲染以外的快速预览用。 |
| `Eota.ContentCli.csproj`、`packages.lock.json` | 工程定义与锁定的还原文件。 |

跨平台 CLI 入口：

```powershell
dotnet run --project tools/Eota.ContentCli -c Release -- check .
dotnet run --project tools/Eota.ContentCli -c Release -- build . Content/Generated
dotnet run --project tools/Eota.ContentCli -c Release -- table . Docs/CardTable.zh-CN.md
dotnet run --project tools/Eota.ContentCli -c Release -- emoji "🐸" artifacts/frog.svg "#427451" "#15281b"
```

`build`、`table` 的最后一个参数都是输出路径，省略时分别落到 `Content/Generated` 和 `Docs/CardTable.zh-CN.md`。

## 回放工具：`Eota.ReplayCli`

用正式内核重放命令，逐帧比较状态、回执、事件与 RNG，是确定性回归的主要手段。
| 文件 | 作用 |
|---|---|
| `Program.cs` | 入口与用法；实现 `record`／`verify`（开局与牌序）和 `rng-vector`（打印 RNG 测试向量），另有 `PrintUsage`。 |
| `Program.MatchReplay.cs` | `record-match`／`verify-match`：完整对局的记录与逐帧校验，失败时指出首个不一致的帧。 |
| `Program.Checkpoints.cs` | `record-checkpoints`／`verify-checkpoints`：私有检查点的记录与恢复校验，验证从检查点续跑与从头执行结果一致。 |
| `Eota.ReplayCli.csproj`、`packages.lock.json` | 工程定义与锁定的还原文件。 |

```powershell
dotnet run --project tools/Eota.ReplayCli -c Release -- verify-match Content/Source/Cards tests/Fixtures/P8Content/protocol-v0.json tests/Fixtures/P8Content/match.replay.json
dotnet run --project tools/Eota.ReplayCli -c Release -- rng-vector 179 8
```

`record-match` 会重写夹具文件；`verify-match` 只读比较。退出码：`0` 一致，`1` 参数错误，`2` 文件或 JSON 错误，`3` 内容或协议不合法，`4` 命令序列无法回放，`5` 逐帧结果不一致（会指出首个出错的帧）。各夹具的用法见 `tests/Fixtures/*/README.zh-CN.md`。

## 夹具录制：`Eota.FixtureRecorder`

一次性辅助工具，用来重建 `artifacts/P9GodotReplay`——`--smoke-remote` 读取的**客户端格式**回放。该夹具固定了具体的手牌实例 ID，所以卡牌内容一改就会失效；它走桌面会话驱动一局合法对局再保存，因此协议与种子会与开服端保持一致。

```powershell
dotnet run --project tools/Eota.FixtureRecorder -c Release -- . artifacts/P9GodotReplay 4
```

参数依次是仓库根、输出目录、要推进的回合数。它会同时写出 `replay.json`（客户端格式）与 `deck-one.json`、`deck-two.json`（必须与录制时使用的牌组一致，否则开服端重建的实例 ID 会对不上）。`protocol-v0.json` 由录制过程沿用，不再改写。

## 卡图工具：`EmojiArt`

浏览器与命令行共用的卡图制作工具。完整操作说明见 [EmojiArt/README.zh-CN.md](EmojiArt/README.zh-CN.md)。

| 文件 | 作用 |
|---|---|
| `index.html` | 工具页面：单个／双 emoji、职业配色、尺寸与样式选择、预览与下载；在 Chrome／Edge 中直接打开即可，无需服务器。 |
| `editor.js` | 页面交互：emoji 输入与校验、可合并候选列表、合并按钮、预览刷新、按尺寸下载 PNG，以及导入 `emoji-recipes.json`／`ui-icons.json` 后填充表单。 |
| `emoji-art.js` | 唯一渲染函数 `renderEmoji`。绘制职业渐变卡图或透明图标，融合图按 `icon`／`card` 取不同边距；浏览器与批量导出共用，保证两条路径像素一致。 |
| `emoji-fusion.js` | 融合查表。按兼容表检查两个 emoji 是否有配方并给出图片地址，支持变体选择符与双向顺序。 |
| `fusion-storage.js` | 浏览器持久缓存（IndexedDB）与可移植的 JSON 交换格式，负责缓存导出／导入的校验。 |
| `cache-controls.js` | “本机融合缓存与离线分享”区域：导出本机缓存、导入他人缓存、显示缓存统计。 |
| `profession-palettes.js` | 由 `sync-profession-colors.mjs` 生成的职业配色，供页面读取；不要手改。 |
| `export.mjs` | 批量渲染。用无头 Chrome／Edge 读配方渲染 PNG，融合图直接读本地 `Fusions/*.png`，因此断网也能重建。 |
| `sync-profession-colors.mjs` | 按卡牌真实职业回写配方的 `profession`、`top`、`bottom`，并重新生成 `profession-palettes.js`。 |
| `build-fusion-cache.mjs` | 把 `Fusions/*.png` 与 `shared-cache/*.json` 合并成 `fusion-cache.js`，让下载者离线使用；不访问网络。 |
| `verify.mjs` | 浏览器验收：实际用 Chrome／Edge 跑离线缓存、新组合联网、PNG 下载、配置导入、不支持的组合、网络失败与持久缓存重载，日志与截图写入 `artifacts/emoji-art-check-*`。 |
| `fusion-cache.js` | 生成物：合并后的离线素材包（数 MB），随仓库提交，勿手改。 |
| `shared-cache/` | 手工整理、随仓库分发的额外融合原图 JSON；放入后需运行 `build-fusion-cache.mjs`。见其 `README.zh-CN.md`。 |
| `vendor/` | Emoji Kitchen 兼容表快照（`emoji-compatibility.js`，来自 MIT 许可的 emoji-mixer 1.3.1）与许可文件 `emoji-mixer-LICENSE.txt`；只查表，不批量下载图片。见其 `README.zh-CN.md`。 |
| `README.zh-CN.md` | 工具的完整使用说明：网页操作、缓存位置、素材入仓流程与验收命令。 |

```powershell
node tools/EmojiArt/export.mjs Content/Source/Art/emoji-recipes.json Content/Generated/Art
node tools/EmojiArt/export.mjs Content/Source/Art/ui-icons.json Content/Generated/Icons
node tools/EmojiArt/build-fusion-cache.mjs
node tools/EmojiArt/verify.mjs
```

### 职业配色与卡图约定

卡图渐变背景统一由 `Content/Source/Art/profession-palettes.json` 定义：守卫蓝、奥术师紫、工匠棕、猎人绿、灵魂使青绿、中立灰棕。职业由卡牌的 `profession` 字段决定，同职业随从、法术、场地与衍生卡保持一致，网页与批量导出都严格跟随；内容编译会拒绝职业或配色不一致的配方。更改色板或新增卡牌后运行 `node tools/EmojiArt/sync-profession-colors.mjs` 同步配置及网页色板，再通过 `Build-Content.ps1 -WithPng` 重建。

在“图案来源”中选择“单个 emoji”或“双 emoji 合并”。合并模式分别输入两个 emoji，也可从“可合并的第二个 emoji”列表选取，点击“合并 emoji”预览；不支持或无法获取的组合会显示原因并禁用下载，避免误导出之前的预览。页面顶部“从已有卡图配置开始”可导入 `emoji-recipes.json` 或 `ui-icons.json`，选条目后填入 emoji、融合来源、配色和文件名；它不导入 PNG，也不导入游戏的卡牌内容包。文件名经过清理；输入作为文本绘制，不作为 HTML 执行。

当前 282 张卡中有 251 张使用 Emoji Kitchen 双 emoji 融合图，另外 31 张使用单个 Unicode emoji；不再把两个独立 emoji 并排缩小绘制。复杂卡名和复合效果优先寻找融合图，例如困倦搬运工使用打哈欠的纸箱组合；白板随从、语义简单或没有准确组合的卡保留单个 emoji，例如原野巨犀使用 `🦏`。五个职业的 50 张基础卡与 2 张衍生卡全部使用融合图；保留单 emoji 的主要是中立与猎人的白板随从、若干衍生卡和语义极简单的卡。融合只替换中央图案；职业渐变背景、圆形光环、卡面名称、说明和数值布局保持原样。

融合兼容关系通过 MIT 许可的 [emoji-mixer](https://github.com/MattFor/emoji-mixer) 核对，实际透明图由 Google Emoji Kitchen 提供并缓存到源码目录。每条配方保留原 emoji、缓存路径和原始 `gstatic.com` URL，便于审计或恢复。具体来源说明见 [融合素材目录](../Content/Source/Art/Fusions/README.zh-CN.md)。这些融合图当前用于内部测试素材；公开发行前应再次确认 Emoji Kitchen 图像的发行许可。

普通 PNG 外观由当前系统 emoji 字体及浏览器决定，跨系统重新绘制未必逐像素相同；融合 PNG 已缓存，因此保持一致。规则包与卡表的可重复构建不依赖浏览器或字体。开发环境中的客户端会直接读取 `Content/Generated` 下的当前 PNG，避免 Godot 继续显示旧 `.ctex` 导入缓存；发布后的资源包仍由 `ResourceLoader` 读取构建时导入的贴图。卡图源素材为 512×512 方形，客户端按原比例显示；整张卡牌的插图区、名称、描述及数值布局由场景控制。

### 透明图标

`Content/Source/Art/ui-icons.json` 定义界面图标，按样式 `icon` 导出到 `Content/Generated/Icons`：

| 文件 | 含义 | Emoji |
|---|---|---|
| `Icons/fast-spell.png` | 快速法术 | ⚡ |
| `Icons/slow-spell.png` | 慢速法术 | ⏳ |
| `Icons/frozen.png` | 路冻结：该路所有随从无法攻击 | ❄️ |
| `Icons/locked.png` | 路锁闭：该路无法进入或离开 | 🔒 |
| `Icons/ether.png` | 以太 | 🌊 |
| `Icons/slow-minion.png` | 迟缓随从 | 🐌 |

冻结／锁闭作用于整条路、影响双方，图标显示在路的状态区域。语义见 [ADR-027](../Docs/ADR/027-lane-statuses.md)，接入及验证见 [P9 验收](../Docs/Review/P9-Godot-Review.zh-CN.md)。

## 游戏内卡牌编辑器（P10）

首页进入“卡牌编辑器”。左侧筛选或选择卡牌，中间编辑基本属性、效果 JSON 或完整 V2 JSON，右侧显示编译后的原型预览。修改约 0.4 秒后刷新，也可以点击“校验预览”。类型不同会显示攻击／最大生命、法术速度／范围或场地耐久等对应属性。

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

“保存卡牌”写入用户目录 `content/draft.eotapack.json`，不覆盖 `Content/Source` 或 `Content/Generated`。新建／复制、删除和恢复内置版本均作用于编辑包；被其他卡引用的衍生卡不能直接删除。切换未保存卡牌时有放弃确认，普通菜单和对战切换保留本次运行中的编辑状态。

页首“导出卡牌”保存当前卡及其关联卡牌（包含递归衍生引用）和名称／说明，“导出内容包”保存全部编辑内容。先保存当前卡牌，再选择 `.eotapack.json` 路径。“内容包管理 → 导入并合并卡牌”按 ID 合并单卡包或整包，同 ID 替换，其他卡牌保留；导入后需单独启用。启用时编译并核对包的 RuleContentHash，复制为 `content/active.eotapack.json`，后续保存草稿不改变已启用版本。插图引用现有 `Content/Generated/Art/*.png`，暂不打包外部图片。

自定义内容同样可以用于本机、AI 和开服。联机仅要求双方卡组所有卡牌及其关联衍生卡的规则与服务器一致；不要求整个卡池相同。第二位玩家确认时会重新核对两个席位的完整对局范围。只修改名称／说明不改变规则哈希。规则一致性不等同于自动评估卡牌平衡性；服务器不会接受客户端上传的数值或效果来覆盖房间规则。构筑限制与疲劳选择见 [客户端功能与操作](../Docs/GameplayGuide.zh-CN.md)。

## 客户端冒烟测试

客户端自带 12 个冒烟入口，用 .NET 版 Godot 直接运行，全部通过就表示界面与传输链路正常。多数可以 headless 运行，不弹窗口：

```powershell
& 'D:\Dev\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' --path . --headless -- --smoke
& 'D:\Dev\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' --path . --resolution 1280x800 -- --smoke-ui-review
```

| 入口 | 覆盖 |
|---|---|
| `--smoke` | 本机对局、回放校验，并重写 `artifacts/P9GodotReplay` |
| `--smoke-ui-review` | 图鉴排序、关键词悬停、牌组工坊、协议页、两种窗口尺寸 |
| `--smoke-interactions` | 换牌、规划、撤回、法术标记、已提交锁定、图鉴与衍生卡 |
| `--smoke-presentation` | 抽牌飞行、碰撞、打脸、数值动画、暂停／倍速／跳过 |
| `--smoke-scene-lifetime` | 反复创建卡牌场景与 GC |
| `--smoke-card-status` | 迟缓标记与标签展示 |
| `--smoke-ai` | 三档 AI 完整对局与重开 |
| `--smoke-p10-editor` | 卡牌编辑器：草稿隔离、统计预览、效果与场地校验 |
| `--smoke-p10-decks` | 牌组删除、构筑限制、协议保存与按费用排序 |
| `--smoke-p10-host` / `--smoke-p10-join` | 本机开服、加入、重连、补帧与诊断导出 |
| `--smoke-remote` | 远程 WebSocket 投影与本地回放一致性 |

两个需要额外前置条件：

- **开服冒烟**需要先重新发布随包服务端，否则 `Server/win-x64` 里的旧构建会拒绝当前内容（`room-content-invalid`）：

  ```powershell
  dotnet publish src/Eota.Server.Host/Eota.Server.Host.csproj -c Release -r win-x64 --self-contained true -o Server/win-x64 --verbosity quiet
  ```

  然后一个进程跑 `--smoke-p10-host --invitation-file <路径>`，等它写出邀请文件后再用另一个进程跑 `--smoke-p10-join --invitation-file <路径>`。

- **`--smoke-remote`** 需要先起一个开发 Host，并让 `artifacts/P9GodotReplay` 与当前内容一致（否则用上面的夹具录制工具重建）：

  ```powershell
  dotnet build src/Eota.Server.Host/Eota.Server.Host.csproj -c Release
  src/Eota.Server.Host/bin/Release/net8.0/Eota.Server.Host.exe --fixture artifacts/P9GodotReplay --cards Content/Source/Cards --match-id p9-smoke --urls http://127.0.0.1:5086
  ```

  Host 就绪（`GET /health` 返回 200）后再运行客户端冒烟。`--fixture` 模式必须显式给 `--cards`，否则会去找夹具目录下不存在的 `Cards` 子目录。

## 其他说明

- 各 CLI 目录下的 `*.cs.uid` 由 Godot 4 自动生成，用于脚本资源的稳定标识，随仓库提交，不要手工编辑。
- `bin/` 与 `obj/` 是构建输出，已被 `.gitignore` 排除；调试某个 CLI 时最直接的入口是 `dotnet run --project tools/<项目>`。
- 新增卡牌后至少依次执行：`Build-Content.ps1`（校验并重生成产物）→ 需要图片时 `-WithPng` → `dotnet test Eota.sln -c Release` → 必要时用 ReplayCli 重录受影响的夹具。

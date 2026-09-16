# P9.6 交互与动画反馈验收

2026-09-11。接续 P9.5，落实两轮实际游玩反馈。以下为开发与界面检查结果，视觉体验继续接受玩家反馈；固定 `.tscn` 是当前布局源文件。

## 已实现的交互

| 反馈                   | 当前行为                                                                                                                       |
|------------------------|--------------------------------------------------------------------------------------------------------------------------------|
| 本机双人与 AI 混在一起 | 独立的“本机双人”与“人机对战”页面；只有人机页提供三档难度                                                                       |
| 切换席位暴露对方手牌   | 点击切换先移除整个战场、手牌和动画，显示交接页；下一位玩家确认后才切换观察者，确认按钮与原切换按钮不重叠                       |
| 拖牌后缺少明确反馈     | 待入场随从、场地在对应槽位显示绿框；快速／慢速法术使用已有图标，分别显示在路线中间及全局区域；撤回立即恢复手牌和原槽位         |
| 图鉴的悬停与选择       | 悬停显示，离开清空；点击卡牌在外部加框并锁定详情，悬停其他卡不切换，再次点击取消；筛选、离开图鉴均清理旧选择                   |
| 对齐和开发说明         | 首页协议摘要居中；协议页签各状态使用一致的边距，悬停不移动文字；移除实验卡数量、内部结算限制等客户端说明                       |
| 抽牌和随从碰撞         | 抽牌沿弧线飞入手牌；实际攻击事件驱动随从前冲、碰撞闪光、回位；浮字同样使用表现时钟                                             |
| 场地缺少耐久           | 原型卡面右下角显示初始耐久，场上卡面显示剩余耐久；无限耐久场地隐藏徽章                                                         |
| 战斗预览不消失         | 鼠标离开手牌、场上卡、待入场卡或法术标记时清空右侧预览；重绘移除原控件时也清理引用                                             |
| 图鉴混入衍生卡         | 主列表仅显示 129 张基础卡；选定来源卡后，相关衍生卡显示在整个窗口右下角，点击切换详情，支持返回原卡。内容包仍包含全部 132 张卡 |
| 构筑缺少卡组选择步骤   | 先显示已有卡组与草稿列表；新建时选择职业并阅读介绍，再进入编辑页；已有卡组直接加载编辑。卡池自动提供本职业及中立基础卡         |

卡组草稿按卡组分别保存于本次运行内，同职业可有多个草稿。返回列表或切换页面不丢失编辑，点击保存后写入用户牌组目录并更新对局选择项；名称冲突或非法 40 张牌组会显示明确提示。

## 数据与表现边界

- `DesktopCatalog` 从编译后的场地定义投影初始耐久；衍生卡关联来自召唤、加入手牌、变形等效果引用，递归处理组合与条件效果，并去重、防循环。没有硬编码卡牌 ID 或解析中文描述。
- `GameApp.Planning` 只读取当前观察者的私有规划，预览节点与真实 EntityId 注册表分离。替换时暂时隐藏原实体，取消后恢复；另一席位、观战和回放不会看到他人的规划。
- `BattleAnimations` 不读取 Kernel 状态。抽牌卡面完全取决于 `PresentationEvent.Card`，没有卡面权限时只生成固定卡背场景；攻击目标取自 `AttackDeclared`。
- 抽牌和碰撞播放时按表现帧排队。暂停停止推进，倍速缩放表现时间，跳过恢复被动画遮住的真实卡面；回放跳转、重建、离开和交接清除轨迹。动画完成不触发规则结算。
- 新增或拆分的界面包括 `AiSetup`、`SeatHandoff`、`CardOutline`、`SpellPlans`、`RelatedCards`、`CardMini`、`DeckWorkshop`、`DeckLibrary`、`DeckTile`、`ProfessionPicker`、`DeckEditor`、`BattleAnimations`、`CardBack`、`ImpactFlash`。具体布局说明见 [场景 README](../../Client/Godot/Scenes/README.zh-CN.md)。

## 验证结果

- `dotnet build Eota.Godot.csproj --no-restore`：0 警告、0 错误。
- `dotnet test Eota.sln -c Release --no-restore`：582 项通过，0 失败（Kernel 137、内容编译 357、集成 80、架构 7、代数 1）。新增目录投影测试覆盖耐久以及嵌套效果中的衍生卡关系。
- `--smoke-interactions`：实际鼠标事件验证图鉴悬停／锁定／取消、衍生卡切换、战斗预览离开清空和页签悬停；正式命令验证随从／场地及四类法术规划、撤回、替换恢复、席位交接和私有信息隔离。检查有限／无限场地原型与实例徽章，并验证权威回放一致。
- `--smoke-ui-review`：四职业创建及介绍、自动筛选卡池、草稿恢复、已有卡组编辑和保存后返回列表；保存测试使用唯一临时名称并在结束后清理文件。检查换牌、独立玩家以太、6／12 路与 10 张手牌滚动。
- `--smoke-presentation`：从正式三回合对局生成回放，检查可见抽牌卡面、隐藏卡背、前冲位移与原卡恢复、暂停冻结、4 倍速、跳过及最终回放状态。
- 上述三组界面检查均在真实 Godot 渲染下运行，覆盖 1600×1000 和 1280×800。1280 最终日志为 `artifacts/p96-final-{interactions,ui-review,presentation}.log`，保存流程另见 `artifacts/p96-deck-save.log`。
- 原本机 smoke 仍通过，四回合最终哈希保持 `ff4220becdc29056b447cd6246fe62bad1ab344169fab5b137e55924715f8aea`；三档 AI 的完成、重开、撤回和回放检查沿用 `--smoke-ai`，见 `artifacts/p96-ai-smoke.log`。

当前内容包中的场地均为有限耐久；无限耐久徽章分支使用仅供表现的视图夹具检查，不向正式内容包添加测试卡。

## 界面截图

- [本机交接页](../../artifacts/p96-1280x800-handoff.png)
- [待入场绿框与路线／全局法术](../../artifacts/p96-1280x800-planned.png)
- [来源卡与衍生卡详情](../../artifacts/p96-1280x800-derived-card.png)
- [卡组列表](../../artifacts/p96-1280x800-deck-library.png)、[职业介绍](../../artifacts/p96-1280x800-profession-picker.png)、[编辑页](../../artifacts/p9-review2-1600x1000-deck-builder.png)
- [抽牌飞行](../../artifacts/p96-1280x800-draw-flight.png)、[碰撞中间帧](../../artifacts/p96-1280x800-collision.png)

## 后续微调：状态图标与职业原文

2026-09-11 根据后续反馈完成：

- 冻结雪花和锁闭图标从 16×16 改为 32×32，并通过 AtlasTexture 裁去源贴图透明留白。固定 `LaneStatuses.tscn` 显示在路线标题旁，两个状态可以同时显示并保留悬停说明，不挤占法术和以太区域。
- 路线标题由 12 提升至 16 像素，场上随从／场地卡名由 13 提升至 15 像素，英雄席位、手牌／牌库数量和提交状态的小字适度放大。对手槽位随标题区调整，卡牌槽位尺寸保持一致。
- 四职业介绍由 [GameApp.DeckWorkshop.cs](../../Client/Godot/GameApp.DeckWorkshop.cs) 中的 ProfessionDescription 定义；扩大说明区域以完整显示各职业背景与战术说明。
- 本次构建 0 警告、0 错误。复用实际 Godot 界面检查，1600×1000 和 1280×800 均通过，覆盖四职业完整介绍、冻结与锁闭同时出现、以太两侧、6／12 路布局及手牌滚动；原交互 smoke 通过。文案另经逐字比较确认一致。日志为 `artifacts/p96-readability-{1600x1000,1280x800,interactions}.log`。
- 截图：[状态图标与文字](../../artifacts/p9-review2-1280x800-ether-sides.png)、[工匠完整介绍](../../artifacts/p96-1280x800-profession-Artisan.png)、[守卫完整介绍](../../artifacts/p96-1280x800-profession-Guardian.png)。

## 后续微调：垂直攻击、数值颜色与逐张法术

2026-09-11 根据后续战斗反馈完成：

- 随从攻击保持所在路线的横坐标不变，双方分别向上／向下冲撞。打脸也使用本路对方槽位作为纵向参考，不再向左侧英雄面板运动；暂停、倍速、跳过和恢复原卡面仍有效。
- 生命数字先判断是否受伤：低于当前最大生命为红色；满血且高于原型最大生命为绿色，其余使用默认色。例如原始 4 血，强化后 5/6 血为红色，6/6 为绿色。攻击低于原始攻击为红色、高于为绿色。手牌强化、场上数值及节点恢复均使用同一原型基准。
- 三张慢速法术显示三个独立沙漏；快速法术同样逐张显示闪电，不再合并成图标加数量。路线和全局标记均按各自 PlanCommandId 绑定，悬停预览该张，提交前点击撤回该张。更多法术可横向滚动，提交后禁用撤回。
- 实际 Godot 检查在 1600×1000 和 1280×800 均通过。`--smoke-presentation` 使用正式命令产生攻击随从及英雄的回放，验证两个观察席位的横坐标不变和纵向方向，并检查伤血强化优先红色、满血强化绿色、攻击红绿和颜色复原；视图夹具只用于数值颜色展示。`--smoke-interactions` 使用正式命令分别规划三张路线／三张全局法术，实际点击中间图标，验证只撤回该张、手牌与费用返还；还验证第四张图标滚动可达且未被滚动条裁切、提交后不能撤回及权威回放一致。
- 构建 0 警告、0 错误。日志：`artifacts/p96-feedback-{presentation,interactions}.log` 与 `artifacts/p96-feedback-1280-{presentation,interactions}.log`。
- 截图：[三个沙漏](../../artifacts/p96-1280x800-three-spells.png)、[法术滚动](../../artifacts/p96-1280x800-spell-overflow.png)、[数值红绿状态](../../artifacts/p96-1280x800-stat-colors.png)、[玩家一视角打脸](../../artifacts/p96-1280x800-hero-attack-PlayerOne.png)、[玩家二视角打脸](../../artifacts/p96-1280x800-hero-attack-PlayerTwo.png)。

## 复现

```powershell
dotnet test Eota.sln -c Release --no-restore
dotnet build Eota.Godot.csproj --no-restore
& $godot --path . --resolution 1600x1000 -- --smoke-interactions
& $godot --path . --resolution 1600x1000 -- --smoke-ui-review
& $godot --path . --resolution 1600x1000 -- --smoke-presentation
& $godot --headless --path . -- --smoke
& $godot --headless --path . -- --smoke-ai
```

`$godot` 指向 Godot 4.6.2 Mono 可执行文件。场景可直接在编辑器调整；不要重新运行 artifacts 中历史的一次性场景生成脚本覆盖后续布局。

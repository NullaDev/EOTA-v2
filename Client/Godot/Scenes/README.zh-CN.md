# Godot 场景布局

这些 `.tscn` 是可直接在 Godot 编辑的界面源文件。运行时通过 PackedScene 实例化，脚本绑定数据与事件。页面、卡牌边框、属性徽章和战场分区均在当前场景中定义。

| 场景 | 用途 |
|---|---|
| MenuScreen | 侧边导航与固定页面实例，在编辑器内即可查看页面结构 |
| LocalSetup / AiSetup / RemoteSetup / HostSetup | 本机双人、三档人机、双方牌组核对、受管理进程开服／恢复／邀请 |
| CardEditor | V2 卡牌列表、按类型编辑属性、效果／完整 JSON、原型预览、内容包管理 |
| RoomProtocolReview / RoomProtocolRow | 开局确认前查看房间的完整玩法协议，使用只读参数行 |
| SeatHandoff | 移除手牌后的席位交接页，下一位玩家确认后才切换观察者 |
| CardLibrary / RelatedCards / CardMini | 基础卡图鉴、右下角衍生卡导航和可点击缩略卡 |
| DeckWorkshop / DeckLibrary / DeckTile | 卡组工坊入口、已有卡组及草稿列表 |
| ProfessionPicker / DeckEditor / DeckRow | 职业介绍与创建、职业卡池编辑、已选卡牌行 |
| ProtocolPage / ProtocolGroup / ProtocolField | 协议分页、可滚动分组及参数行 |
| ReplayPage | 回放选择和载入 |
| BattleScreen / HeroPanel / Lane | 战场框架、双方英雄和固定随从／场地槽位 |
| CardTile / CardDetail | 原型或手牌卡面、图鉴大图详情 |
| MinionTile / FieldTile | 战场实例表现：随从显示当前生命，有限场地右下角显示剩余耐久 |
| CardOutline / SpellPlans / SpellPlanIcon | 待入场与图鉴锁定绿框、路线和全局每张法术独立的可撤回图标 |
| LaneStatuses | 路线标题旁的冻结／锁闭标记，32×32 图标裁去透明留白，固定节点按状态显隐 |
| BattleAnimations / CardBack / ImpactFlash | 按表现时钟播放的抽牌、碰撞、浮字与隐藏卡背 |
| CardInspection / PlanChip | 战场原型详情、可取消的规划条目 |
| EtherPips | 独立玩家的以太条：零级隐藏，每级一幅波浪图标 |

窗口按 1600×1000 画布保持比例缩放。卡牌尺寸、边框与文字区域由场景定义，颜色和控件状态由 `../GameTheme.tres` 统一维护。路线、卡牌和参数行按数据数量重复实例化；额外路线与手牌横向滚动，长描述有独立滚动区域。

以太条使用生成的 `ether.png`，渲染时裁去透明留边；图片文件未修改。大于固定条容量的自定义等级可以横向滚动逐个查看，不把多级合并为单个数字。归属绑定 `(LaneId, PlayerId)`，切换席位只改变上下位置。

CardTile 的展示模式绑定 CardPresentation 原型投影，不创建假的实例。有限场地原型携带初始耐久，场上实例绑定 FieldEnergy；无限场地不显示徽章。图鉴主列表为 222 张基础卡，另外 8 张衍生卡通过编译后的效果引用关联到来源卡，不直接混入主列表。悬停预览，点击外框锁定；离开、取消锁定或筛选时清理详情。

GameApp.DeckWorkshop 负责卡组列表、职业选择和编辑流程，DesktopDeckDraft 负责职业卡池和每个卡组的草稿；允许同职业多个草稿，返回列表或切换页面保留本次运行中的编辑。只有点击保存才写入用户牌组目录，构筑合法性继续由正式 MatchFactory 校验。旧 DeckBuilder.tscn 为历史场景，当前 MenuScreen 引用 DeckWorkshop.tscn。

DeckTile 的 Delete 按钮用于删除已保存自建卡组及新建草稿，确认后同步文件和列表；默认模板保留。DesktopDeckStore 使用独立于名称的文件标识，重命名不会留下旧文件。构筑数量、卡池、填入默认牌组、保存校验均读取当前协议；改变协议保留原草稿，不合法项标红。ProtocolPage 的“构筑与疲劳”提供张数、副本上下限、三种职业规则和三种疲劳策略。HostSetup 只选择自己的参考牌组，OpponentDeck 固定显示由对方加入后选择。

四职业介绍由 [GameApp.DeckWorkshop.cs](../GameApp.DeckWorkshop.cs) 中的 ProfessionDescription 定义，分别说明守卫、奥术师、猎人与工匠的背景及战术。ProfessionPicker 的说明区域为 222 像素高，使用 22 像素文字，容纳完整介绍。

规划预览单独注册，真实战场实体继续按 EntityId 绑定。替换预览临时遮住原实体，取消后恢复；其他玩家和观战者不能看到这些私有规划。SpellPlans 为每张法术实例化 SpellPlanIcon，三张慢速法术显示三个沙漏。悬停预览对应卡牌，提交前点击按 PlanCommandId 撤回该张，图标不向路线透传点击；更多法术可横向滚动，保留逐张操作。

BattleAnimations 通过表现播放器推进时间，随从攻击及打脸均固定 X 轴，只沿当前路线的上下方向冲撞。打脸使用本路对方随从槽位的纵坐标，不朝侧边英雄信息面板运动。暂停冻结轨迹、倍速缩放时间、跳过恢复真实卡面；回放跳转、战场重建和交接会清除动画。

CardTile 数值颜色通过 GameTheme 的 stat_reduced／stat_increased 维护：当前生命低于当前最大生命时优先显示红字；满血且超过 CardPresentation.Health 时显示绿字。攻击与 CardPresentation.Attack 比较，低于原值红、高于原值绿。治疗恢复、强化消失或节点更新回原值时移除字体颜色覆盖；图鉴原型保持原值颜色。

AiSetup 固定场景提供 AI 牌组与简单／普通／困难选择；玩家固定为玩家一，玩家二由 AI 自动换牌、规划、提交。LocalSetup 只提供两位本机玩家设置。BattleScreen 固定包含 AI 状态和“重新开始”按钮；返回大厅／重开会先停止旧 AI。策略在 `Eota.Client.AI` 后台执行，场景只绑定 Desktop 配置与状态；回放不重新运行策略。最新验收见 [P9.6](../../../Docs/Review/P96-Interaction-Review.zh-CN.md)，策略验收见 [P9.5](../../../Docs/Review/P95-AI-Review.zh-CN.md)。

固定场景为当前布局源文件。artifacts 中早期的场景生成脚本和本轮一次性 author_p96 脚本只保留操作记录，不应重新运行来覆盖手工维护后的场景。

本机开服注入 `LocalServerLauncher`，执行随游戏分发的 `Server/win-x64/Eota.Server.Host.exe`，只管理本次持有的进程。玩家先核对规则与协议，再确认自己的牌组；双方都通过服务器校验才进入对局。协议、牌组与规则在房间中锁定。详情见 [P10 验收](../../../Docs/Review/P10-Hosting-Editor-Review.zh-CN.md)。

CardEditor 的编辑包保存到 `user://content/draft.eotapack.json`；启用复制为独立 active 包。菜单和对战重建前保留未保存的编辑状态，显式保存才落盘。编辑器从当前 V2 定义编译预览，不实例化对战状态。artifacts/author_p10_scenes.py 为一次性初稿记录，后续布局以手工修改后的 tscn 为准，不要重新运行覆盖。

基本属性表单使用固定的 `Pages/Basic/Margin/Fields`，页签下方和四边留白、行间距由场景维护。场地强度／耐久合一，Permanent 切换立即禁用 Energy；禁止攻击、蓄能和关键词移入效果 JSON 对象。页首固定 ExportCard／ExportPack 按钮，单卡包含规则引用闭包；导入按 ID 合并。联机准备提交逐卡规则清单，只校验双方卡组及其相关卡牌，房间协议仍绑定服务器冻结内容。

HostSetup 的 Timeout 固定控件选择不限时／30／60／120／180 秒；RemoteSetup 与 RoomProtocolReview 显示并核对时限。BattleScreen 的 MatchClock 节点绑定服务器倒计时，独立于动画暂停／倍速；不限时、本机或回放隐藏。远程会话重连期间停止旧表现，收到同席位新快照后重建战场，保留动画速度与暂停选择。

BattleScreen 的联机工具栏增加 SaveDiagnostics，导出元数据到 `user://diagnostics/`。重连返回的补发批次由 Client.Core 整批校验；场景先取出基准视图及帧队列再重建，继续复用抽牌／碰撞动画。窗口失效时直接显示当前快照，不产生模拟战斗结果。

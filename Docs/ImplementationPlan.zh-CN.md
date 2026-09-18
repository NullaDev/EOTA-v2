# Embers of the Arcane VNext 重构实施计划

> 状态：执行中；P0–P9.6 已完成开发验收。P10 已实现卡组删除、加入者自选卡组、可配置构筑与疲劳、卡牌编辑器、导出及按双方卡组范围核对定义。当前仅维护内部测试版格式，文档、内容和回放夹具与实现同步更新；不保留旧格式兼容或迁移入口。权威历史压缩、计数耗尽终止语义和发布环境矩阵仍待后续验收。  
> 玩家操作：[当前玩法与房间规则](GameplayGuide.zh-CN.md)。  
> Mod 开发：[JSON 编写指南](CardJsonDesignGuide.zh-CN.md) 直接说明当前规则，在各功能旁提供 30 处 JSON 示例，并配有 [9 张完整教学卡](Content/Examples/README.zh-CN.md)。第 8 节说明双方无先后手、权限相同，引擎通过统一优先级等规则实现同帧操作加法的可交换幺半群；英雄 kill 及回手容量失败后的死亡触发已接入结算。  
> 卡池：已实装，五职业各 50 张基础卡，每职业 2 张衍生卡，中立 22 张，共 282 张。十五套 40 张模板已同步。
> 2026-09-18 核验：973 项测试通过（Kernel 154、Compiler 592、Integration 218、Architecture 7、Algebra 2），含全卡池 313 个独立效果场景和 108 场跨职业体系对局。构筑模板在 1600×1000 与 1280×800 两种窗口尺寸验收通过。平衡尚未定稿，AI 采样中的强弱差异和人工复核重点记录在各阶段的验收文档中，卡池数值以 [卡表](CardTable.zh-CN.md) 为准。  
> P10 当前记录：[开服、牌组准入与卡牌编辑器](Review/P10-Hosting-Editor-Review.zh-CN.md)。  
> 当前记录：[P9.6 交互与动画](Review/P96-Interaction-Review.zh-CN.md)；AI 验收：[P9.5](Review/P95-AI-Review.zh-CN.md)；历史页面反馈：[P9 第二轮反馈](Review/P9-Scene-Review-2.zh-CN.md)；前次交付：[P9](Review/P9-Godot-Review.zh-CN.md)；内容验收：[P8](Review/P8-Content-Review.zh-CN.md)；生成卡表：[CardTable](CardTable.zh-CN.md)；工具用法：[开发工具与内容制作](../tools/README.zh-CN.md)。规则提案状态见各 ADR，尚未公开冻结，卡牌平衡性尚未验证。  
> 架构基线：[Architecture.zh-CN.md](Architecture.zh-CN.md)。  
> 规则基线：根目录 `Docs/ADR/` 中的 Accepted 决策及供 V0 开发使用的 Proposed 提案；各自状态保持明确。  
> 当前内容清单：[282 张卡表](CardTable.zh-CN.md)；源文件为 `Content/Source/Cards`，十五套模板为 `Content/Source/Decks/archetypes.json`。

## 1. 前提与目标

本计划采用以下已确认前提：

- 内核独立实现规则，项目依赖由当前 solution 和架构测试约束。
- 卡牌定义使用当前 V2 schema，输入位于 `Content/Source/Cards`。
- 内部测试版没有真实对战记录的兼容负担；协议、状态及内容只维护当前格式，同步更新测试数据，不保留自动升级逻辑。
- 卡池为原型期实验设计，平衡性未经验证；“正式录入”仅指进入内容管线，不代表数值定稿，后续可能扩充。
- 卡图优先按卡名选择 emoji，缺少贴切符号时使用默认图案，通过独立工具生成；快速、慢速、冻结、锁闭、以太图标同样独立生成。规则定义以当前 ADR 和源内容为准。
- 每一步都必须形成可运行、可测试的纵向切片；不接受“先写完所有模型，最后再集成”。

最终交付链路是：

```text
Card/Protocol Source
        │ compile
        ▼
RuleContentPack + CompiledGameProtocol
        │
        ▼
Pure .NET Kernel
        │
        ├─ Replay CLI
        ├─ In-process server adapter
        └─ Headless .NET Server ── WebSocket ── Godot / future Web client
```

## 2. 总体顺序

```text
P0 规则与工程决策冻结
 │
 ▼
P1 Solution 与架构护栏
 │
 ▼
P2 确定性地基 + 最小内容编译 + 回放骨架
 │
 ▼
P3 命令、区域与同时规划
 │
 ▼
P4 统一结算帧 + 无效果完整对局
 │
 ├──────────────► P5 Server/Client 端到端骨架
 │                         │
 ▼                         │
P6 基础效果语言与基础 Intent│
 │                         │
 ▼                         │
P7 复杂冲突、触发链与区域操作
 │                         │
 ├──────────────► P8 按 CardTable 重新录入 132 张卡
 │                         │
 └──────────────► P9 完整 Godot Adapter ◄────────┘
                           │
                           ├──────────► P9.5 本地人机对战
                           │                      │
                           ▼                      │
                 P10 玩家开服、持久化与联机加固    │
                           │                      │
                           └──────────┬───────────┘
                                      ▼
                             P11 发布验收与冻结
```

关键路径是 `P0 → P1 → P2 → P3 → P4 → P6 → P7`。在 P4 之前不开始正式 UI，在 P7 相应能力稳定之前不批量填卡。

## 3. 全程执行规则

每个阶段均遵守：

1. 先写该阶段的可执行验收测试，再实现功能。
2. 一次提交只引入一个确定语义；规则变化同时更新规范和版本。
3. 所有权威状态变化必须经过 Kernel 正式入口。
4. 每新增一种 Intent，同步提交 ConflictKey、Reducer、Receipt、DomainEvent、错误码和排列测试。
5. 每新增一个序列效果节点，同步提交回执输出契约和回放案例。
6. 不以 Godot 场景或 Server 运行成功代替普通 `dotnet test`。
7. 构建、测试和分发只使用本仓库声明的源文件、资源与依赖。
8. 任一阶段结束时，主分支必须可构建、可运行已有 replay corpus。

## 4. P0：规则与工程决策冻结

### 目标

把会影响所有后续代码的数据语义先固定，避免实现中不断修改底座。

### 工作项

- 在 `Docs/ADR/` 维护规则与验收合同，在 `Docs/GameplayGuide.zh-CN.md` 维护玩家可读的规则说明。
- 建立首批 ADR：
  - `ADR-001`：Kernel 纯度与依赖方向。
  - `ADR-002`：稳定 ID 构造和计数器规则。
  - `ADR-003`：权威整数、溢出、钳制与舍入规则。
  - `ADR-004`：Canonical Encoding V1 与 SHA-256 domain separator。
  - `ADR-005`：GlobalRuleRng V1、调用键和测试向量。
  - `ADR-006`：Effect IR 的 `Parallel`/`Sequence` 帧语义。
  - `ADR-007`：生命周期冲突、Tombstone 和离场观察窗口。
  - `ADR-008`：Transport Contract 版本与 audience 投影。
- 把 VNext 规范中仍可产生两种实现的内容列为显式决议，至少包括：
  - 所有命令、帧和局内对象的 ID 派生方式。
  - 开局抽牌是否走普通 Draw Intent，及其回执记录点。
  - 手牌容量冲突窗口的准确开始和结束位置。
  - 规则错误后比赛进入失败、终止还是可恢复状态。
  - `Sequence` 中空目标、部分成功和 Error 是否继续。
  - 检查点对活跃回执和 Tombstone 的保留规则。
- 建立 `ProtocolVersion = 0` 的开发期策略：未发布前允许破坏性更新，但每次更新重建 golden corpus；首个可玩版本再冻结为 V1。

### 完成标准

- 上述决议不存在“按实现方便决定”的空白。
- 能仅根据文档写出 RNG、ID、编码和单帧结算的独立测试。
- `Docs/CardTable.zh-CN.md` 明确标记为从源内容生成的卡表，不反向定义规则规范。

## 5. P1：Solution 与架构护栏

### 目标

先让错误依赖无法进入代码库。

### 工作项

- 创建 `global.json`，固定当前 .NET 8 SDK feature band。
- 创建 `Directory.Build.props`：
  - `TargetFramework=net8.0`（普通项目）。
  - nullable、implicit usings、deterministic build。
  - checked arithmetic、分析器、警告即错误。
- 创建 solution 和第一批项目：
  - `Eota.Kernel`。
  - `Eota.Content.Compiler`。
  - `Eota.Transport.Contracts`。
  - `Eota.Kernel.Tests`、`Eota.Kernel.Algebra.Tests`。
  - `Eota.Content.Compiler.Tests`、`Eota.Architecture.Tests`。
  - `Eota.ReplayCli`。
- Server、Client 和 Godot 项目可先保留目录，在进入对应阶段时创建，避免空项目掩盖真实进度。
- Architecture Tests 检查：
  - Kernel 仅依赖 BCL。
  - Contracts 不依赖 Kernel。
  - 非 Godot 项目不引用 Godot。
  - 项目不引用 solution 之外的本地代码目录。
- 配置最小 CI：restore、build、test、格式与禁止依赖检查。

### 完成标准

- `dotnet build` 和 `dotnet test` 在没有 Godot 编辑器的环境中通过。
- 人为加入一条 Kernel → Godot/ASP.NET 的引用会使 Architecture Tests 失败。
- 仅凭仓库内的项目文件及声明的依赖即可构建。

## 6. P2：确定性地基、最小内容编译与回放骨架

### 目标

先证明“同样输入永远得到同样字节和哈希”，再写玩法。

### P2.1 稳定值对象

- 实现强类型 ID、`LaneId`、协议/内容/hash 值对象。
- 实现稳定错误码和 `Result<T>` 风格返回。
- 禁止权威路径使用随机 GUID、对象引用身份和默认 `GetHashCode()`。
- 固定两名玩家的规则身份，但禁止用玩家枚举次序产生优先级。

### P2.2 Canonical Encoding

- 实现显式字段顺序、长度前缀、UTF-8、整数位宽和字节序。
- 实现协议、内容、状态、回执和事件各自的 domain separator。
- 为字符串、集合、可选值、枚举和嵌套对象建立 golden bytes。
- 添加不同插入顺序产生相同编码的测试。

### P2.3 GlobalRuleRng

- 实现固定算法、seed expansion、有界随机 rejection sampling。
- 加入固定测试向量、调用计数、调用键和候选记录。
- 验证单候选和无冲突不消费随机数。

### P2.4 最小内容编译器

此时就建立 V2 内容管线，但只支持无效果内容：

- `schemaVersion`、ID、卡牌类型、职业、费用、基础身材/能量。
- `RuleContentPack` 与 `PresentationPack` 分离。
- 内容规范排序、重复 ID、非法引用和数值范围诊断。
- 使用专门的测试卡，不录入正式 CardTable 内容。

尽早建立最小编译器可以避免后续用硬编码卡牌对象开发，再额外重写一套内容加载路径。

### P2.5 最小 Match 与 Replay

- `MatchManifest`、`CompiledGameProtocol` 最小版本。
- 确定性创建两副牌的 `CardInstanceId`。
- 规范洗牌、起手、初始状态和状态哈希；此阶段还不实现回合推进。
- Replay CLI 支持 `record`、`verify` 和首次 hash mismatch 定位骨架。

### 完成标准

- 同一 manifest、内容、种子在独立进程中得到相同牌序、ID、RNG 计数和状态哈希。
- 改变任一规则输入会改变对应 manifest/hash。
- Replay 文件序列化往返后最终哈希不变。
- 目前没有任何 Godot、网络或系统时间参与测试。

## 7. P3：命令、区域与同时规划

### 目标

完成双方通过稳定命令构造最终计划的权威边界。

### 工作项

- 实现卡牌区域：Deck、Hand、Planning、Battlefield、Discard/Removed 等 VNext 明确定义的区域。
- 实现基础命令：
  - `SubmitMulligan`（协议可关闭）。
  - `PlanCard`。
  - `PlanSpell`。
  - `CancelPlan`。
  - `SubmitTurn`。
- 命令只引用 `CardInstanceId`、`LaneId`、slot 和稳定 `PlanCommandId`。
- 实现费用预留、返还、路线/目标合法性和每方提交锁定。
- 分开 `CommandReceipt` 与效果结算用 `IntentReceipt`。
- 实现 `MatchRevision`、每玩家 command revision 和命令日志格式；网络幂等键由 P5 Server 层加入。
- 明确同一玩家命令顺序有意义；不同玩家规划状态彼此隔离，网络到达顺序不能改变统一结算结果。

### 测试重点

- 使用手牌索引、错误拥有者、重复命令、过期 revision 均被稳定拒绝。
- 取消后卡牌身份不变、费用准确返还。
- 提交后不能修改计划。
- 交换双方命令到达顺序，最终计划规范编码相同。

### 完成标准

- 两名测试客户端可以完成计划、取消和提交。
- 命令日志可重建完全相同的 Planning State。
- 尚未实现卡牌效果，也不需要 Godot UI。

## 8. P4：统一结算帧与无效果完整对局

### 目标

先用系统规则打通正式的 Intent 管线，避免以后把即时修改玩法再重构一次。

### P4.1 单帧骨架

实施状态：P4.1A 的共同快照、冲突分组、数值归约、原子提交、回执/事件批次、生命周期检查点、终局短路和逐帧哈希已完成。后续切片已实现基础槽位占用、随从/场地部署与替换、可配置移动策略、迟缓、战斗、清理和九阶段驱动器，新增 73 帧完整对局 golden replay。通用 Continuation/触发队列与可恢复的阶段内调度仍未实现；当前范围与已确认语义见 [P4-Gameplay-Review.zh-CN.md](Review/P4-Gameplay-Review.zh-CN.md)。

- `FrameSnapshot`、`WorkItem`、`AtomicIntent`。
- `ConflictKey`、完整冲突分组、Reducer Registry。
- `CommitPlan`、原子提交、每 Intent 一份 Receipt。
- `DomainEventBatch`、下一帧队列和逐帧哈希。
- `Kernel.Step` 与 `KernelRunner.RunUntilBoundary`。
- 建立基础玩法所需的第一组正式 reducer：槽位占用、入场/替换、伤害、治疗、击杀/离场、场地能量和慢速持续时间。
- 从一开始就生成最终结构的 Tombstone；P7 再补齐复杂生命周期冲突、冻结观察者和 Frozen Source 消费。

即使是部署、移动、战斗和清理等系统规则，也应通过系统 WorkItem 产生 Intent，不允许先写直接改状态版本。

### P4.2 九阶段状态机

实施状态：无效果阶段已通过正式 WorkItem/Intent 管线贯通；`TurnResolver.ResolveReadyTurn` 运行至下一次 Planning 或终局，`MatchReplay.ReplayCommands` 支持跨回合命令回放。每帧固定状态、回执、事件哈希与 RNG 计数，CLI 可定位首个不一致帧。空效果阶段暂不派发触发器；阶段与清理合同见 [ADR-015](ADR/015-turn-progression-and-cleanup.md)。

按以下小切片逐个完成，每个切片都产生 replay：

1. 部署随从/场地和槽位占用。
2. 空入场效果阶段。
3. 空快速法术阶段。
4. 游击/追猎移动与槽位竞争。
5. 空战斗前充能阶段。
6. 战斗宣告、攻击宣告、先攻、普通伤害、斩杀和吸血。
7. 空慢速法术阶段。
8. 空回合结束效果阶段。
9. 持续时间、场地能量、以太衰减和新回合。

### P4.3 基础关键词

实施状态：以下关键词已进入 V2 编译器与系统结算；场地另支持 replace/replaceable，默认为空。已接受的规则分别见 [ADR-011：自动移动](ADR/011-automatic-movement.md)、[ADR-012：部署替换](ADR/012-deployment-replacement.md)、[ADR-013：攻击、防守与迟缓](ADR/013-combat-eligibility-and-slow.md) 和 [ADR-014：战斗伤害分段](ADR/014-combat-damage-frames.md)。

先实现内建规则关键词：`swift`、`guard`、`slow X`、`replace`、`replaceable`、`lifesteal`、`skirmisher`、`pursuit`、`firstStrike`、`execute`。

### 测试重点

- 所有路线战斗读取相同阶段快照。
- 调换玩家、路线和 WorkItem 枚举顺序结果不变。
- 多移动竞争和部署槽位占用规则稳定。
- 双方英雄同帧死亡为平局。
- 迟缓 X 的获得与消耗边界正确。

### 完成标准

- 仅使用无效果测试卡即可打完整局。
- 九阶段每个边界都有状态/事件/hash 记录。
- 所有系统 Intent 通过排列测试，没有即时修改旁路。

## 9. P5：Server/Client 端到端骨架

实施状态：已完成内存 MatchActor、Contract V0、PlayerOne/PlayerTwo/Spectator 投影、Client.Core、InProcess/WebSocket 传输和最小 Headless Host。两种传输及独立 Host 进程均能完成 73 帧无效果对局，服务端完整哈希与 P4 Replay CLI 基准一致；159 项全量测试通过。启动方式见 [传输契约说明](Transport/README.zh-CN.md)，验收与后续边界见 [P5 检查点](Review/P5-Server-Client-Review.zh-CN.md)。

### 目标

在效果系统变复杂前验证跨进程边界，避免最后才发现领域对象无法安全投影或传输。

### 工作项

- 创建 `Eota.Server.Application`、`Eota.Server.Host`、`Eota.Server.Infrastructure`。
- 创建 `Eota.Client.Core` 和 `Eota.Client.Transport.InProcess`。
- 实现内存版 `MatchActor`、有界邮箱和单逻辑写者。
- 定义 Contract V0 envelope、命令确认/拒绝、快照和表现帧。
- 实现 PlayerOne、PlayerTwo、Spectator 三套 audience 投影。
- 使用两个进程内测试客户端完成无效果对局。
- 建立最小 Headless Host 和健康检查；WebSocket 只需支持开发协议，不做生产鉴权。
- 加入完整 StateHash 与 ObserverViewHash 分离测试。

### 完成标准

- 两个 Client.Core 实例可以通过 Server.Application 打完无效果对局。
- 玩家二不能从玩家一投影中读到其手牌内容和牌库顺序，反之亦然。
- Server、InProcess 和 Replay CLI 使用相同 Kernel，最终完整状态哈希一致。

## 10. P6：基础效果语言与基础 Intent

实施状态：已完成。206 项测试、格式检查与五份固定回放通过；六张代表卡和 Set 冲突经 InProcess、WebSocket 双客户端与观战者完成验收。验收见 [P6 检查点](Review/P6-Effects-Review.zh-CN.md)，JSON 编写见 [Mod 指南](CardJsonDesignGuide.zh-CN.md)。

### 目标

实现能覆盖简单卡牌的第一条完整内容纵向切片：作者 JSON → 编译 IR → 帧求值 → Intent → Receipt/Event → 客户端投影。

### P6.1 编译器与 IR

- Trigger、Selector、Condition、Expression 的类型系统。
- `Parallel`、`IfElse`、`Retarget` 基础节点。
- 表达式使用自建 parser/AST，不在权威路径调用动态脚本引擎。
- 编译期检查目标类型、变量作用域、引用、数值和节点预算。

### P6.2 第一批 Effect Intent family

按依赖顺序完成效果侧 emitter 和尚未被基础玩法覆盖的 reducer；伤害、治疗、击杀等直接复用 P4 的正式 reducer：

1. Hero/Minion damage、healing 与 kill 的 Effect IR emitter。
2. Attack、MaxHealth、DamageTaken 等整数属性修改。
3. Keyword add/remove 与 `slow X` max/remove-wins。
4. FieldPower、费用上限、临时费用、以太增量和防衰减。

每个 family 先完成 reducer 代数测试，再接 Effect IR。

### P6.3 第一批 Trigger

- 自身入场。
- 本路/全场友方与敌方入场。
- 法术自身使用、本路友方法术使用。
- 自身受伤。
- 战斗时、攻击时。
- 随从/场地回合结束。

### 代表性卡牌验收夹具

先只根据 CardTable 手工建立少量 V2 测试内容，不开始 132 张正式填充：

- 哥布林雇佣兵：无效果基础实体。
- 赏金猎人：入场伤害。
- 流浪医师：英雄治疗。
- 举起盾牌！：条件化生命增益。
- 迅捷射击：快速法术与路线伤害。
- 奥术火花：共鸣条件分支。

### 完成标准

- 上述代表卡从 V2 JSON 编译并在正式 Kernel 管线运行。
- 同帧数值、伤害/治疗、Set、关键词添加/移除冲突通过排列测试。
- UI/Server 只看到投影事件，不接触 IntentReceipt 内部秘密。

## 11. P7：复杂冲突、触发链与区域操作

> 已完成。验收与检查点恢复结果见 [P7 验收](Review/P7-Complex-Effects-Review.zh-CN.md)，效果编写见 [Mod 指南](CardJsonDesignGuide.zh-CN.md)。

### 目标

完成 CardTable 全部内容所需的引擎表达能力和 VNext 最危险的冲突语义。

### P7.1 生命周期与离场

- Kill、Return、Transform、Banish、Replace 的统一生命周期冲突组。
- Tombstone、Frozen Source、冻结观察者集合。
- `EntityLeft`、`EntityDied`、`EntityBanished` 的明确区分。
- Death 同时触发死亡时与离场时；成功 Return／Replace 仅触发离场时；回手因容量不足失败则原实体死亡，同时触发死亡时与离场时；Banish 与 Transform 均不触发二者。
- 同帧离场观察者仍能观察其他实体离场。
- 后续帧只能从 Frozen Source 读取离场来源属性。

### P7.2 召唤、移动与槽位

- Summon 与 Move 进入同一槽位占用冲突域。
- 多召唤、召唤对移动、同方/敌方优先级和真正需要时的 RNG。
- 新实体稳定 ID、入场事件和后续触发。

### P7.3 手牌与牌库容量

- 普通抽牌、筛选检索、生成、回手；回手成功时按当前卡牌原型创建全新手牌实例，不搬运旧实例状态。
- DrawAllocationKey 与稳定牌库顶分配。
- 同批就绪请求统一处理回手 > 生成 > 抽牌；跨因果帧保留已提交结果，按 [ADR-020](ADR/020-hand-allocation-windows.md) 的开发提案实施。
- 同优先级溢出时才消费 RNG。
- 每个意图回执记录移出牌库、进入手牌、烧毁或未创建的具体身份。

### P7.4 Sequence 与结果传递

- `Sequence`、`Continuation` 和 Receipt Ledger。
- previous affected/created/removed targets。
- 标量结果，例如清除的以太值。
- 有界 `ForEach`、`Loop` 和稳定停止条件。
- `Applied`、`PartiallyApplied`、`Rejected`、`NoOp`、`Error` 的继续策略。

### P7.5 完整职业机制

- 以太活化、共鸣、清空和防衰减。
- 充能批处理、蓄能、充能需求修改。
- 有限持续增益、临时效果添加和到期移除。
- 手牌/牌库卡牌身材、关键词和费用修改。
- 全局/相邻路线选择器和表达式变量。

### 代表性验收夹具

- 换防：回手成功后召唤，以及容量不足分支。
- 图纸分析：筛选抽牌后修改实际抽到的实例。
- 以太回流：清空资源并按回执标量继续抽牌。
- 奥术爆震：显式有界循环与重复伤害。
- 发条侦察机、轻型反应炉：充能与蓄能整批判断。
- 夹子/毒雾/尖刺/爆炸陷阱：观察者、临时效果与自身离场。
- 民兵小队长：离场后从 Frozen Source 执行生成。
- 换防、击杀、放逐、变形同时指向同一实体的冲突矩阵。

### 完成标准

- VNext 规范第 22 节的交换幺半群、触发和随机测试全部实现。
- CardTable 中每种效果都能映射到现有 IR/Intent，不需要卡牌专用 C# 分支。
- 从任意合法检查点恢复与从头回放一致。

## 12. P8：按 CardTable 重新录入实验内容

> 已完成。531 项测试、7 份回放、91 个 P8 检查点及独立内容构建通过，详见 [P8 验收](Review/P8-Content-Review.zh-CN.md)。卡牌数值可继续用于平衡测试和调整。

### 目标

在引擎语言稳定后，以 V2 schema 录入首批 132 张实验卡，验证内容管线与规则行为；实验设计不代表平衡性定稿。

### 录入批次

| 批次 | 内容 | 数量 | 主要覆盖 |
|---|---|---:|---|
| A | Token + Core/Neutral | 12 | 基础实体、静态关键词、入场、抽牌、治疗、路线伤害 |
| B | Core/Guardian | 34 | 守备、替换、生成、召唤、回手、群体增益、场地监听 |
| C | Core/Arcanist | 29 | 以太、共鸣、清空标量、相邻/全局目标、有界重复 |
| D | Core/Artisan | 34 | 充能、蓄能、卡牌区修改、费用资源、机械条件与生命周期 |
| E | Core/Hunter | 23 | 陷阱、受伤/攻击监听、临时效果、野兽筛选、追猎/游击 |
| **总计** |  | **132** |  |

批次顺序主要按引擎复杂度安排，不代表职业优先级。

### 每张卡的 Definition of Done

- V2 规则 JSON 能通过 schema、类型、引用和确定性校验。
- 名称、描述、本地化键和表现资源元数据齐全。
- ID、类型、费用和数值与 CardTable 清单核对。
- 至少有一个数据驱动的效果场景；复杂卡包含冲突/空目标/容量不足等边界场景。
- 生成的规则描述或卡表与人工文本一致。
- 不新增按 CardPrototypeId 判断的 Kernel 特例。

### 内容校验工具

- 生成 `Docs/CardTable.zh-CN.md`，并与 `Content/Design/card-baseline.json` 做 ID／数量差异报告。
- 生成“IR node / Intent family → 使用卡牌”覆盖矩阵。
- 报告未被任何正式卡牌使用的能力和引用缺失。
- 内容包构建两次并逐字节比较，保证输出可复现。

### 完成标准

- 132 个 CardPrototypeId 全部重新录入，无重复、无遗漏。
- 全部内容编译为稳定 RuleContentPack/PresentationPack。
- 所有卡牌场景测试、代表性 golden replay 和内容覆盖报告通过。
- 内容构建输入为 `Content/Source`，差异报告基线为 `Content/Design/card-baseline.json`。
- 具备一键卡表生成与 emoji 卡图／透明图标生成工具；开发验收不承诺卡牌平衡性。

## 13. P9：完整 Godot Adapter

实施状态：2026-09-11 已完成 P9.5 AI 与 P9.6 交互迭代；当前全量 582 项测试通过。卡牌图鉴与牌组工坊独立，构筑先选择卡组或职业，再进入自动筛选卡池的编辑页。战场保持固定场景与双方槽位，增加私有规划反馈、耐久徽章、抽牌与碰撞动画；以太仍按玩家独立绘制、零级隐藏。联机必须提交与服务器相同的 RuleContentHash。本机开服占位的实际启动功能归 P10。截图与检查范围见 [当前记录](Review/P96-Interaction-Review.zh-CN.md)；界面体验继续接受实际游玩反馈。当前 Contract V0 使用完整视图帧替换，未新增独立 delta 格式；生产重连与补发归 P10。

### 目标

用稳定的 Client Contract 构建表现，不把规则重新引入 Godot。

### 实现顺序

1. 创建与实际编辑器版本一致的 `Eota.Godot.csproj`。
2. 实现应用启动、连接选择和 Client.Core 生命周期。
3. 先支持 InProcess transport，完成本地调试。
4. 建立 Battle Screen 的 ObserverSnapshot 全量渲染。
5. 建立稳定 ID → Node 的 View Registry。
6. 应用 ObserverDelta，处理重连后的全量替换。
7. 按 PresentationFrame 播放部署、移动、伤害、治疗、离场、抽牌和结果动画。
8. 实现规划拖拽、取消、提交和服务端合法提示。
9. 接入 WebSocket transport，并验证与 InProcess 行为一致。
10. 再实现牌组管理、对局设置、回放控制和内容浏览界面。

路状态前置缺口已补齐：冻结与锁闭作用于整路双方，权威状态、效果语言、持续时间、进出规则及公开投影均已接入，并通过机制测试和 61 帧固定回放验证。客户端据公开状态渲染；生命周期例外及版本变化见 [ADR-027](ADR/027-lane-statuses.md)。P8 当时的核对记录作为历史保留。

### 客户端素材与规则边界

- 场景布局位于 `Client/Godot/Scenes`，贴图和本地化由 `Content/Source` 生成。
- 卡图保持 512×512 方形；卡面分别安排插图区、名称、描述与数值布局，并保持图片比例。
- 快速法术、慢速法术、冻结、锁闭、以太图标作为独立客户端素材接入，使用 Content/Generated/Icons 下的 fast-spell、slow-spell、frozen、locked、ether PNG。生成工具通过通用配方维护这些素材。
- 冻结／锁闭图标显示在对应路的状态区域，由服务端公开状态驱动。
- 界面只提交命令和显示观察者投影，规则判断由内核和服务端执行。
- 动画只依赖 PresentationEvent；动画结束不驱动 Kernel。

### 完成标准

- 相同命令日志在 Godot 本地模式、远程 Server 和 Replay CLI 的权威最终哈希一致。
- 暂停、倍速、跳过动画或重建全部 Node 不改变比赛结果。
- Godot 命令不包含 NodePath、对象引用或手牌索引。
- 主要页面、卡牌、英雄、路线、协议行与牌组行均具有可直接编辑的固定 `.tscn`，脚本绑定数据和交互。
- 卡牌图鉴主列表仅显示基础卡；悬停临时预览，点击加框锁定，再次点击取消。选定卡牌的衍生卡显示在窗口右下角，可点击切换并返回原卡。
- 牌组工坊先选择已有卡组，或阅读职业介绍后创建新卡组；编辑页仅提供本职业和中立基础卡，返回列表保留每个卡组的草稿。
- 本机双人与人机分别设置；本机双人切换时先移除战场和手牌，再由下一位玩家确认进入。
- 待入场随从和场地在实际槽位显示绿框；路线及全局法术每张独立显示快速／慢速图标，悬停查看该张，提交前点击撤回该张，超过区域容量可横向滚动。
- 有限场地在卡面右下角显示初始／剩余耐久，无限场地隐藏徽章；战场预览在鼠标离开后消失。
- 抽牌飞入手牌、随从前冲碰撞只消费公开投影事件；随从攻击与打脸均沿所在路线垂直运动。隐藏抽牌使用卡背，暂停、倍速、跳过和席位交接保持正确清理。
- 随从受伤时生命数字为红色，满血且超过原型最大生命时为绿色；攻击低于／高于原型攻击时为红／绿，恢复原值后清除颜色状态。
- 以太归属 `(LaneId, PlayerId)`，零级不显示，每级显示一个图标；切换席位与观战时保持正确归属。
- 联机双方使用相同卡牌定义及效果内容，使用 RuleContentHash 校验；不要求双方使用相同牌组，图片和翻译不纳入规则哈希。

## 13.1. P9.5：本地人机对战

状态：2026-09-11 已实现并完成首版开发验收。三档 AI 可从大厅直接启动，48 场自动对局与三档 Godot 实际对局均结束；当前全量 581 项测试通过。实现、复现命令、截图与策略限制见 [P9.5 验收记录](Review/P95-AI-Review.zh-CN.md)。

### 玩家流程

在对局设置选择“人机对战”，设置己方牌组、AI 职业／牌组、难度及对战协议。玩家固定控制一方，AI 自动完成换牌、规划和提交；界面显示“对手思考中”。对局支持取消、返回大厅、重新开始和保存回放。

第一版提供三档难度，AI 牌组由同一构筑校验器检查，四职业均可使用：

| 难度 | 已实现策略 |
|---|---|
| 简单 | 保留起手牌，使用独立种子从合法候选中随机选择，无法继续时提交 |
| 普通 | 调整高费起手牌，按公开场面的攻防交换、英雄威胁、费用、卡牌效果、场地和单方以太评分 |
| 困难 | 更重视起手曲线，在普通评分上增加最多三步的组合搜索，比较费用分配与槽位冲突 |

这是本地启发式 AI；困难档尚不进行完整规则模拟或多回合预测。联网 AI 房间和 AI 对局实时观战延后扩展；保存的回放已支持观战视图。

### 架构与信息权限

- 新建 `Eota.Client.AI`，通过 Client.Core 和 Contracts 作为普通席位接入 InProcess 会话；由 Desktop 层装配，Godot 只呈现状态和配置。
- 输入仅包含该席位的 ObserverView、可公开的卡牌规则资料和服务端 PlanOptions。禁止访问完整 MatchState、对手手牌、牌库顺序、对手尚未公开的规划及规则 RNG 状态。
- AI 输出仅为 SubmitMulligan、PlanCard／PlanSpell、SubmitTurn 等正式客户端命令，逐次等待回执和新视图后再决策；拒绝或 revision 过期时重新同步并重新评估。玩家自己的 CancelPlan 操作保持可用。
- AI 决策使用独立种子和固定候选排序，不消耗 Kernel RuleRng；首版使用固定评估步数，避免把线程调度时间变成策略输入。
- 决策在可取消的后台任务中执行，不阻塞 Godot 主线程。退出、切换对局或对局结束立即取消任务，旧任务不得向新对局提交命令。
- 回放记录被服务器接受的命令。AI 算法版本、难度与决策种子作为诊断元数据保存，重放不要求再次运行同一版 AI。

### 实现批次

| 批次 | 交付 | 验证 |
|---|---|---|
| A：合法行动与会话闭环，已完成 | 定义决策接口、私有视图输入、换牌策略；简单 AI 从稳定排序的合法候选中按独立种子选择，无法继续时提交 | 四职业均能出牌／提交；空手、无费用、锁路、无目标和对局结束均能退出当前决策 |
| B：普通／困难策略，已完成 | 公开场面与卡牌效果评分；困难档搜索三步兼容组合；根据更新后的视图继续规划 | 同输入／种子／策略版本得到同输出；使用通用规则特征，避免按单卡 ID 写策略分支 |
| C：界面与回归，已完成 | 接入三档难度、牌组选择、思考状态、取消与重开，保存带 AI 元数据的回放 | Godot 三档实际对局均完成；独立 Replay CLI 验证困难档 360 帧，最终状态哈希一致 |

### 完成标准

- 人类对 AI、AI 对 AI 都能通过正式命令链完成对局；至少覆盖四职业的 16 种配对和一组固定种子。极长对局由测试上限标记为未结束并记录原因，不由 AI 绕过规则强制判胜。
- 单个规划阶段有候选／重试上限；无可行动作时必定提交，不出现忙循环。超时兜底只提交合法命令；真正的服务端超时结算沿用 P10 的可回放 SystemCommand。
- 不读取对手秘密信息：相同己方观察下，仅改变不可见手牌／牌序不应改变决策输入和结果。使用架构约束及配对场景检查。
- 非默认协议、起手换牌、替换、取消、路锁闭／冻结、单方以太均有决策链路覆盖。
- 比较普通 AI 与简单 AI 的胜率、耗时和无效命令率，报告固定测试集结果；不预先承诺胜率，也不把 AI 对战结果当作卡牌平衡结论。

首版限额：每次决策最多 512 个原始候选、2048 次评分，困难档束宽 8、深度 3；每阶段最多 64 条命令，连续三次拒绝停止并在界面显示失败。正常等待使用异步状态通知，不轮询或阻塞 Godot；返回大厅／重开会先等待旧 AI 退出。后续策略改进包括更准确的条件／充能／移动效果估值、更多牌组与种子的对照测试，以及多回合规划。

## 13.2. P9.6：游玩交互与动画反馈

状态：2026-09-11 已完成两轮反馈的开发与界面检查，详见 [交互与动画验收记录](Review/P96-Interaction-Review.zh-CN.md)。

- [x] 本机双人与人机使用独立固定页面；本机席位切换加入交接页。
- [x] 待入场随从／场地绿框、路线／全局法术图标与即时撤回反馈。
- [x] 图鉴悬停、点击锁定及取消；衍生卡从基础卡详情的右下角缩略卡进入。
- [x] 有限场地右下角耐久、战斗预览离开清空、协议文字对齐与客户端文案整理。
- [x] 卡组列表 → 职业介绍／已有卡组 → 编辑 → 保存；每个草稿独立保留。
- [x] 后续可读性微调：冻结／锁闭标记裁去显示留白，改为路线标题旁的 32 像素固定场景；适度放大路线标题、场上卡名和英雄信息。四职业介绍由 `Client/Godot/GameApp.DeckWorkshop.cs` 定义，并扩大显示区域。
- [x] 后续战斗反馈：攻击与打脸都沿路线垂直冲撞；生命受伤优先显示红字，满血强化显示绿字，攻击按原型值红／绿比较；每张法术独立图标，点击只撤回对应一张。
- [x] 抽牌飞入、随从前冲碰撞、隐藏卡背，以及暂停／倍速／跳过和回放清理。
- [x] 582 项全量测试，1600×1000／1280×800 实际 Godot 界面检查和本机／AI／回放回归。

## 14. P10：玩家开服、Server 持久化与联机加固

实施状态：2026-09-13 前三批、编辑器／准入范围修正及本轮卡组管理／可配置协议已验收，P10 整体仍进行中。卡牌编辑器纳入此阶段，提供 V2 内容包的属性编辑、效果 JSON、预览、校验、导入和导出。

### 本轮卡组与协议反馈已完成

- [x] 自建牌组及未保存的新草稿可删除，提供确认／取消；按稳定文件标识保存、重命名和删除，不留下旧名称副本，内置模板保留。
- [x] 本机开服不要求预选对方牌组，加入者提交自己的合法牌组。服务器参考牌组可为空，双席位都确认后才开始。
- [x] 协议页开放总张数、单卡最少／最多副本；构筑正常（本职业＋中立）、仅本职业、无限制（不限职业的基础卡）。卡池筛选、默认填充、保存和服务器准入使用同一协议规则。
- [x] 空牌库可选不疲劳、1／2／3…递增伤害、疲劳即死；按玩家独立累计，效果抽牌和回合抽牌一致，含同帧平局、满手烧牌、断点恢复与回放验证。
- [x] 疲劳计数进入权威状态、公开视图及回放，协议绑定当前状态格式；本地菜单与房间统一校验。见 [ADR-029](ADR/029-configurable-decks-and-fatigue.md)。
- [x] 全量 680 项通过（本轮新增 21 项）；两种窗口大小的实际点击验证删除和自定义构筑，独立双 Godot 开服、对战、补播及恢复成功。新增 [当前玩法指南](GameplayGuide.zh-CN.md)。

### 前轮编辑器与准入反馈已完成

- [x] 基本属性页增加页签下方及表单四周留白，扩大行距，与下方校验区域分开。
- [x] 场地耐久即强度：仅保留一个输入，永久场地立即禁用并不写耐久；规则层 fieldPower 与 fieldEnergy 也统一。
- [x] 禁止本路主动攻击、蓄能、关键词和触发效果集中在效果 JSON 对象，支持完整 JSON 往返。
- [x] 页首提供单卡／整包导出，单卡附带递归关联卡与本地化；按 ID 导入合并保留无关卡。
- [x] 新客户端提交逐卡哈希，只比对双方卡组并集及规则引用闭包。第二位确认前重新核对两个席位，拒绝本局相关定义缺失／不符，放行无关差异；恢复时同样复核，不泄露对方牌组。
- [x] 编辑器及真实双 Godot 不同内容包对局通过；自包含 Host 与内容同步构建。场地耐久和按卡组范围校验见 [ADR-028](ADR/028-field-durability-and-deck-rule-scope.md)。

### 第一批已完成

- [x] 固定 `CardEditor.tscn`：卡名／ID 搜索、职业／类型筛选，新建／复制／删除／恢复内置，类型对应属性、关键词与效果 JSON、完整 JSON、延迟刷新原型预览和编译诊断。
- [x] 编辑包独立保存、导入／导出、主动启用／切回内置；保存不改内置规则，启用不改变已启动服务器的内容快照。
- [x] `LocalServerLauncher` 启动随包 self-contained Windows Host；启动、可加入、停止、失败状态，参考牌组／协议／监听范围，三种席位邀请和恢复上次房间。当前界面仅选择自己的参考牌组。
- [x] 开局前双方分别确认牌组；服务器编译并冻结自己的内容包，校验规则哈希、协议哈希、卡牌存在性、职业、来源、数量和重复项。双方可以使用不同合法牌组。
- [x] 每个席位独立随机凭据；HTTP 准入与 WebSocket 都鉴权，WS 再核对双哈希。客户端仅提交 ID／数量及正式命令，不提交卡牌数值或效果实现。
- [x] 有序日志先 flush 再 ACK，每 16 条日志原子检查点；哈希链、恢复尾部命令、席位命令去重恢复、损坏隔离，单局存储失败不拖垮其他局。
- [x] 沿用有界邮箱和慢观察者断开，增加房间接口限频、WS 命令限频、消息与分片时间限制、日志容量上限。
- [x] 602 项测试；编辑器在 1600×1000／1280×800 验收，两个独立 Godot 客户端完成对局并核对结果；验证端口释放、占用失败后重试及跨 launcher 恢复到同一视图哈希。

### 后续工作

- [ ] 更大规模及长时间的持续生成／离场、网络恶意流量与尾延迟验收；已有 16 局并发基线，不能替代完整容量目标。
- [ ] 设计 CardInstances／Tombstones／CommandLog 的历史压缩，验证 Frozen Source、续执行及当前回放语义；当前仅裁剪传输缓存、去重记录及磁盘日志分段。
- [ ] Kernel 在人为构造的耗尽状态下生成正式终止事实的语义。恢复／服务端接纳已拒绝危险计数状态，但外围隔离不等同于新增可回放的内核终止合同。
- [ ] 已发布游戏完整安装包与无 SDK 的独立干净系统验收。服务器已构建为包含 .NET／ASP.NET 运行时的 self-contained 产物，并由游戏直接运行 exe；完整发布矩阵仍归 P11。

### 第二批已完成：重连与限时对局

- [x] 独立的远程会话重连循环，保留原邀请凭据与席位；核对规则、协议、房间时限设置和恢复 revision，拒绝进度倒退。静默连接用心跳检测，失败逐步退避重试；退出对局终止重连。
- [x] 请求等待有上限；提交没有收到 ACK 时，以相同命令 ID、原 expected revision 重试，沿用服务端持久化去重。临时服务故障和限频可重试，协议／凭据不兼容停止重连。
- [x] 开服页增加不限时／30／60／120／180 秒；入场前显示并确认时限，限定房间的准入与 WS 同时校验 RoomSettingsHash。时间设置属于房间策略，独立于纯玩法协议哈希。
- [x] 服务端到期只替未提交玩家执行 SystemTimeoutCommand：换牌保留手牌，规划保留已部署牌并提交。命令携带回合／阶段／玩家 revision，正常网络命令无法伪造系统超时。
- [x] 截止时间原子落盘，重启沿用已记录的截止时间；超时命令进入同一日志／检查点／回放链，恢复不依赖重新读取当年的时钟。支持桌面回放和 Replay CLI。
- [x] BattleScreen 固定倒计时节点，暂停或倍速动画不改变提交时限；已提交显示等待对方，最后 10 秒变色，不限时对局隐藏。
- [x] 全量 614 项测试（本批新增 12 项）；双 Godot 客户端验收倒计时、主动断开后自动恢复席位、完成对局与服务器恢复；1280×800 菜单和战场交互回归通过。

### 第三批已完成：补发、日志分段与运行诊断

- [x] 每个 audience 独立保留至多 128 条观察记录／1 MiB；按原 revision 和观察者哈希匹配补发，历史缺失、席位不同或报文过大时返回当前快照。断线期间也记录各席位投影。
- [x] 客户端原子校验整批视图哈希、席位、帧顺序与容量后补播；非法批次不发布部分动画。Godot 重建战场时保留补发队列，修复旧连接关闭期间的同步竞态。
- [x] 默认 4,096 条命令去重窗口；窗口内返回原 ACK，窗口外已接受操作由原玩家 revision 阻止重复执行，不再因累计键数达到上限永久拒绝新操作。
- [x] 每 512 条日志建立校验过的完整恢复根，先原子写 journal-head 再替换旧分段；恢复绑定初始 manifest、房间 ID、哈希链与近期去重记录。验证两个替换之间中断及损坏隔离。
- [x] 接入 BCL Meter／ActivitySource，私有 runtime-diagnostics 每 5 秒覆盖写入有界指标。战场增加“导出诊断”，只导出序号、哈希、错误类别及本席位最近操作，不带卡牌列表、手牌、凭据、原始命令或规则 RNG。
- [x] 恢复时校验 ID 唯一性／不复用及分配余量；覆盖 16 类计数耗尽和既有中间帧检查点恢复，不重录 golden 回放。
- [x] 本批新增 33 项测试，全量 647 项通过。16 局并发完成 1,536 次合法与 768 次非法操作，16 个慢观察者被独立断开；双 Godot 进程验证帧补播和诊断导出。

### 目标

将 P5 的开发骨架提升为可恢复、可诊断、可限制资源的服务端。

### 工作项

- 完成本机开服：用 `ILocalServerLauncher` 替换当前 unavailable 实现，从游戏内选择对局名称、端口、监听范围、牌组和协议，启动受管理的独立 Host 进程。
- 开服状态区分启动中、可加入、停止中、失败与已停止；就绪后显示玩家／观战加入地址并提供复制、停止按钮。端口占用、启动失败和异常退出有明确反馈。
- 开服使用应用打包的服务器与当前规则内容；仅管理本次启动的进程。关闭房间或退出游戏时按既定流程关闭，不能误停其他服务器。
- 房间由服务器校验双方提交的牌组与本局相关卡牌定义，锁定服务器 RuleContentHash／ProtocolHash 后进入对局；玩家的完整内容包可以不同。结合席位凭据、鉴权与版本校验。
- 已接受命令追加日志和原子检查点。
- 进程重启恢复、逐记录哈希验证和损坏隔离。
- WebSocket 鉴权、席位绑定、命令幂等、revision 与 server sequence。
- 断线重连、补发窗口、超窗 snapshot resync。
- 邮箱背压、消息大小限制、命令频率限制和 ResolutionBudgets。
- 服务端超时转换为可回放 `SystemCommand`。
- 日志、指标、trace 和失同步诊断包。
- 多局并发、慢客户端和恶意输入测试。
- 纳入 [P0–P6 复查](Review/P0-P6-Code-Review.zh-CN.md) 后续项：恢复状态的 ID／revision 耗尽合同，以及历史保留、命令去重窗口和长期对局资源预算。

数据库、对象存储或部署平台仍由 Infrastructure Adapter 决定，不修改 Kernel。

### 完成标准

- 任意阶段进程重启后可从检查点和命令日志恢复到相同哈希。
- 重复、乱序、过期和越权命令均有稳定响应。
- 玩家/观战者投影通过隐藏信息审计。
- 单局异常不会终止其他 MatchActor。
- 玩家从游戏内开服，第二个独立 Godot 客户端能够加入并完成对局；停止后端口释放，失败重试不会遗留进程。没有安装开发 SDK 的发布环境也能使用开服功能。

## 15. P11：发布验收与协议冻结

### 工作项

- 冻结首个 `ProtocolVersion`、`EffectLanguageVersion`、`RandomAlgorithmVersion`、`CanonicalStateVersion` 和 Contract Version。
- 建立不可修改的官方 golden replay corpus。
- Windows/Linux Headless Server、Debug/Release 和 Godot 支持平台交叉验证。
- 运行 reducer 属性测试、内容全量测试、命令 fuzz、回放篡改和 Server load tests。
- 输出当前协议 manifest、规则内容 manifest、构建产物清单和运行环境矩阵。
- 文档同步：玩法、卡牌 V2 作者指南、Server 运行指南、Godot Adapter 指南和回放诊断指南。

### 完成标准

- 架构文档第 18 节与 VNext 规则文档第 24 节全部勾选。
- 官方 282 张卡内容包在所有目标平台得到相同 RuleContentHash。
- 官方 replay corpus 在所有权威运行方式得到相同逐阶段与最终哈希。
- 新增 Web Adapter 只需依赖 Transport Schema，不需要引用或修改 Kernel。

## 16. 可以并行与不能并行的工作

### 可以并行

- P4 完成后，P5 Server/Client 骨架可以与 P6 效果 reducer 开发并行。
- Contract V0 稳定后，Godot 的菜单、资源加载和纯表现组件可以提前开发。
- P7 完成某一能力族后，对应的 P8 内容批次可开始录入，不必等待所有职业能力完成。
- P5 后，Server 持久化 Adapter 可以和 CardTable 内容录入并行。

### 不能提前

- Canonical Encoding、ID 和 RNG 未冻结前，不能积累 golden replay。
- 单帧 Intent 管线未完成前，不能实现卡牌动作的即时可变版本。
- Effect IR 与目标类型系统未稳定前，不能批量填写 132 张卡。
- Observer Contract 未稳定前，不能构建完整战斗 UI。
- 生命周期和容量冲突测试未通过前，不能宣称效果引擎完成。

## 17. 明确避免的实施路线

- 不在 `Eota.Kernel` 中引入界面控制器或直接修改共享对象的结算逻辑。
- 不先复制 132 个旧 JSON，再围绕旧格式设计编译器。
- 不为每张特殊卡增加 C# 类或 `switch (CardPrototypeId)`。
- 不先做完整 Godot 战斗画面，再从动画反推规则事件。
- 不先实现联网同步完整 `MatchState`，以后再补隐藏信息。
- 不把所有功能写完后才做交换律、排列和回放测试。
- 不让 InProcess 模式绕过 Contracts 直接操作 Kernel。
- 不因首版只有 Godot 就让传输 DTO 引用 Godot 类型。

## 18. 建议的第一个实际开发批次

第一批编码只做 P0、P1 和 P2 的最小闭环：

1. 完成 8 个基础 ADR 和未决规则清单。
2. 建立 solution、统一构建设置和架构测试。
3. 实现第一组强类型 ID 与 Canonical Writer。
4. 实现 GlobalRuleRng V1 及固定测试向量。
5. 编译两张无效果测试卡和一个 DefaultProtocol 开发版。
6. 用固定 seed 创建比赛、规范洗牌、发起手牌并输出状态哈希。
7. Replay CLI 在第二个独立进程中验证相同哈希。

该批次通过后再进入规划命令和九阶段玩法。它规模小，但会验证整个项目最昂贵、最不能晚改的确定性基础。

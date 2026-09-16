# P6 基础效果语言与 Intent 检查点

> 日期：2026-09-08  
> 状态：基础效果纵向切片完成，代表卡、排列冲突、回放与双客户端验收通过。  
> 前序：[P5 Server/Client](P5-Server-Client-Review.zh-CN.md)。P5 没有阻塞项，也没有要求用户手动执行的验收。  
> 编写说明：[Mod JSON 指南](../CardJsonDesignGuide.zh-CN.md)。

## 已实现

- 类型化 Trigger、Selector、Condition、Expression IR；Parallel、IfElse、Retarget；自建表达式 parser/AST，常量折叠、目标类型、作用域、未知引用和预算诊断。效果定义进入 RuleContentHash。
- 效果直接生成正式 Intent，复用伤害、治疗、击杀和生命周期 reducer。数值属性支持 Set/Add/Multiply/Divide；Set 按目标控制方优先，再对多个同方候选调用全局 RNG。
- 随从关键词和场地 replace/replaceable 的添加/移除；Remove 优先，slow 按最大剩余次数合并。战斗记录迟缓获得代数，避免新获得状态提前消耗。
- 有限场地能量、费用上限、当前/下回合临时费用、以太和一次性防衰减。永久场地保持独立类型，不接受数值能量修正。
- 自身/友方/敌方入场、法术自身使用、本路友方法术使用、自身受伤、战斗、攻击、随从/场地回合结束。
- 提交后按批次推进触发，不递归提交。致死受伤保留冻结来源属性；已移除实体不再成为 self 目标。运行时算术错误和触发/Intent 超预算经正式 Error 帧回滚并停止比赛。
- 客户端仍只接收投影视图和事件；新增状态不会暴露 IntentReceipt、RNG、Set 候选或 Tombstone。nextTurnCost 只投影给本人，并同步更新 Contract V0 Schema。

数值、关键词、触发时序分别记录在 [ADR-016](../ADR/016-numeric-effect-reduction.md)、[ADR-017](../ADR/017-keyword-updates.md)、[ADR-018](../ADR/018-basic-trigger-scheduling.md)。三份记录各自围绕一个决策，保持 Proposed，按 ADR 索引约定在首个公开协议冻结前统一复核；不构成本阶段开发的等待条件。

## 验收结果

Release 构建 0 warning / 0 error。206 项测试通过：Kernel 108、Algebra 1、Content.Compiler 54、Architecture 5、Server.Integration 38。排列测试位于 Kernel 的 FrameResolverTests.Effects，覆盖 720 种数值/关键词组合排列以及资源、场地、迟缓冲突。

新增 [P6Effects](../../tests/Fixtures/P6Effects/README.zh-CN.md) 根据 CardTable 手工编写六张 V2 测试卡：哥布林雇佣兵、赏金猎人、流浪医师、举起盾牌！、迅捷射击、奥术火花。它们经正式命令、回合调度和客户端投影运行；额外例测覆盖受伤英雄的实际治疗、护卫加成、共鸣两分支、空目标、死亡来源、触发时序、先攻只出手一次和错误回滚。

[P6Set](../../tests/Fixtures/P6Set/README.zh-CN.md) 固定每个英雄的一份敌方 Set 和两份己方 Set，验证每个属性的筛选、抽样、回执和哈希。两份新夹具均通过 InProcess 与真实 WebSocket 的双客户端和观战者验证，逐帧检查完整 Kernel 回放。篡改 Set 所选 IntentId 后，CLI 在第 4 帧返回 ReplayHashMismatch（退出码 5）。

格式检查及五份 CLI 回放核验通过：

| 夹具 | 结果 | 最终 StateHash |
|---|---|---|
| InitialReplay | 开局 | `9f960dfa69f7c9e8532d8fa0212d20aaa9ad1b7466f1316ebf21ca2e3d4521a9` |
| P4Match | 73 帧，4 回合，PlayerOneWon | `ffc45debfb84e4d5c0d29ebed600fb611356d89e67ffc6b490bd9defb35d6389` |
| P4CenterMovement | 20 帧，进入第 2 回合 | `5bdb4eda830e0186ffe02f071105b5c20e388c22ab0f7c86229d5e4d888fdb9e` |
| P6Effects | 61 帧，进入第 4 回合 | `d7fd7ac22bc730192cb237d22f22bf659bfab8d4b52849569c17889135e0c382` |
| P6Set | 21 帧，进入第 2 回合，规则 RNG 增加 2 次 | `bd2db544eb23f2a98f2410c1a88822c8cdac2c5069ddf04dacbec97c692d9571` |

## 版本变化

当前仍为开发 ProtocolVersion/ContractVersion 0。新增规则状态、效果内容和 RNG 调用需要显式版本变化：

| 范围 | P5 → P6 |
|---|---|
| Protocol canonical | 2 → 3 |
| RuleContent canonical | 3 → 4 |
| MatchState canonical | 5 → 6 |
| Receipt / Event canonical | 2 / 3 → 2 / 4 |
| RandomCallSchemaVersion | 2 → 3 |
| EffectLanguageVersion | 1 → 2 |

新状态编码包含玩家 nextTurnCost、随从 SlowGeneration、Tombstone 的冻结最终实体。协议哈希包含触发帧和效果 Intent 预算。协议编译拒绝不支持的规则版本，不以旧版本标签运行新语义。

开局回放格式仍为 1，完整回放格式仍为开发版 2，新增 numericSetChoices。旧三份夹具已按新版本重建；P4 的帧数、结果和中心移动 RNG 行为保持一致。P4/P5 检查点保留当时的验收哈希，当前基准以本表和夹具为准。

## 下一阶段边界

P6 没有必须由用户手动执行的测试。P7 继续通用生命周期冲突、Sequence/ReceiptLedger、可恢复触发队列、区域操作、充能、临时持续效果和复杂容量规则。P6 的完整回合回放不等同于任意因果帧可恢复检查点；也尚未开始 132 张正式卡牌填充或 P9 Godot UI。

# P4 基础玩法与完整对局检查点

> 日期：2026-09-07  
> 状态：基础玩法切片已通过自动验收与人工复核；确认结果已实现，决策拆分为 Accepted 的 ADR-011–015。原 ADR-010 保留替代关系索引。  
> 前序检查点：[P4 单帧复核](P4-Frame-Review.zh-CN.md)。

> 后续进度（2026-09-07）：Server/Client 与 Transport 基础已在 [P5 检查点](P5-Server-Client-Review.zh-CN.md) 完成。下文保留 P4 完成时的实施与验收范围。

> 后续进度（2026-09-08）：基础效果已在 [P6 检查点](P6-Effects-Review.zh-CN.md) 完成；该检查点列出版本升级后重建的当前回放哈希。下文哈希为 P4 历史基准。

## 本次续接结果

续接时，部署、移动、战斗、清理和 TurnResolver 代码已存在，Release 构建通过；71 项测试中初始 golden replay 的状态哈希失败，玩法检查点缺失，Replay CLI 仍只支持开局。

已补齐跨回合命令回放、CLI 完整对局记录与核验、73 帧 golden corpus 和回归测试。人工复核后增加了独立的移动冲突/方向配置、场地 replace/replaceable，以及奇数路线中心随从的双向随机选择。当前 Canonical State Version 为 5，协议 canonical schema 为 2，规则内容为 3，RandomCallSchemaVersion 为 2；现有回放已重建，另新增五路线随机移动夹具。

## 已实现并验证

- 规划卡牌经统一 Intent 部署随从/场地，处理 replace/replaceable 与槽位占用。
- 移动冲突支持中心优先、外侧优先或全部失败；目标方向独立支持向外/向内，默认中心优先、向外。按位置优先时校验总路线数为偶数。
- 奇数路线正中心随从向外移动且两边都可移动时使用规则 RNG；零个或单个方向不消耗样本。选择记录可回放，RNG 与整帧原子提交。
- 九阶段系统回合：空入场效果、快速/慢速空法术消耗、移动、空充能、战斗、空结束效果、清理和下一回合。
- 基础关键词：swift、guard、slow X、replace、replaceable、lifesteal、skirmisher、pursuit、firstStrike、execute。
- 共同战斗快照、战斗/攻击宣告帧、先攻死亡检查点、普通同时伤害、斩杀、同帧英雄伤害与吸血。
- 场地能量衰减、以太衰减与一次性保护、费用增长/恢复、新回合抽牌、满手烧牌和空牌库无疲劳。
- 终局时停止慢速法术、清理和下一回合；无效果测试卡可以经正式命令入口打到胜负。
- 反转玩家、路线、实体、卡实例及系统 Intent 枚举顺序，状态/回执/事件哈希一致；九阶段边界都有提交记录。
- `MatchReplay.ReplayCommands` 跨回合重放命令；首个被拒绝的命令返回索引和原因。
- CLI 完整回放核验初始 manifest、所有帧及最终结局；篡改第 4 帧事件哈希会报告第一处差异为第 4 帧，缺少 playerId 的命令会被格式校验拒绝。

## 已确认并实现的玩法语义

以下四项已完成人工复核并落实到代码与测试：

1. 多个移动竞争同一己方槽位时由 config 决定中心优先、外侧优先或全部失败，默认靠中心的随从优先；按位置选赢家要求总路数为偶数。只考虑帧开始时空闲的槽位。多个可选方向由另一项 config 决定向外或向内优先，默认向外。**奇数路最中心的随从向外移动时，如果两边都可移动，使用 RNG 选择方向**；只有一边可移动时直接选择该方向，不消耗 RNG。见 [ADR-011](../ADR/011-automatic-movement.md)。
2. slow X 同时阻止主动攻击和防守，swift 不能覆盖；战斗开始已有迟缓在本次战斗后减 1。见 [ADR-013](../ADR/013-combat-eligibility-and-slow.md)。
3. execute 在具有随从战斗伤害资格时，即使攻击为 0 也会添加 Kill；最终帧同归于尽仍可斩杀对手。见 [ADR-014](../ADR/014-combat-damage-frames.md)。
4. 场地也可以具有 replace/replaceable：新场地有 replace 或旧场地有 replaceable 即允许替换；默认没有关键词，不能替换。随从和场地替换均只产生离场事实，不产生死亡事实。见 [ADR-012](../ADR/012-deployment-replacement.md)。

回合顺序、终局短路、清理与新回合资源见 [ADR-015](../ADR/015-turn-progression-and-cleanup.md)。此次按独立决策整理文档，未改变已确认的规则、代码或协议版本。

默认协议配置（完整取值和例子见 [ADR-011](../ADR/011-automatic-movement.md)）：

```json
{
  "laneCount": 6,
  "movementConflictPolicy": "centerFirst",
  "movementDirectionPreference": "outwardFirst"
}
```

场地作者 JSON 使用 `"keywords": ["replace"]` 或 `"keywords": ["replaceable"]`；可以同时具备，也可以省略。

此前已确认的治疗上限、可变英雄最大生命、同帧吸血救回致命伤害仍按 ADR-003/006 执行。

## 当前编码版本与回放入口

| 项目 | 当前版本 |
|---|---|
| 协议版本 | ProtocolVersion 0 |
| 协议 / 状态 / 规则内容 canonical schema | 2 / 5 / 3 |
| 回执 / 事件 canonical schema | 2 / 3 |
| RNG 算法 | splitmix64+xoshiro256ss/v1 |
| RNG 调用语义 | RandomCallSchemaVersion 2 |
| 开局 / 完整命令回放格式 | FormatVersion 1 / 2 |

`MatchReplay.ReplayCommands` 按给定命令顺序调用正式命令入口，并在每次双方提交后自动结算至下一个 Planning 边界或终局。首个拒绝命令使回放停止并返回索引和原因，不会排序或忽略无效命令。P3 的 `CommandLogReplay.Replay` 继续用于只含规划的日志。

方向选择由 WorkItem 声明候选，在帧规划中按来源 EntityId、IntentId 规范排序后统一取样，再按选定目标构造完整槽位冲突组。CommitPlan 保留选择记录与拟提交的 RNG 状态；重复求值或查询 Plan 不修改权威 RNG。调用合同见 [ADR-005](../ADR/005-global-rule-rng.md)。

CLI `record` / `verify` 用于开局，`record-match` / `verify-match` 用于完整命令回放。完整回放固定初始 manifest、命令、每帧前后状态/回执/事件哈希与 RNG 计数、最终状态及胜负；`movementRandomChoices` 另记录和核验左右候选、选择结果、调用键及样本消费量，无选择时为空。具体命令见下方验收及夹具说明。

## 尚未完成的范围

- 通用 Continuation、触发器、Receipt Ledger，以及可保存/恢复的阶段内待执行队列。
- Effect IR、充能、临时增益及其持续时间清理；目前对应效果阶段为空。
- Return/Replace/Banish/Transform 综合生命周期冲突、Frozen Source、冻结观察者，以及通用抽牌/生成/回手容量窗口。
- Godot、Server、Transport 与正式 132 张卡内容录入。

当前 TurnResolver 只能从 ReadyToResolve 运行至 Planning 或终局；不能把它视为通用的任意帧恢复入口。上述调度与效果能力在后续 P6/P7 切片接入。

## 验收记录

```powershell
dotnet build Eota.sln -c Release --no-restore
dotnet test Eota.sln -c Release --no-build --no-restore
dotnet format Eota.sln --verify-no-changes --no-restore
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify tests/Fixtures/InitialReplay/Cards tests/Fixtures/InitialReplay/protocol-v0.json tests/Fixtures/InitialReplay/initial.replay.json
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify-match tests/Fixtures/P4Match/Cards tests/Fixtures/P4Match/protocol-v0.json tests/Fixtures/P4Match/match.replay.json
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify-match tests/Fixtures/P4Match/Cards tests/Fixtures/P4CenterMovement/protocol-v0.json tests/Fixtures/P4CenterMovement/match.replay.json
```

Release 构建 0 warning / 0 error；126 项测试通过（Kernel 97、Algebra 1、Content.Compiler 24、Architecture 4）；格式检查、开局/完整对局/五路线随机移动回放核验通过。中心随机移动覆盖固定种子的左右选择、零/单候选不取样、双方共享全局流、枚举顺序一致、槽位冲突不重选，以及整帧失败回滚 RNG。回放同时核验每个随机选择记录。

完整夹具见 [P4Match 使用说明](../../tests/Fixtures/P4Match/README.zh-CN.md)：4 回合、73 帧、PlayerOneWon，最终状态哈希为 `4667cf4b31d96c55025cd18ca2ac144111791c89b29430428de0feeccb7b3fe4`。

新增 [P4CenterMovement 夹具](../../tests/Fixtures/P4CenterMovement/README.zh-CN.md)：20 帧完成一个回合，在第 7 帧从第 2 路的左右候选中随机选第 1 路，RNG 样本计数从 8 增至 9；最终状态哈希为 `4090ae0704cbc8ffea3ca30c8248b177a7023b740ed35ef40d5894ebac848a52`。

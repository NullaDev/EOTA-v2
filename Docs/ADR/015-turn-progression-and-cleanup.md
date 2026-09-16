# ADR-015：九阶段回合与清理后的新回合

- 状态：Accepted
- 日期：2026-09-07
- 接受日期：2026-09-07
- 来源：从 [ADR-010](010-p4-system-gameplay.md) 拆分，保留已确认语义。

## 背景

系统玩法也必须经过正式帧管线；阶段边界、资源清理和终局停止不能成为直接修改状态的旁路。

## 决定

双方提交后进入 ReadyToResolve，依次执行：

1. 部署。
2. 入场效果。
3. 快速法术。
4. 移动。
5. 战斗前充能。
6. 战斗。
7. 慢速法术。
8. 回合结束效果。
9. 清理。

清理完成后开始下一回合 Planning。阶段变化本身也是一帧 Intent 提交，空效果阶段保留边界事件。部署、移动、伤害、死亡、法术消耗、以太衰减与回合推进均经过系统 WorkItem → Intent → Reducer，遵循 [ADR-006](006-effect-frame-semantics.md)。

任一提交使比赛 Finished 或 Failed 后，不再执行后续帧；战斗终局会停止迟缓消耗、慢速法术、清理和下一回合。

## 清理与资源重置

- 有限场地按协议减能量，永久场地不自然衰减；能量耗尽的生命周期处理遵循 [ADR-007](007-lifecycle-and-tombstones.md)。
- 以太衰减受协议下限限制；PreventNextEtherDecay 抵消一次衰减并被清除。
- BeginNextTurnIntent 增长费用上限至协议限制、补满当前费用、按牌库顶顺序抽牌并解除提交锁定。
- 满手时抽出的牌进入 Removed 并产生 CardBurned；空牌库不疲劳。日常抽牌与 [ADR-009](009-match-bootstrap-and-mulligan.md) 的开局 bootstrap 分开。

## 实现边界

P4 时抽牌属于系统新回合 reducer。P7 开发实现已按 [ADR-020 提案](020-hand-allocation-windows.md) 将其拆为新回合资源 Intent 和通用 HandCardIntent，烧牌仍进入 Removed。历史切片验收见 [P4 检查点](../Review/P4-Gameplay-Review.zh-CN.md)，当前执行进度见 [P7 检查点](../Review/P7-Complex-Effects-Review.zh-CN.md)。

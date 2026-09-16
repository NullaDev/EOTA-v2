# P0–P2 人工复核记录

> 首次检查点：2026-09-04  
> 复核完成：2026-09-06  
> 状态：已完成；后续实现以 Accepted ADR 为准。

## 复核结果

- Kernel 边界、Canonical Encoding V1、SHA-256、GlobalRuleRng V1 和 Transport 分层由实现方负责选型，现已接受。
- Return 回手改为旧实体离场，并按其当前卡牌原型在手牌创建全新 `CardInstanceId`；不继承生命、伤害或 Buff。
- 起手牌不是普通抽牌。新增独立、可由协议关闭的 Mulligan bootstrap；默认协议关闭，但 Kernel 保留完整换牌能力。
- 权威数值采用 `Int64`、checked 算术、规范有理倍率和最终一次向零取整；攻击与费用允许为负，但显示、伤害和支付按 `max(0, value)`。
- 英雄生命、随从当前/最大生命和有限场地能量的非正值会分别触发死亡或破坏检查；永久场地与有限场地分型，不显示能量也不自然衰减。
- Death 与 Leave 分为两个扳机。死亡同时产生死亡和离场；成功 Return／Replace 仅离场；回手容量失败使原实体死亡，同时产生死亡和离场；Banish 两者都不产生；Transform 原位改变形态，两者都不产生。
- Parallel 与 Sequence 的语义及失败策略已由 [ADR-006](../ADR/006-effect-frame-semantics.md) 固定。

详细合同见 [ADR 索引](../ADR/README.md)，特别是新增的 [ADR-009](../ADR/009-match-bootstrap-and-mulligan.md)。当前规则以各 ADR 明确的状态和语义为准。

## 更新后的固定回放

P3 引入区域、换牌状态、命令日志和新的规范字段后，开发期 `ProtocolVersion = 0` 的 golden corpus 已按规则重建：

| 项目 | 固定值 |
|---|---|
| seed | `123456789` |
| protocol hash | `23650d72d862d8b45fc82fdd4fcd230833510bebb2cebfe71531e2081877aae4` |
| rule content hash | `2d92c788b58ac143abb435debf59c8882e0d8898faf4e4495d488c141bc5db64` |
| initial state hash | `a1c7530e45f4d5607555e8c4c6d2a9ddd6b76c8e7153152110494c5222b8a790` |
| RNG sample count | `6` |

Canonical Encoding 和 RNG 原始测试向量没有改变；P4 帧状态加入实体、Tombstone 和比赛结果后，状态编码版本升为 3；规则内容规范编码版本仍为 2。

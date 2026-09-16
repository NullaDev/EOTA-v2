# ADR-010：P4 系统玩法与九阶段回合（已拆分）

- 状态：Superseded
- 日期：2026-09-07
- 原接受日期：2026-09-07
- 拆分日期：2026-09-07

## 拆分原因

原记录同时包含多个可独立变化的规则决策，以及实现进度、编码版本和 CLI 使用说明。现在按决策边界拆分；已确认语义和 Accepted 状态由下列记录承接。

## 替代关系

| 原主题 | 当前决策 |
|---|---|
| 自动移动候选、方向偏好、槽位竞争、奇数中心随机方向 | [ADR-011：自动移动](011-automatic-movement.md) |
| 随从/场地 replace、replaceable 与部署替换权限 | [ADR-012：部署替换](012-deployment-replacement.md) |
| 入场限制、swift、guard、场地禁攻与 slow X | [ADR-013：攻击、防守与迟缓](013-combat-eligibility-and-slow.md) |
| 战斗宣告、firstStrike、execute 与最终同帧伤害 | [ADR-014：战斗伤害分段](014-combat-damage-frames.md) |
| 九阶段顺序、终局短路、清理与新回合资源 | [ADR-015：回合推进](015-turn-progression-and-cleanup.md) |

RNG 算法、调用排序与原子提交继续由 [ADR-005](005-global-rule-rng.md) 定义；规范编码由 [ADR-004](004-canonical-encoding.md) 定义；替换的生命周期事实由 [ADR-007](007-lifecycle-and-tombstones.md) 定义。

当前版本号、回放入口与验收结果见 [P4 检查点](../Review/P4-Gameplay-Review.zh-CN.md)。CLI 命令与夹具说明见 [完整对局回放](../../tests/Fixtures/P4Match/README.zh-CN.md) 和 [中心随机移动回放](../../tests/Fixtures/P4CenterMovement/README.zh-CN.md)。

保留本编号和路径用于解析历史引用；新增引用应直接指向相应决策。此次仅整理文档，不修改规则、代码或协议版本。

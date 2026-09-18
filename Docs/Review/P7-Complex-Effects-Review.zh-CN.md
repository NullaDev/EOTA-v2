# P7 复杂效果验收

> 状态：开发协议 V0 下的 P7 已完成，2026-09-09。规则取舍仍按各 ADR 的 Proposed / Accepted 状态管理；不表示首个公开协议已经冻结。

## 实现范围

- 生命周期：Kill、Return、Transform、Banish、Replace 统一竞争；最终状态 Tombstone、冻结来源和同帧离场观察者。随从与场地均覆盖生命周期组合与排列测试。
- 续执行：Sequence、并行分支汇合、有界 Loop、显式调度游标与回执结果；后续步骤引用实际 affected / created / removed 身份和标量。
- 槽位与区域：召唤与移动竞争；同批回手 > 生成 > 抽牌；筛选抽牌按稳定键分配牌库身份，容量竞争仅在需要时使用 RNG。系统回合抽牌也走统一手牌意图。
- 职业机制：以太活化、共鸣、清空与防衰减；部署后冻结充能来源，战斗前与回合结束整批判断，蓄能不消耗；手牌／牌库实例身材、费用及关键词修改。
- 持续效果：有限持续属性与关键词、临时基础攻击和充能需求、实体附加效果、玩家观察者、到期移除。失去生命与伤害分开建模。
- 战斗边界：入场阶段、战斗阶段、随从对撞，以及与随从战斗伤害同帧提交的额外英雄伤害；最终帧同归于尽仍能产生该帧已具备资格的贡献。
- 恢复与投影：私有完整检查点恢复调度器、活跃回执及临时状态；客户端只接收对应 audience 的投影，双方私有牌区与内核执行图不会混入公开 DTO。

## 规则记录与能力覆盖

独立决策拆为 [ADR-019–026](../ADR/README.md)，编写说明见 [Mod JSON 指南](../CardJsonDesignGuide.zh-CN.md)。此前两项未获用户明确答复的选择按开发提案实施：

1. [ADR-020](../ADR/020-hand-allocation-windows.md)：以同批就绪请求为手牌容量窗口。后续因果帧不撤销已经提交的抽牌或生成结果。
2. [ADR-024](../ADR/024-temporary-modifier-layers.md)：基础攻击与增益分层；临时基础攻击为 4、永久攻击增益为 +1 时，最终攻击为 5。不同帧临时基础值按后加入层优先，到期恢复仍有效的前层。

这两项不是用户已确认的规则；依据 ADR 开发期约定，它们不阻塞 V0 后续实现，公开冻结前统一复核。

[CardTable 能力映射](P7-CardTable-Coverage.zh-CN.md) 列出 132 个 ID 对应的通用 IR / Intent 路径。P7 验证引擎表达能力；132 张卡的 JSON、数值、文本和逐卡验收属于 P8，不计入 P7 验收范围。

后续口径：用户已明确这 132 张卡也是原型期实验设计，不能据此声称平衡性达标；“正式录入”仅指 VNext 管线。P8 当前实现与工具见 [P8 验收](P8-Content-Review.zh-CN.md)及[开发工具与内容制作](../../tools/README.zh-CN.md)，卡图重新按 emoji 生成。

## 验证结果

P7 完成时全量自动测试 **259 项通过**：Kernel 120、Compiler 90、Server Integration 43、Architecture 5、Algebra 1。验收包含生命周期组合与排列、容量与 RNG、续执行、临时层恢复、冻结观察者、效果预算、双传输客户端及私有信息投影。其后 P0–P6 复查增加的测试与最终总数见 [复查报告](P0-P6-Code-Review.zh-CN.md)。

- Release 构建无警告、无错误；dotnet format --verify-no-changes 通过。
- InitialReplay、P4Match、P4CenterMovement、P6Effects、P6Set、P7Complex 共 **6 份 golden replay** 在独立 CLI 进程验证通过；开发版基线已更新。
- [P7Complex](../../tests/Fixtures/P7Complex/README.zh-CN.md) 含 15 个组合测试原型，执行 **76 帧**，最终进入第 4 回合 Planning，最终状态哈希为 `2563291e3cac9d5a078e0ac19fd848bdb8a902f4f5e07d03149efaa1a04d8589`。
- 从该夹具的初始状态、每条命令和每帧边界导出 **97 个私有检查点**，由另一个进程逐个恢复并执行剩余命令和帧；状态、回执、事件及 RNG 裁决全部一致。导出与验证命令见夹具说明。

检查点验证覆盖本夹具全部合法边界，并有单独序列化测试；它不是对所有可能对局状态的穷举证明。检查点文件含秘密状态，只用于私有恢复。

## 当前开发版本

| 合同 | 版本 |
|---|---|
| Protocol / Transport | 0，首次公开冻结前 |
| Compiled protocol canonical | 3 |
| Rule content canonical | 5 |
| Match state canonical | 7 |
| Receipt / Event schema | 3 / 5 |
| RNG call schema / Effect language | 4 / 3 |
| Draw allocation | sourceEffectOrdinal，策略 2 |

本阶段没有新增需人工操作 Godot 的验收项。完整界面体验留在 P9；生产服务器的检查点存储、重启恢复和联机加固留在 P10。


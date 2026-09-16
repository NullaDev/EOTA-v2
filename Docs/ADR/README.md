# Architecture Decision Records

项目处于内部测试期，协议尚未定稿，也没有需要保留的真实对战记录。文档和实现只维护当前规则；修改格式时同步更新源内容、测试夹具和生成产物，不保留旧格式解析、字段别名或自动迁移逻辑。规则、状态与回放仍使用规范编码及哈希校验当前数据的一致性。

每份 ADR 围绕一个可独立评估、修改或替代的决策组织，记录背景、决定及影响。阶段完成度、当前版本号和测试结果放在 `Docs/Review/`，CLI 用法放在对应工具或夹具说明中。已拆分的 ADR 保留编号、路径和替代关系，新引用直接指向承接决策。

| ADR | 决定 | 当前状态 |
|---|---|---|
| [001](001-kernel-boundary.md) | Kernel 纯度与依赖方向 | Accepted |
| [002](002-stable-identities.md) | 稳定身份与分配顺序 | Accepted |
| [003](003-authoritative-numbers.md) | 权威数值语义 | Accepted |
| [004](004-canonical-encoding.md) | Canonical Encoding V1 | Accepted |
| [005](005-global-rule-rng.md) | GlobalRuleRng V1 | Accepted |
| [006](006-effect-frame-semantics.md) | Parallel、Sequence 与单帧语义 | Accepted |
| [007](007-lifecycle-and-tombstones.md) | 生命周期、离场和 Tombstone | Accepted |
| [008](008-transport-and-projections.md) | Transport Contract 与观察者投影 | Accepted |
| [009](009-match-bootstrap-and-mulligan.md) | 开局 Bootstrap 与起手换牌 | Accepted |
| [010](010-p4-system-gameplay.md) | P4 系统玩法汇总（已拆分至 011–015） | Superseded |
| [011](011-automatic-movement.md) | 自动移动的方向选择与槽位竞争 | Accepted |
| [012](012-deployment-replacement.md) | 随从与场地的部署替换权限 | Accepted |
| [013](013-combat-eligibility-and-slow.md) | 主动攻击、防守与迟缓计数 | Accepted |
| [014](014-combat-damage-frames.md) | 战斗宣告、先攻与最终伤害帧 | Accepted |
| [015](015-turn-progression-and-cleanup.md) | 九阶段回合与清理后的新回合 | Accepted |
| [016](016-numeric-effect-reduction.md) | 数值效果归约与 Set 冲突 | Proposed |
| [017](017-keyword-updates.md) | 关键词更新与迟缓获得边界 | Proposed |
| [018](018-basic-trigger-scheduling.md) | 基础触发器的结算边界 | Proposed |
| [019](019-continuations-and-checkpoints.md) | 续执行与私有检查点 | Proposed |
| [020](020-hand-allocation-windows.md) | 手牌窗口与牌库身份分配 | Proposed |
| [021](021-extended-lifecycle-conflicts.md) | 效果替换与生命周期竞争 | Proposed |
| [022](022-summon-and-movement-conflicts.md) | 召唤与移动的槽位竞争 | Proposed |
| [023](023-charge-batching.md) | 充能批处理与蓄能 | Proposed |
| [024](024-temporary-modifier-layers.md) | 持续修正与基础攻击分层 | Proposed |
| [025](025-attached-effects-and-observers.md) | 附加效果与玩家观察者 | Proposed |
| [026](026-combat-effect-boundaries.md) | 战斗阶段、对撞与同帧额外伤害 | Proposed |
| [027](027-lane-statuses.md) | 整路冻结／锁闭、进出边界与持续时间 | Proposed |
| [028](028-field-durability-and-deck-rule-scope.md) | 场地强度与耐久统一、按对局卡牌校验规则 | Proposed |
| [029](029-configurable-decks-and-fatigue.md) | 可配置构筑与独立玩家疲劳 | Proposed |

`Proposed` 表示可用于开发协议 V0，但需要在首个公开协议冻结前由人工复核。`Accepted` 表示已经成为协议合同。`Superseded` 表示已被其他记录替代，应沿替代关系查阅当前决策。

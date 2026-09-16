# ADR-027：整路冻结与锁闭

- 状态：Proposed（开发协议 V0 使用）
- 日期：2026-09-10
- 承接：ADR-006、ADR-011、ADR-012、ADR-013、ADR-021、ADR-022

## 语义

冻结与锁闭保存在 `LaneState.Statuses`，没有所属玩家，影响该路双方。冻结取消双方主动攻击资格，并在战斗宣告后再次校验；场地 `preventsActiveAttacksInLane` 与显式 Frozen 状态取并集。因此移除显式 Frozen 不会解除仍在场的暴风雪。冻结不阻止效果伤害、施法或移动。

锁闭禁止移动进入与离开，并禁止部署、召唤、返回手牌及替换。规划阶段拒绝向已锁闭的路部署；执行阶段仍读取共同快照复核。法术可以选择锁闭的路；死亡、放逐、场地到期仍正常移除实体，变形保留原槽位，允许发生。这些生命周期细节是本次 V0 开发提案，尚未成为公开冻结规则。

自动移动先排除锁闭的来源／目标，再执行既有方向与竞争规则。锁闭不能使非法候选消耗规则 RNG。

## 帧与持续时间

`ChangeLaneStatusIntent` 与 `DecayLaneStatusesIntent` 归入 `ConflictKind.LaneStatus + LaneId`。每份 Intent 有回执；最终集合变化产生携带 LaneId 的公开 `LaneStatusChanged` 事件。

同状态的多次添加取最大剩余时长，永久覆盖有限时长；同帧移除优先于添加。省略 duration 表示永久，正数在回合 Cleanup 按 `EffectDurationDecayPerTurn` 衰减。清理只衰减旧快照的时长，然后合并本帧新添加。无效 Intent 以 `invalid-lane-status` 拒绝；表达式得到非正时长沿用 `empty-duration` NoOp。

移动、召唤与部署读取帧开始时的路状态。同帧加锁不撤销该帧原本合法的移动；同帧解锁不使召唤立即合法。需要先解锁再进入时使用 Sequence 的后续帧。

`laneStatus` 支持 Lane 选择器，仍按既有 scope（默认本路）选路。选择器的阵营只用于定位，最终状态施加在整路。重复命中同一条路合并成一份状态 Intent。Sequence 的 affected 输出为来源方对应的 Lane 目标，后续仍按整路解释。

## 版本与验收

Match state canonical 从 7 升为 8，Effect language 从 3 升为 4；Rule content canonical 保持 5，Receipt/Event 保持 3/5，Transport 保持开发 V0。无路状态内容的规则字节不变。旧七份回放按新协议／状态编码重建，逐帧回执、事件、RNG、命令与胜负结果均与重建前一致。

验收包含双方禁攻、战斗后校验、移动进出、部署二次校验、生命周期例外、排列一致性、持续时间清理、Sequence／Parallel 差异、公开投影、私有检查点恢复及 [P9 固定回放](../../tests/Fixtures/P9LaneStatuses/README.zh-CN.md)。

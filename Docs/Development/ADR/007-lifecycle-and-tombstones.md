# ADR-007：生命周期、离场和 Tombstone

- 状态：Accepted
- 日期：2026-09-04
- 接受日期：2026-09-06

## 冲突与事实

Transform、Return、Kill 和 Banish 进入同一实体生命周期冲突组。V1 默认优先级为 Banish > Kill > Return > Transform。

死亡、离场、放逐和变形是四种不同的规则事实：

| 操作 | 死亡 | 离场 | 专用事实 |
|---|---:|---:|---|
| 随从因生命条件或 Kill 死亡 | 是 | 是 | `EntityDied` + `EntityLeft` |
| 有限场地能量耗尽或被 Destroy | 是 | 是 | `EntityDied` + `EntityLeft` |
| Return 回手 | 否 | 是 | `EntityLeft`；成功时另建全新手牌实例 |
| Replace 替换旧实体 | 否 | 是 | 旧实体 `EntityLeft`，新实体入场 |
| Banish 放逐 | 否 | 否 | 仅 `EntityBanished`，不触发死亡时或离场时 |
| Transform 变形 | 否 | 否 | 原位 `EntityTransformed`，保留实体身份 |

因此规则文本分别提供“死亡时”和“离场时”扳机；死亡会同时满足两者，普通离场只满足后者。两类扳机不能再互为别名。

随从与场地在规划部署时能否替换已有对象，由 [ADR-012](012-deployment-replacement.md) 定义；本记录定义替换成功后的生命周期事实与观察窗口。

## Tombstone 与观察窗口

- Death、Return、Replace 和 Banish 会从战场删除实体并产生 Tombstone；Transform 原位更新，不产生 Tombstone。
- Tombstone 使用同帧全部数值归约完成、实体实际删除前的最终值。
- 放逐虽然不产生可触发的死亡/离场事实，仍保留 Tombstone 和 `EntityBanished` 供回执、回放与诊断使用。
- 死亡/离场观察者集合从提交前 FrameSnapshot 冻结；同批被删除的观察者仍可观察其他实体的死亡或离场。
- 后续工作项从 Frozen Source 读取来源属性，不回查当前战场。

随从当前生命或最大生命小于等于 0 时进入死亡处理；有限场地能量小于等于 0 时进入死亡处理。永久场地不参与能量耗尽检查，但仍可被显式 Destroy。

## 帧末生命周期检查点

“立即死亡/破坏”不表示在遍历某个 Intent 时马上删除对象。实现顺序固定为：

1. 同一原子帧的全部并列数值与生命周期 Intent 读取共同快照并统一提交。
2. 在提交结果上同时计算 `IsDead` / `IsDestroyed` 标记；显式 Kill/Destroy 的标记不会被同帧治疗清除。
3. 若双方英雄都死亡则比赛平局；只死亡一方则另一方获胜。比赛一旦结束，丢弃全部未执行 Continuation 和新触发。
4. 冻结观察者与 Tombstone，然后在同一个生命周期检查点原子移除全部已标记实体。
5. 仅当比赛仍在进行时，死亡与离场事实才为后续帧安排触发。

因此已标记对象不会存活到下一个普通效果帧，也不会成为新目标；但同帧所有并列操作仍能看到一致的帧开始快照。

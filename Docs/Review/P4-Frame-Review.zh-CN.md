# P4 单帧结算人工复核检查点

> 首次检查点：2026-09-06  
> 复核完成：2026-09-07  
> 状态：已复核；确认结果已实现并写入 ADR-003 与 ADR-006。  
> 范围：共同帧快照、Intent 冲突组、数值归约、生命周期检查点、终局短路、回执/事件与哈希。

> 后续进度（2026-09-07）：基础玩法、九阶段回合和完整对局逐帧回放已通过自动验收，见 [P4-Gameplay-Review.zh-CN.md](P4-Gameplay-Review.zh-CN.md)。本文保留单帧切片的历史检查点；下面的“特意未做”描述的是该切片当时的范围。

## 已完成

- `FrameSnapshot → WorkItem → AtomicIntent → ConflictGroup → Reducer → Commit` 单帧管线。
- 所有 WorkItem 读取同一个帧开始快照；传入枚举顺序不影响状态、回执或事件哈希。
- 英雄伤害/治疗、随从身材/伤害/治疗/Kill、有限场地能量与显式 Destroy 的第一组正式 Intent 和 Reducer。
- 所有 Intent 恰有一份 `Applied`、`NoOp`、`Rejected` 或 `Error` 回执。
- 帧内使用 `BigInteger` 聚合并校验回 `Int64`；任一权威数值溢出会使整帧失败，不提交部分目标。
- 最大生命变化先同步改变当前生命，之后统一应用同帧伤害和治疗；显式 Kill 不会被治疗清除。
- 生命周期检查点在本帧归约后统一判断死亡/破坏、生成 Tombstone、释放槽位并移出战场。
- 随从死亡产生 `EntityDied` 与 `EntityLeft`；永久场地拒绝能量修改，但可被显式破坏。
- 同帧双方英雄死亡为平局；单方死亡立即确定胜负。终局后的剩余帧会被 Runner 丢弃，直接再次调用单步或帧归约入口也会被拒绝。
- Canonical State Version 已升至 3，初始 golden replay 已随状态结构更新。

这里的 `IsDead` / `IsDestroyed` 是提交过程中的生命周期标记：不会在遍历单个 Intent 时删除对象，也不会拖到下一个普通效果帧。它允许同帧并列操作读取一致快照，同时保证 Continuation 和新触发开始前实体已经原子移除。

## 特意未做

- 尚未实现部署、槽位竞争、入场/替换、移动、战斗、回合清理与阶段驱动器。
- 尚未建立 Continuation/触发队列；因此当前只有事件事实，没有根据事件派生新的 WorkItem。
- Return、Replace、Banish、Transform 的完整生命周期冲突仍按计划放在 P7。
- Godot、Server 和 Transport 不参与本切片。

## 已确认的玩法语义

### 1. 随从治疗是否受最大生命限制

已确认：受限制。先得到同帧归约后的最大生命与伤害后生命，再把治疗结果限制到该最大生命；回执、事件和吸血等后续规则使用实际恢复量，而不是请求量。`full heal` 也只是恢复到最大生命。

### 2. 英雄治疗上限

已确认并补充：英雄具有独立、可变化的 `HeroMaximumHealth`，初始值来自协议的 `InitialHeroHealth`。最大生命变化会像随从一样同步改变当前生命，治疗受同帧归约后的当前最大生命限制。

### 3. 致命伤害与吸血的先后关系

已确认：所有路线的最终战斗伤害和由英雄伤害产生的吸血治疗属于同一个战斗原子帧。先求出全部贡献，再统一归约生命并检查死亡；吸血因此可以把英雄从本帧原本致命的伤害中救回。只有统一归约后双方生命都小于等于 0 才判平局。

## 验收

```powershell
dotnet build Eota.sln -c Release --no-restore
dotnet test Eota.sln -c Release --no-build --no-restore
dotnet format Eota.sln --verify-no-changes --no-restore
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify tests/Fixtures/InitialReplay/Cards tests/Fixtures/InitialReplay/protocol-v0.json tests/Fixtures/InitialReplay/initial.replay.json
```

首次检查点生成时，Release 构建为 0 warning/0 error，全部 49 项测试通过；格式检查与初始回放核验通过。复核后的实现与后续 P4 验收记录见 [P4 玩法检查点](P4-Gameplay-Review.zh-CN.md)。

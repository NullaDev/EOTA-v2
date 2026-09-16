# P3 人工复核检查点

> 日期：2026-09-06  
> 状态：已复核；P4 可以开始。  
> 范围：区域、起手换牌、规划/取消/提交命令、费用预留、revision 与命令日志。

## 已完成

- `Deck`、`Hand`、`Planning`、`Battlefield`、`Discard`、`Removed` 区域类型及卡牌区域一致性。
- 固定路线和双方独立的随从/场地槽位模型。
- `SubmitMulligan`、`PlanCard`、`PlanSpell`、`CancelPlan`、`SubmitTurn`。
- 按当前权威费用计算 `max(0, cost)`，规划时预留，取消时准确返还；负费用不会回费。
- 每位玩家独立的 command revision 与 `PlanCommandId(PlayerId, ordinal)`；对方命令不会使己方命令过期。
- 全局 MatchRevision、接受/拒绝回执、提交锁定和权威命令日志。
- 双方提交后进入 `ReadyToResolve` 输入边界；尚未开始九阶段结算。
- 命令日志能够从同一初始状态重建相同的完整 Planning State。
- 换牌选择在双方提交后按 PlayerId 统一执行，网络到达顺序不改变牌序或 RNG。
- 有限/永久场地生命周期接口和权威数值派生规则。

## 特意未做

- Godot、Server、Transport DTO 和网络幂等由 P5 开始实现。
- 战场实体、部署提交、战斗、抽牌和回合推进属于 P4。
- Intent、Reducer、Receipt Ledger 和效果语言属于 P4/P6。
- Return/Replace/Banish/Transform 的执行器属于 P7；本阶段只冻结语义。

## P4 前玩法边界复核结果

1. 已确认：同一原子帧结算后双方英雄都小于等于 0 时平局；只死亡一方则立即结束比赛，并丢弃未执行的后续帧与触发。
2. 已确认：最大生命实际增加/减少多少，当前生命同步增加/减少多少，之后再统一应用同帧伤害和治疗。当前生命 2、最大生命大于 2 的随从受到 `-2/-2` 仍会因当前生命降至 0 而死亡。
3. 已确认实现为帧末生命周期检查点：先统一提交本帧操作，再同时标记 `IsDead` / `IsDestroyed`，随后在任何 Continuation 或新触发前原子移除。标记不会拖延到下一个普通效果帧。

对应正式语义已写入 ADR-003 与 ADR-007。

## 验收

```powershell
dotnet build Eota.sln -c Release --no-restore
dotnet test Eota.sln -c Release --no-restore
dotnet format Eota.sln --verify-no-changes --no-restore
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify tests/Fixtures/InitialReplay/Cards tests/Fixtures/InitialReplay/protocol-v0.json tests/Fixtures/InitialReplay/initial.replay.json
```

检查点生成时，Release 构建为 0 warning/0 error，全部 37 项测试通过，格式检查与初始回放核验通过。

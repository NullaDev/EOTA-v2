# P4 奇数中心随机移动回放

此夹具复用 `../P4Match/Cards` 与两副牌组，使用五条路线、`movementConflictPolicy: allFail`、`movementDirectionPreference: outwardFirst`。

玩家一在正中心第 2 路部署追猎随从，玩家二在第 1、3 路部署随从。移动开始时玩家一的左右方向都合法，通过 GlobalRuleRng 选择第 1 路；该次选择出现在第 7 帧，样本计数由 8 增至 9。夹具在第 20 帧结束本轮并进入第二回合 Planning。

在仓库根目录核验：

```powershell
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify-match tests/Fixtures/P4Match/Cards tests/Fixtures/P4CenterMovement/protocol-v0.json tests/Fixtures/P4CenterMovement/match.replay.json
```

规则或编码变化需要重建基准时：

```powershell
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- record-match tests/Fixtures/P4Match/Cards tests/Fixtures/P4CenterMovement/protocol-v0.json tests/Fixtures/P4Match/deck-one.json tests/Fixtures/P4Match/deck-two.json 123456789 tests/Fixtures/P4CenterMovement/commands.json tests/Fixtures/P4CenterMovement/match.replay.json
```

`expectedFrames[].movementRandomChoices` 保存来源 EntityId、IntentId、左右候选、选定路线及样本计数。核验会同时比较这些记录及全部状态/回执/事件哈希，不能仅靠最终位置恰好相同通过。

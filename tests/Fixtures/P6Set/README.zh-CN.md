# P6 Set 冲突回放

三张测试随从同时入场，分别 Set 敌方英雄最大生命为 40、己方为 20、己方为 32。双方各部署一组，因此每个英雄都收到一份敌方、两份己方 Set。

第 4 帧先按目标控制方筛掉敌方候选，再按稳定 IntentId 从两份己方候选中抽样；双方共消费两个全局 RNG 样本。最终完成 21 帧并进入第 2 回合。numericSetChoices 固定冲突类型、目标、属性、候选、所选 IntentId 和样本计数，即使最终状态相同，篡改随机轨迹也会核验失败。

```powershell
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify-match tests/Fixtures/P6Set/Cards tests/Fixtures/P6Set/protocol-v0.json tests/Fixtures/P6Set/match.replay.json
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- record-match tests/Fixtures/P6Set/Cards tests/Fixtures/P6Set/protocol-v0.json tests/Fixtures/P6Set/deck-one.json tests/Fixtures/P6Set/deck-two.json 123456789 tests/Fixtures/P6Set/commands.json tests/Fixtures/P6Set/match.replay.json
```

P6AcceptanceTests 在 Kernel、进程内双客户端、真实 WebSocket 双客户端和观战者路径核对该基准。Set 候选和采样细节只供服务端诊断，不进入客户端视图。

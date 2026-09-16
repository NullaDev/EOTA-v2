# P9 整路状态回放

四张独立测试卡验证整路冻结、锁闭和先解锁再召唤，不加入 132 张实验卡目录。

seed 123456789，12 条命令，61 帧，结束于第 4 回合 Planning。第一回合双方部署同路随从，冻结该路并锁闭相邻路；第二回合对相邻路施加冻结，再由慢速法术通过 Sequence 解锁并召唤；第三回合验证状态到期后的结算。

最终状态哈希：`830eeae57659adf3bd02456d2101f2895dee336773934d71f10ab25770296443`。

```powershell
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify-match tests/Fixtures/P9LaneStatuses/Cards tests/Fixtures/P9LaneStatuses/protocol-v0.json tests/Fixtures/P9LaneStatuses/match.replay.json
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- record-checkpoints tests/Fixtures/P9LaneStatuses/Cards tests/Fixtures/P9LaneStatuses/protocol-v0.json tests/Fixtures/P9LaneStatuses/match.replay.json artifacts/P9Checkpoints
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify-checkpoints tests/Fixtures/P9LaneStatuses/Cards tests/Fixtures/P9LaneStatuses/protocol-v0.json tests/Fixtures/P9LaneStatuses/match.replay.json artifacts/P9Checkpoints
```

全部 74 个检查点已在独立进程恢复，剩余命令、逐帧状态／回执／事件／RNG 一致。语义见 [ADR-027](../../../Docs/ADR/027-lane-statuses.md)。

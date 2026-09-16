# P4 完整对局 golden replay

此夹具使用手工编写的 V2 无效果测试卡，覆盖部署、追猎、快速/慢速空法术、slow 2 消耗、守备反击、有限场地自然离场、费用/新回合与终局停止。所有输入从正式内容编译器和 Kernel 命令入口进入。

玩家一的迅捷/追猎/吸血随从从第 0 路追到第 1 路；前两回合绕过敌方迟缓随从攻击英雄，第三回合击杀该随从，第四回合击败英雄。场地在第一次清理时离场。对局固定为 73 帧、4 回合，玩家一获胜。

在仓库根目录运行：

```powershell
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify-match tests/Fixtures/P4Match/Cards tests/Fixtures/P4Match/protocol-v0.json tests/Fixtures/P4Match/match.replay.json
```

规则或编码变动需要重建基准时运行：

```powershell
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- record-match tests/Fixtures/P4Match/Cards tests/Fixtures/P4Match/protocol-v0.json tests/Fixtures/P4Match/deck-one.json tests/Fixtures/P4Match/deck-two.json 123456789 tests/Fixtures/P4Match/commands.json tests/Fixtures/P4Match/match.replay.json
```

`commands.json` 使用 0/1 表示玩家一/二，路线从 0 编号。命令的 kind、playerId、expectedPlayerRevision 为必填；PlanCard 还需 cardInstanceId、laneId，PlanSpell 可省略全局目标的 laneId。每次双方提交后自动运行到下一次规划或终局，再读取下一条命令。

FormatVersion 2 在 initial 中固定开局 manifest、种子、牌组与哈希，expectedFrames 固定每帧前后状态哈希、回执哈希、事件哈希、回合/阶段与 RNG 消费计数。核验失败会返回首个差异帧及预期/实际摘要。`P4ReplayFixtureTests` 同时验证原始命令文件与回放中的命令可得到相同结果。

# P8 实验内容验收夹具

夹具与当前协议和内容同步，逐帧预期结果及最终哈希见 `match.replay.json`。

直接编译 Content/Source/Cards 中的全部 282 张卡，不另复制测试版卡牌。

scenarios.json 包含 313 个明确的效果和边界场景，每个带独立的环境安排与可观察结果断言；全部有主动效果的卡都至少有一个场景。它们隔离效果根，验证具体数值、身份、关键词或附加效果结果。另有 282 个完整规划／三个回合测试，验证全部卡能通过正式入口运行；静态卡通过此路径及基线数据核对验收。

综合回放使用 282 卡内容包、双方各一份全卡牌测试牌组、seed 179、大起手和高费用，刻意展示机制而非正常比赛节奏。双方各留一张机械在牌库。三个回合展示临时充能、有期限的毒伤、死亡生成、回手后召唤、筛选抽牌修改、以太循环和玩家死亡观察者；70 帧后进入第 4 回合 Planning。

```powershell
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify-match Content/Source/Cards tests/Fixtures/P8Content/protocol-v0.json tests/Fixtures/P8Content/match.replay.json
```

修改内容规则后，先修订预期场景并核对行为，再按开发版策略重录：

```powershell
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- record-match Content/Source/Cards tests/Fixtures/P8Content/protocol-v0.json tests/Fixtures/P8Content/deck-one.json tests/Fixtures/P8Content/deck-two.json 179 tests/Fixtures/P8Content/commands.json tests/Fixtures/P8Content/match.replay.json
```

私有检查点可以沿用 Replay CLI 的 record-checkpoints / verify-checkpoints 命令。卡图、本地化文本变化不会改变本回放的权威规则哈希。

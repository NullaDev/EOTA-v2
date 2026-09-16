# P7 综合效果回放

本夹具为手工编写的组合测试内容，费用、身材和生成数量用于制造冲突，不是 P8 的正式平衡卡表。15 个测试原型覆盖机械筛选、回手再召唤、生成溢出、蓄能与整批充能、临时基础攻击和关键词、每回合入场阶段、毒雾附加失去生命、冻结来源生成、清空以太与有界循环、玩家死亡观察者，以及放逐压过击杀/回手。

固定 seed=123456789。双方保留一张机械牌在牌库，以验证筛选抽牌修改的是实际身份；其余大起手用于同时规划多种独立机制。第 1 回合部署、生成溢出和毒雾离场；第 2 回合换防、图纸分析、循环伤害及生命周期竞争；第 3 回合继续观察持续状态到期，最终进入第 4 回合 Planning。

```powershell
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify-match tests/Fixtures/P7Complex/Cards tests/Fixtures/P7Complex/protocol-v0.json tests/Fixtures/P7Complex/match.replay.json
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- record-checkpoints tests/Fixtures/P7Complex/Cards tests/Fixtures/P7Complex/protocol-v0.json tests/Fixtures/P7Complex/match.replay.json artifacts/P7Checkpoints
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify-checkpoints tests/Fixtures/P7Complex/Cards tests/Fixtures/P7Complex/protocol-v0.json tests/Fixtures/P7Complex/match.replay.json artifacts/P7Checkpoints
```

最后两个命令分别在独立进程中导出和验证检查点。导出的 JSON 含双方秘密状态，只用于本地私有恢复，不是供玩家观看的 replay。验证会从每个命令及帧边界恢复，接着执行剩余回合和命令，核对状态、回执、事件和每次随机裁决。

重录开发版基线：

```powershell
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- record-match tests/Fixtures/P7Complex/Cards tests/Fixtures/P7Complex/protocol-v0.json tests/Fixtures/P7Complex/deck-one.json tests/Fixtures/P7Complex/deck-two.json 123456789 tests/Fixtures/P7Complex/commands.json tests/Fixtures/P7Complex/match.replay.json
```

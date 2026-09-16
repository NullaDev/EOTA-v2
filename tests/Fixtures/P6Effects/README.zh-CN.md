# P6 六张代表卡回放

本夹具使用手工编写的 V2 测试内容，覆盖伤害、治疗、资源和基础触发等规则能力。测试 ID 与正式卡 ID 分离；当前卡牌定义见 [卡表](../../../Docs/CardTable.zh-CN.md)。

| 测试卡 | CardTable 对应 | 验证 |
|---|---|---|
| TEST-P6-GOBLIN | 哥布林雇佣兵 | 1 费 1/2，无效果 |
| TEST-P6-HUNTER | 赏金猎人 | 2 费 2/1，入场对本路敌方随从造成 1 |
| TEST-P6-HEALER | 流浪医师 | 3 费 2/3，入场恢复己方英雄 3 |
| TEST-P6-SHIELD | 举起盾牌！ | 1 费快速，己方本路随从 +1 最大生命，具有 guard 时再 +1 |
| TEST-P6-SHOT | 迅捷射击 | 1 费快速，对本路敌方随从造成 1 |
| TEST-P6-SPARK | 奥术火花 | 0 费慢速，本路敌方随从 1 伤害，共鸣 1 时改为 2 |

双方各六张牌全部在起手。第 1 回合同时部署三个随从，猎人互相入场击杀；第 2 回合同时使用三张法术，快速增益和伤害在同一帧，战斗后火花击杀哥布林；第 3 回合医师互相战斗离场。最终进入第 4 回合 Planning，共 61 帧。其他分支由 EffectRuntimeTests 覆盖。

```powershell
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify-match tests/Fixtures/P6Effects/Cards tests/Fixtures/P6Effects/protocol-v0.json tests/Fixtures/P6Effects/match.replay.json
dotnet run --project src/Eota.Server.Host -c Release --no-build -- --fixture tests/Fixtures/P6Effects
```

重新生成开发基准：

```powershell
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- record-match tests/Fixtures/P6Effects/Cards tests/Fixtures/P6Effects/protocol-v0.json tests/Fixtures/P6Effects/deck-one.json tests/Fixtures/P6Effects/deck-two.json 123456789 tests/Fixtures/P6Effects/commands.json tests/Fixtures/P6Effects/match.replay.json
```

commands.json 的玩家编号为 0/1，路线为 0..5。回放同时固定每帧状态、回执、事件和随机调用轨迹；测试通过真实 Server/Client 路径重放相同命令。

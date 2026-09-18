# Mod JSON 教学示例

对应 [完整编写指南](../../CardJsonDesignGuide.zh-CN.md)。这些源文件与内置卡池隔离，不会自动进入游戏。卡图借用游戏已有资源，数值仅供测试。

教学卡已随本轮卡池收紧费用与持续收益：哨兵 3 费、无人机 2 费、调度员 5 费；工坊为 4 费、耐久 2、蓄能 1。

| 文件 | 主要写法 |
| --- | --- |
| [教学哨兵](Cards/MOD-DEMO-SENTINEL.json) | 最小随从、关键词、入场伤害，与指南首个整卡示例一致 |
| [教学无人机](Cards/MOD-DEMO-DRONE.json) | token 来源、mechanical 标签、迅捷，被工坊和调度员引用 |
| [教学工坊](Cards/MOD-DEMO-WORKSHOP.json) | 有限场地耐久、蓄能、结束充能、生成手牌 |
| [恒定回路](Cards/MOD-DEMO-PERMANENT.json) | 永久场地不填耐久、保护本方以太 |
| [机械调试](Cards/MOD-DEMO-REPAIR.json) | 全局法术、筛选抽牌、sequence 输出、强化刚抽到的卡 |
| [寒霜符文](Cards/MOD-DEMO-FROST.json) | 路线法术、同帧冻结整路与增加本方以太 |
| [无人机调度员](Cards/MOD-DEMO-DEPLOY.json) | 入场时向相邻路线召唤衍生随从 |
| [以太回响](Cards/MOD-DEMO-ECHO.json) | clearEther → previous.scalar → loop 逐次伤害 |
| [短期教官](Cards/MOD-DEMO-WATCHER.json) | 全场友方入场观察、排除自己、临时 grantEffect |

[zh-CN.json](zh-CN.json) 是名称和说明字典；它不应放进 Cards 文件夹交给单卡编译器。

在完整源码仓库根目录运行：

```powershell
dotnet test tests/Eota.Server.IntegrationTests -c Release --filter FullyQualifiedName~ModAuthoringDocumentationTests
```

验证会编译指南中 30 处 JSON 和上述 9 张卡，核对文本、资源、协议、卡组及实际结算，并检查并列／顺序快照、英雄斩杀与满手回手死亡后的逐帧恢复，生成 `artifacts/ModAuthoringExamples/demo.eotapack.json`。在游戏“卡牌编辑器 → 内容包管理 → 导入并合并卡牌”中选择该文件，再启用。导出文件包含这 9 张卡；指南中的能力对象可单独复制到编辑器练习。导入合并保留已有内置卡。

逐张通过编辑器试写时，先保存教学无人机，再保存依赖它的工坊和调度员。若更换 Mod 前缀，应一起改卡牌 id、prototype 引用、本地化键、文本字典和牌组中的 id。

[protocol.json](protocol.json) 是开发验证用完整协议：8 张、每种 1 张、不限职业的基础卡、起手 8 张、100 点初始费用、递增疲劳。它让全部基础教学卡一开始便可操作，但抽牌效果此时会遭遇空牌库；要观察“抽牌后强化”，可在客户端增大牌组并减少起手，或按测试中起手 1 张、手中持有机械调试的场景验证。永久场地示例本身不产生以太，需要搭配活化效果。

试写指南中的“归乡信使”时，将教学哨兵的能力替换为该案例，并把手牌上限从 9 调为 8。这样打出信使后，生成无人机填满手牌，自身回手失败，死亡效果才会触发。

[deck.json](deck.json) 是对应 Replay CLI 牌组，包含 8 种基础教学卡；无人机作为衍生卡不直接入组。客户端请按这些规则在协议页配置，再在牌组工坊构筑；不能将此 CLI 文件直接作为客户端牌组存档。

仅验证初始对局及编译依赖还可运行已有 CLI：

```powershell
dotnet run --project tools/Eota.ReplayCli -c Release -- record Docs/Content/Examples/Cards Docs/Content/Examples/protocol.json Docs/Content/Examples/deck.json Docs/Content/Examples/deck.json 146 artifacts/ModAuthoringExamples/initial.replay.json
```

测试或 CLI 不会改写内置卡牌、当前启用包或用户牌组。生成后的包要修改规则时，请编辑源 JSON 后重新运行验证生成，或在游戏编辑器中修改并重新导出，不能只保留旧 RuleHash。

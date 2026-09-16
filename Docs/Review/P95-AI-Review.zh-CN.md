# P9.5 本地人机对战验收

> 本文记录 AI 首版。后续已将入口拆为“人机对战”与“本机双人”，并新增交接遮挡、卡组选择和动画，见 [P9.6 当前界面记录](P96-Interaction-Review.zh-CN.md)。以下旧入口描述和截图保留为历史验收依据。

2026-09-11。已实现可直接游玩的本地 AI，提供简单、普通、困难三档。入口为“开始对局”：选择玩家一牌组、玩家二／AI 牌组及 AI 难度，点击“开始人机对战”。自定义职业牌组和已保存的对战协议均可使用；玩家一始终由用户控制。

## 策略与边界

| 难度 | 起手 | 规划 |
|---|---|---|
| 简单 | 保留全部起手牌 | 按独立种子选择合法行动，适合熟悉规则 |
| 普通 | 换掉高费牌 | 估算随从攻防交换、英雄威胁、费用、场地及卡牌效果收益，逐张规划 |
| 困难 | 进一步调整曲线与低费法术重复 | 比较最多三张牌的兼容组合，束宽 8；每次得到新视图后重新决策 |

`Eota.Client.AI` 只引用 Client.Core 和 Contracts。输入为己方 ObserverView、PlanOptions 和公开卡牌规则提示；没有 MatchState、对手秘密手牌／牌序／规划或权威 RNG。Desktop 从编译后的效果 IR 提取通用提示，不解析中文卡牌描述，也不按单卡 ID 写策略分支。

独立 AI 种子和稳定候选排序保证相同可见输入得到相同决策。Godot 设置页由对局种子异或 104729 派生 AI 种子；两者在保存文件中独立记录。线程耗时不参与评分或搜索停止条件。

困难档使用启发式组合搜索，尚不完整模拟结算和后续回合。条件、变量表达式、触发与充能等复杂效果使用近似权重，不能保证正确把握所有卡牌连携；没有读取对手秘密信息来提高难度。联网 AI 房间、实时 AI 对局观战和更深入的策略留待后续。

## 会话、界面与回放

- AI 自动换牌、规划和提交；用户仍可取消自己的规划。AI 状态显示在对手英雄下方，包含准备、换牌、思考、等待、结束和失败。
- LocalSetup 和 BattleScreen 继续使用固定可编辑 `.tscn`。人机模式隐藏切换席位，提供重新开始、返回大厅、保存回放与原有动画控制。
- 后台任务逐条通过 GameClient 发送命令，等待回执，再用快照作为邮箱屏障；不会根据中途结算帧连续发送过期命令。等待另一方时使用可取消的异步状态通知。
- 每次决策最多 512 个原始候选、2048 次评分；每阶段最多 64 条命令，预留最后一条提交。连续三次拒绝停止并显示失败，可重新开始；退出／重开先取消并等待旧 AI 结束。
- 保存回放时，MatchActor 在同一次邮箱操作中捕获命令日志和最终哈希，避免 AI 恰好行动造成文件不一致。`settings.ai` 记录难度、独立种子及策略版本 `eota-ai/1`；回放只执行已接受的命令，不重新运行 AI。旧版不含 AI 元数据的回放仍可读取。

已检查截图：[难度和牌组设置](../../artifacts/p95-ai-setup.png)、[困难 AI 思考中的战场](../../artifacts/p95-ai-battle-2.png)、[困难 AI 对局结果](../../artifacts/p95-ai-result-2.png)。

## 验证结果

全量 **581 项测试通过**：Kernel 137、Compiler 357、Server Integration 79、Architecture 7、Algebra 1。新增 17 项测试覆盖策略差异、秘密信息配对、合法候选、起手／全局法术、单方以太、已有规划、冻结／锁闭、三路协议、过期版本重试、连续拒绝停止、异步通知取消、保存／重开和依赖约束。结果保存在 `artifacts/P95TestResults/P95*.trx`。

自动对局通过两个普通 GameClient／AiPlayer 接入 MatchActor，使用四职业默认牌组。三组均覆盖有序的 4×4 配对，总计 **48 场全部结束，最晚第 38 回合，拒绝命令为 0**。测试上限为 100 回合和每场 45 秒；AI 不绕过规则强制判胜。测试关闭界面用的 160ms 行动间隔，整场通常耗时数百毫秒，具体耗时和命令数保留在 JSON；此数字包含客户端传输和完整结算，并非单次决策延迟。

| 对照组 | 对局种子 | 玩家一胜 | 玩家二胜 | 平局 | 结果文件 |
|---|---:|---:|---:|---:|---|
| 简单 vs 普通 | 146 | 4 | 12 | 0 | [记录](../../artifacts/P95AiAcceptance/Easy-Normal-146.json) |
| 普通 vs 简单 | 2026 | 12 | 3 | 1 | [记录](../../artifacts/P95AiAcceptance/Normal-Easy-2026.json) |
| 困难 vs 普通 | 146 | 10 | 6 | 0 | [记录](../../artifacts/P95AiAcceptance/Hard-Normal-146.json) |

因此普通对简单在本测试集为 24 胜、7 负、1 平，困难对普通为 10 胜、6 负。对手席位、种子和默认牌组分布有限，结果只作为首版策略对照，不代表普遍胜率或卡牌平衡结论。

Godot 4.6.2 Mono 实际 OpenGL 窗口执行 `-- --smoke-ai`，通过设置页分别启动三档 AI，完成三场对局，覆盖玩家换牌、取消规划、保存回放、重开和返回大厅时取消 AI。日志为 [p95-ai-smoke.log](../../artifacts/p95-ai-smoke.log)，结束于 `P95_GODOT_AI_SMOKE_OK`。困难档回放由独立 Replay CLI 验证 360 帧，最终哈希与 Godot 相同：

```text
b97a5a8a83fd3a78a66933772e28901e51ff3203977c5bb4327f886eaf7a1e14
```

原本机双人 smoke 仍通过，四回合最终哈希保持 `ff4220becdc29056b447cd6246fe62bad1ab344169fab5b137e55924715f8aea`。构建无警告／错误，Godot 验收日志无错误／警告。

## 复现

```powershell
dotnet test Eota.sln -c Release
dotnet build Eota.Godot.csproj
# 用 Godot Mono 打开工程直接游玩，或给 Godot 可执行文件传入：
# --path . -- --smoke-ai

dotnet run --project tools/Eota.ReplayCli -c Release -- record-match Content/Source/Cards artifacts/P95GodotReplay/Hard/protocol-v0.json artifacts/P95GodotReplay/Hard/deck-one.json artifacts/P95GodotReplay/Hard/deck-two.json 146 artifacts/P95GodotReplay/Hard/commands.json artifacts/P95GodotReplay/Hard/match.replay.json
dotnet run --project tools/Eota.ReplayCli -c Release -- verify-match Content/Source/Cards artifacts/P95GodotReplay/Hard/protocol-v0.json artifacts/P95GodotReplay/Hard/match.replay.json
```

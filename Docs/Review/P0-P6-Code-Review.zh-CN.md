# P0–P6 代码复查

> 已完成本轮复查，2026-09-09。先完成 P7，再检查此前的关键路径并直接修复范围较小的问题。本报告依据现有代码和可复现行为，不对代码作者或模型能力作归因。

## 范围与结论

检查了协议与内容编译、规范编码及 RNG、建局和命令接纳、帧提交与回合调度、数值归约、MatchActor 邮箱与幂等处理、客户端接收与同步、传输约束和观察者投影。结合已有架构、代数、玩法和双传输测试复核；不是对所有文件和运行状态的穷举审计。

目前没有发现需要推翻 P0–P6 架构才能继续 P8 的问题。内核依赖边界、按玩家 revision 接纳命令、规则随机源与表现隔离、冻结来源、按 audience 投影这些设计可以继续使用。已修复以下六项；另外两项需在持久化与资源加固阶段处理。

## 已修复

| 问题 | 触发与影响 | 修复与验证 |
|---|---|---|
| 客户端把无效 ACK 当成命令结果（中） | 同一请求 ID 的 ACK 来自错误比赛／合同版本，或序号重复、跳号、revision 回退。Store 拒绝更新，但 GameClient 仍返回 ACK，调用方可能认为命令成功。 | Store 区分“接受信封”和“已同步”；只有通过校验的响应可以返回结果，无效响应让请求明确失败。5 种无效信封及合法 ResyncRequired ACK 均有测试。 |
| 接收循环退出时的请求收尾竞态（低） | Reader 已遍历待完成请求、但 Task 尚未完成的间隙，新请求可能通过 IsCompleted 检查，随后无人完成它。 | 先发布 readerStopped，再完成挂起请求；请求发送入口检查该状态。测试覆盖断线时挂起请求和后续请求均失败、无需调用方自行取消。精确的线程交错依据代码审查修复，测试不是穷举调度证明。 |
| 非移动类协议策略缺少校验（中） | JSON 删除手牌／牌库策略字段后，枚举成为未定义的 0；编译仍成功，运行时使用当前实现的规则。直接构造未知枚举也有同类问题。 | 对 5 类既有策略补齐枚举校验；删除字段及传入未知值都失败。5 个测试先复现失败，修复后通过；可选移动配置仍使用既定默认值。 |
| 效果 ID 的运行时身份与编码规范不一致（中） | Unicode NFC 等价的两个 ID 在哈希中编码相同，编译器却按原始字符串区分身份和检查重复。 | 编译时先 NFC 规范化，再检查长度、去重和排序；测试核对相同哈希对应相同运行时 ID，并拒绝规范等价的重复声明。 |
| 非法牌组数量在诊断前溢出（低） | 两个副本数为 int.MaxValue 或 int.MinValue 的条目，会在累加时抛 OverflowException，未返回 InvalidDeck。 | 校验阶段以 Int64 累加，非法数量仍返回稳定诊断并不创建比赛；正负极值各有测试。 |
| RNG 审计计数意外回绕（低） | xoshiro 的 unchecked 算法块同时包住 SampleCount；到 UInt64 上限时审计计数归零。 | 只对计数递增增加 checked，保留算法规定的模运算；测试验证最后一次合法取样、计数耗尽拒绝及单候选不耗样本。 |

主要修改点：[GameClient](../../src/Eota.Client.Core/GameClient.cs)、[ObserverStore](../../src/Eota.Client.Core/ObserverStore.cs)、[GameProtocol](../../src/Eota.Kernel/Protocols/GameProtocol.cs)、[EffectCompiler](../../src/Eota.Content.Compiler/EffectCompiler.cs)、[MatchFactory](../../src/Eota.Kernel/Matches/MatchFactory.cs)、[GlobalRuleRng](../../src/Eota.Kernel/Determinism/GlobalRuleRng.cs)。

这些修复不改变现有合法 ASCII 内容与普通对局的回放结果，复查后无需重录 P7 基线。此前 P7 扩展造成的基线更新，单独记录在 P7 验收中。

## 后续问题

2026-09-13 进展：P10 已接入恢复计数／ID 不复用约束、分段日志压缩、有界命令去重和补发窗口，并完成 16 局并发基线。下文保留原复查结论；其中纯 Kernel 耗尽终止事实和参与规则哈希的完整历史压缩仍未结案，详见 [P10 当前记录](P10-Hosting-Editor-Review.zh-CN.md)。

### ID／revision 耗尽尚未统一转换成正式终止结果（低，已复现）

[FrameResolver](../../src/Eota.Kernel/Resolution/FrameResolver.cs) 的帧号／回执号／事件号分配，以及 [MatchCommandProcessor](../../src/Eota.Kernel/Commands/MatchCommandProcessor.cs) 的接纳计数，部分 checked 溢出位于正式错误转换之外。将 NextFrameId 或 NextCommandId 设为 UInt64.MaxValue 后调用正式入口，会抛 OverflowException；没有返回携带终止事实的 transition。Server 会隔离为故障比赛，但这与内核的 RuleFailure 结果路径不同。

本地复现：

```powershell
dotnet run --project artifacts/P0P6ReviewProbe -c Release
```

已实际运行上述探针，两种入口均复现，原输入状态保持 Active。普通对局无法现实地耗尽 64 位 ID，因此不阻塞 P8；P10 接入外部持久化恢复前，应明确恢复状态的计数约束，并设计不用继续分配已耗尽 ID 的终止记录路径。仅在外围 catch 异常不能补齐回放合同，所以本轮没有用这种方式掩盖问题。

### 历史保留与性能预算还需要长期对局验证（维护项，未测得性能故障）

[MatchStateHasher](../../src/Eota.Kernel/Matches/MatchStateHasher.cs) 每次编码完整 CardInstances、Tombstones、CommandLog；这些集合随对局累积。[MatchActor](../../src/Eota.Server.Application/MatchActor.cs) 的命令缓存达到默认 16384 个键后拒绝新键，尚无持久化去重窗口。当前功能测试验证短局，不能证明长期对局的内存和尾延迟满足目标。

建议 P10 先建立持续生成／离场、长命令日志、慢客户端的压力基线，再决定历史分段、检查点保留和去重窗口。删除历史会影响状态哈希、Frozen Source 与恢复语义，应作为独立协议决策处理。本轮不做未经测量的存储重构。

## 验证与阶段边界

- 新增 **16 项**回归测试，最终全量 **275 项通过**：Kernel 123、Compiler 96、Server Integration 50、Architecture 5、Algebra 1。
- Release 构建 0 警告、0 错误；格式校验通过。
- 6 份 golden replay 在独立 CLI 进程全部通过；P7 的 97 个已导出私有检查点再次恢复通过，状态、回执、事件与 RNG 裁决一致。
- 构建和测试结果保存在 artifacts/P7TestResults，最终记录前缀为 p7-final。

P7 开发验收和本轮复查已完成；下一阶段为 P8 的 132 张正式内容录入。没有新增需用户手动操作 Godot 的测试，也没有必须现在决定的架构问题。Proposed ADR 仍需在公开协议冻结前人工复核。P5 开发主机的席位选择、P9 完整 UI 和 P10 生产恢复／鉴权仍按原计划推进，不能把当前开发验收解释成已具备正式上线条件。

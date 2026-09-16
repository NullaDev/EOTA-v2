# P5 Server/Client 端到端检查点

> 日期：2026-09-07  
> 状态：Server/Client 开发骨架已完成，全量自动验收通过。  
> 前序检查点：[P4 基础玩法](P4-Gameplay-Review.zh-CN.md)。P4 的玩法确认项已关闭，通用触发调度仍按计划归入 P6/P7。

> 后续：P6 已完成，当前版本、测试数量和重建后的回放哈希见 [P6 检查点](P6-Effects-Review.zh-CN.md)。本文保留 P5 完成时的历史验收数据。

## 已实现

- Server.Application：内存 MatchActor、有界邮箱、单逻辑写者、席位命令映射、幂等确认和观察者投影。
- Server.Infrastructure：文件内容/协议/牌组加载，以及内存对局目录。
- Server.Host 与 Server.Transport.WebSocket：独立 Headless 进程、健康检查和开发 WebSocket 入口。
- Client.Core：后台接收、命令确认/拒绝、线程安全的不可变观察者快照、表现队列及快照恢复。
- Client.Transport.InProcess 与 Client.Transport.WebSocket：共享 Contract V0；进程内传输也强制 JSON 往返。
- PlayerOne、PlayerTwo、Spectator 三种投影。命令无 playerId 字段，玩家身份来自连接；只构造当前 audience 可以看到的 DTO。

传输格式、边界和启动命令见 [Contract V0 使用说明](../Transport/README.zh-CN.md) 与 [JSON Schema](../Transport/contract-v0.schema.json)。本阶段没有修改玩法、Kernel 状态编码或既有回放版本。

## 验收内容

两个 Client.Core 实例经 InProcess 或真实 WebSocket 完成 P4Match 的 4 回合、73 帧无效果对局，结局均为 PlayerOneWon。服务端完整状态哈希与 Replay CLI 基准一致：

`4667cf4b31d96c55025cd18ca2ac144111791c89b29430428de0feeccb7b3fe4`

另有测试启动独立 dotnet Host 子进程，两个远程客户端完成同一对局；测试不依赖 Godot。32 项 P5 集成测试覆盖完整对局、规划取消、换牌、三种投影、抽牌身份脱敏、隐藏牌序/RNG 与可见哈希分离、重复/过期/跨比赛命令、重连去重、有界邮箱取消、慢观察者、客户端序号/哈希失配、表现队列上限和空闲客户端持续接收。

实际 WebSocket 收发的 293 条消息已按 Contract V0 JSON Schema 校验，包含三种 audience 的初始快照、命令、确认与全部表现帧。

```powershell
dotnet build Eota.sln -c Release --no-restore
dotnet test Eota.sln -c Release --no-build --no-restore
dotnet format Eota.sln --verify-no-changes --no-restore
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify tests/Fixtures/InitialReplay/Cards tests/Fixtures/InitialReplay/protocol-v0.json tests/Fixtures/InitialReplay/initial.replay.json
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify-match tests/Fixtures/P4Match/Cards tests/Fixtures/P4Match/protocol-v0.json tests/Fixtures/P4Match/match.replay.json
dotnet run --project tools/Eota.ReplayCli -c Release --no-build -- verify-match tests/Fixtures/P4Match/Cards tests/Fixtures/P4CenterMovement/protocol-v0.json tests/Fixtures/P4CenterMovement/match.replay.json
```

Release 构建 0 warning / 0 error；159 项测试全部通过（Kernel 97、Algebra 1、Content.Compiler 24、Architecture 5、Server.Integration 32）。格式检查与开局、完整对局、奇数中心移动三份回放核验通过，既有 golden hash 保持一致。

## 后续范围

P5 使用完整视图帧，恢复方式为重新连接并取得快照；没有历史 delta 补发。开发席位由 query 参数选择，尚无生产鉴权。内存日志与幂等键不跨进程重启持久化，通用检查点、生产身份与席位认证、补发窗口、超时系统命令、限流和运维诊断按 P10 完成。

Godot UI 在 P9 接入；Effect IR、充能、临时效果和触发/Continuation 调度仍属于 P6/P7。P5 的网络通路不表示这些玩法能力已经实现。
